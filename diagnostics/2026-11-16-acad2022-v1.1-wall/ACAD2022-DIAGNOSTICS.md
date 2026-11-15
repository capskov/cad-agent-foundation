# ACAD2022 两阶段实战疑难诊断包

生成时间：2026-11-16（本地）
范围：①V1.1 南一路旧标注重做阶段；②本次“空白 DWG + 砖砌围墙截面 + A3 图框 + PDF”实战。
结论先行：V1.1 的业务闭环已完成并有 35/35 重开核验；本轮砖墙图完成了图框、墙体、填充与两批保存，但第三批（地面/土体/尺寸）在补保存时随 CAD 进程退出而丢失，**PDF 尚未产出**。两个阶段暴露的核心问题不是“画不出来”，而是**写入 fence / 保存链路与客户端错误判定不一致**，以及**计划接口的隐式限制没有文档化**。

## 1. 两阶段基线

| 项 | V1.1 | 本轮砖墙 |
| --- | --- | --- |
| 数据源 | 南一路路施25 真实 DWG 副本 | 空白模板 acad.dwt 新建 |
| 产物 | before/cleared/after 三阶段 DWG + 截图 | 图框+墙体 DWG（s2.dwg），未完成 |
| 原生能力 | callout/archive/save | layer/polyline/hatch/text/dimension/page_setup/plot_pdf |
| 结果 | 35/35 重开核验通过 | 第1、2批保存成功；第3批丢失 |

## 2. 问题清单（按影响排序）

### D-01 写入 fence 与保存必须同一次调用，否则后续请求全部被挡住
- 前因：2022 走“先写、后保存、再 finalize”的两段式；apply 不带 `--save-to` 时 fence 保持 held。
- 现象：第一次 apply 返回 `verification_pending / verification=passed`，`next_action=verified_not_persisted`；下一次 apply（即使换新计划）被“held fence”拒绝。
- 后果：如果误判为“已完成”，会反复提交、反复被拒；本轮因此废弃了第一个工作区（v11-wall）。
- 证据：`evidence/wall-errors.txt` 第 3、4、5 条；`wall-request-summary.json` 中 v11-wall 的 `verification_pending`。
- 建议：客户端在“verification_pending 且无 save_to”时直接返回明确失败码，而不是继续允许下一次 apply；或在 apply 内强制要求 save_to（2022 模式）。

### D-02 保存作业成功写盘，客户端却以 “Unknown request record” 报错并退出 1
- 前因：`persist()` 用新的 `save-<uuid>` 请求 ID 调 `dwg_foundation_save_drawing`，随后又用原写请求 ID 去 finalize；两步的 context/记录归属不一致。
- 现象：CLI 返回 `{"error":"Unknown request record; pass a request ID that this workspace submitted"}`，退出码 1；但目标 DWG 实际已生成（s2.dwg 32,021 B，mtime 07:11:51）。
- 后果：脚本/Agent 会把“已保存”当失败，重复尝试；自动化无法可靠判断持久化结果。
- 证据：`v11-wall3` 的 `requests/save-*.json` 中 `last_reply.job.phase=completed`、`result_json` 含 `saved_path/saved_size/saved_sha256`，而 CLI stdout 仍是 Unknown request record。
- 建议：finalize 必须复用写请求的 context；或保存成功后以“文件事实（exists+size+sha）”作为成功判据，请求记录缺失只降级为 warning。

### D-03 保存后同一会话 `drawing_path_mismatch`（V1.1 BUG-05 未根治）
- 前因：SaveAs 改变文档路径，服务端环境变量与绑定快照仍固定旧 `work.dwg`。
- 现象：保存成功后 `stop` / 下一次 `apply` 都报 `drawing_path_mismatch`；本轮两次遇到。
- 后果：每批必须“停实例→新工作区→从保存文件 start”，4 批图面=4 次会话；长任务被切成碎片，任一环节进程退出就丢未保存批次（本次第三批即如此）。
- 证据：`evidence/v11-buglog.md` BUG-05；`evidence/wall-errors.txt` 第 5、6 条。
- 建议：保存后自动重启服务端并重建绑定，或提供 `rebind --drawing`；在这之前，客户端应明确提示“保存后必须新会话”，并在 `status` 中把 mismatch 标为预期状态。

### D-04 单计划 16 步硬上限 + 步骤 ID 必须 ASCII，文档与 Skill 未提示
- 现象 A：39 步计划被 `1..16 steps required` 拒绝；
- 现象 B：图层名直接拼进 id（`layer-图框`）被 `step IDs must be unique identifiers` 拒绝，规则是 `^[A-Za-z][A-Za-z0-9_-]*$`。
- 后果：中文工程图按“图层/构件”自然命名的计划会整批被拒；需要重写 id 与拆批。
- 证据：`evidence/wall-errors.txt` 第 1、2 条；`FoundationDrawingPlan.cs:43`。
- 建议：把 16 步/ASCII id 写进客户端 `apply --help` 与错误信息；允许 id 与显示名分离（display_name）。

### D-05 前台 15 秒等待会把长任务转后台/被停，未保存批次会丢
- 现象：`apply --save-to` 超过 15s 被移到后台；随后 agent 侧任务被 stop，CAD 进程退出，第三批内存成果丢失。
- 后果：长计划+保存组合在自动化里不可靠；用户看到“提交成功”但文件不存在。
- 建议：长任务显式 `run_in_background=true` 并轮询 `TaskOutput`；客户端提供幂等的“pending 保存恢复”命令，进程退出后能凭 journal 判断是否需要重放（当前禁止盲目重放）。

### D-06 空白模板选错会被 plot_pdf 以单位拒绝
- 事实：`plot_pdf` 要求 `INSUNITS∈{0,6}`；`acadISO.dwt` 为毫米（4），会被拒；`acad.dwt` 为 unitless（0），被当作米。
- 后果：按毫米建模的图无法直接出 PDF；按米建模时 A3 1:20 图框必须画成 8.4×5.94 m。
- 建议：模板选择写进出图前置检查；或在 page_setup 前提供受控 `INSUNITS` 设置能力。

### D-07 PDF 能力“存在但未验证”，与 V1.1 报告表述有出入
- 事实：源码已有 `page_setup` / `plot_pdf` / `window_settings` 与 PDF PC3 校验；V1.1 报告写“PDF/CTB 出图未接入 2022 计划路由”。
- 现状：本轮已能提交尺寸与打印 op，但未走到 plot_pdf（第三批丢失，未保存）。
- 建议：下一轮先用一张最小图验证 `page_setup(Model, A3, monochrome.ctb, extents, 1:20) → 重新 bind 取 digest → plot_pdf` 的完整链路，再并入砖墙任务。

### D-08 V1.1 遗留的代码/测试缺口
- P1-A：2022 动态入口已实测成功，但“入口不可用/attach 返回非 0”的负向分支未构造（BUGLOG 附注）。
- P1-B：负向用例（写侧缺路径、文件移走/覆盖、同库多图纸）未跑完（REPORT 五）。
- P2-C/P2-D：回归断言、独立关闭重开、程序集解析影响面未补强（任务 #21 pending）。
- ED04 仍有 2 条 `leader_text_candidate` 待人工判读；2 个旧圆点未归档。
- 这些不是本轮砖墙引入，但会让“V1.1 完成”在外审时被质疑。

### D-09 外部评审分支缺少完整 C# 源码
- 反馈：`acad2022-v1.0-review` 分支主要是评审资料/测试脚本/运行时压缩包，不是完整 C# 源码分支；外部智能体只能审客户端与测试逻辑，无法审底层 C#。
- 影响：GPT-6 类审查会给出“有条件放行”，无法覆盖 Foundation 插件实现。
- 建议：在评审分支补 `source/` 完整快照（或明确指向已存在的 source 目录提交），并在 README 标注 commit 与 MANIFEST。

## 3. 本轮砖墙任务执行记录（可复核）

1. 空白源：复制 `acad.dwt` → `blank.dwg`（64,148 B），`start --source blank.dwg --write --allow-save --headless` 成功（PID 21996）。
2. 设计（米制，1:20，A3=8.4×5.94）：外框/内框、标题栏；C15 条形基础 800×300；370 砖放脚；240 砖墙 2400 高；双面 20 抹面；C25 压顶 60；地面线+土体填充；尺寸 2400/240/800/370/300/60；6 组 callout。
3. 第 1 批（15 步：7 图层+2 样式+3 图框+3 文字）：提交 `verification=passed`，因未带 save_to 进入 fence pending（D-01）。
4. 第 2 批（墙/基础/压顶/3 个填充）：新会话从第 1 批保存文件继续，apply 提交成功并生成 `s2.dwg`（32,021 B），但 CLI 报 Unknown request record（D-02）。
5. 第 3 批（地面线/土体 2 填充/6 尺寸，14 步）：`verification=passed` 后补保存时 CAD 退出，`s3.dwg` 未生成（D-05）。
6. 第 4 批（6 个 callout）与 page_setup/plot_pdf **未执行**，因此无最终 PDF。

## 4. 现场证据

- `evidence/wall-request-summary.json`：v11-wall / wall2 / wall3 / wall4 全部请求的 phase/verification/error/saved_path/size/sha。
- `evidence/wall-errors.txt`：逐条原始错误文本。
- `evidence/wall-batch3-server-tail.txt`：第三批 dwg_job_get 轮询尾部。
- `evidence/wall-plan-01..04.json`：四批计划原文（可直接重放，但需先解决 D-01/D-02）。
- `evidence/v11-buglog.md` / `v11-report.md`：V1.1 原始报告与 Bug 记录。
- `evidence/environment.txt`：包/插件/模板/实例基线。

## 5. 给 GPT-6 的审查问题（请直接回答）

1. D-02 的“保存成功但 finalize 报 Unknown request record”应归为客户端 bug、服务端记录归属 bug，还是两者皆有？最小修复点在哪一层？
2. D-03 的根治方案（保存后自动重启+rebind）是否会破坏现有 fence/身份校验？有无更小的改法？
3. 16 步上限与 ASCII id 是否应作为公开契约，还是提高上限/支持中文 id？哪个风险更低？
4. V1.1 的 P1-A/P1-B 负向分支与 P2-C/P2-D，是否足以阻塞“生产默认”评级？
5. 评审分支补 `source/` 快照时，如何避免与本地更新代码不一致（commit 绑定 + MANIFEST 是否足够）？

## 6. 诚实边界

- 本轮没有最终 PDF，不能宣称“砖墙图已交付”。
- V1.1 的 35/35 是重开核验结论，不等于零 Bug；ED04 两条候选仍待人工判读。
- 任何“已完成”均以 DWG 文件、journal 记录、重开读回三者同时存在为准；本轮第三批不满足。
