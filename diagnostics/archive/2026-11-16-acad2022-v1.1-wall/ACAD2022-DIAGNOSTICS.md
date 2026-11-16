# ACAD2022 两阶段实战疑难诊断包

生成时间：2026-11-16（本地；第二轮收敛后更新）
范围：①V1.1 南一路旧标注重做阶段；②本次“空白 DWG + 砖砌围墙截面 + A3 图框 + PDF”实战。

> **审查后归因更正（2026-11-16 第二轮，优先于下文历史表述）**
> 1. 第三批不是 CAD 崩溃，也不是 Agent 自主执行失败。第三批 15 步已 `verification=passed`，但未保存；随后是**用户主动终止本轮任务**，保存未执行。以 `s2fix.dwg` 为输入重跑后，第三批已保存为 `s3.dwg`（36,337 B）；第四批已保存为 `s4.dwg`（37,620 B）。
> 2. `Unknown request record` 已按“请求身份传递”修复，并通过正向回归；反向回归尚未执行，见第 2 节 D-02 的“修复后状态”。
> 3. PDF 已产出：A3、1191×842 pt、20,647 B，尺寸文字已由 `Dimlfac=1000` 修正为 2400/240/60/400/300/800/370；但 `plot_pdf` 的服务端最终判定仍为 failed，产物可用不等于链路 passed。
> 4. 下文保留的是**第一轮现场记录**，其中“CAD 退出/丢失”“PDF 尚未产出”“未改插件代码”等表述已被本轮事实取代；阅读时以本节、第 2 节 D-02 的“修复后状态”和第 3 节执行记录为准。

结论先行：V1.1 的业务闭环已完成并有 35/35 重开核验；砖墙图已完成四批几何、米制 A3 页面设置与可见 PDF，尺寸文字正确。两个阶段暴露的核心问题不是“画不出来”，而是**写入 fence / 保存链路与客户端错误判定不一致**、**SaveAs 后会话不能连续**、**校验器分散在服务端与插件导致“只修一半”**，以及**计划接口的隐式限制没有文档化**。

## 1. 两阶段基线

| 项 | V1.1 | 本轮砖墙 |
| --- | --- | --- |
| 数据源 | 南一路路施25 真实 DWG 副本 | 空白模板 acad.dwt 新建 |
| 产物 | before/cleared/after 三阶段 DWG + 截图 | 四批 DWG（s2fix/s3/s4）+ 页面设置 s11 + 尺寸修正 s12 + A3 PDF |
| 原生能力 | callout/archive/save | layer/polyline/hatch/text/dimension/page_setup/plot_pdf |
| 结果 | 35/35 重开核验通过 | 四批均已保存；PDF 可见且尺寸正确；plot_pdf 服务端判定未收口 |

## 2. 问题清单（按影响排序）

### D-01 写入 fence 与保存必须同一次调用，否则后续请求全部被挡住
- 前因：2022 走“先写、后保存、再 finalize”的两段式；apply 不带 `--save-to` 时 fence 保持 held。
- 现象：第一次 apply 返回 `verification_pending / verification=passed`，`next_action=verified_not_persisted`；下一次 apply（即使换新计划）被“held fence”拒绝。
- 后果：如果误判为“已完成”，会反复提交、反复被拒；本轮因此废弃了第一个工作区（v11-wall）。
- 证据：`evidence/wall-errors.txt` 第 3、4、5 条；`wall-request-summary.json` 中 v11-wall 的 `verification_pending`。
- 建议：客户端在“verification_pending 且无 save_to”时直接返回明确失败码，而不是继续允许下一次 apply；或在 apply 内强制要求 save_to（2022 模式）。

### D-02 保存作业成功写盘，客户端却以 “Unknown request record” 报错并退出 1（已修复，正向回归通过）
- 前因：`apply_with_optional_save()` 在未显式传 `request_id` 时，`apply()` 内部会生成 ID，但后续 finalize 查询仍使用原始 `None`；保存作业与 finalize 的请求身份不一致。`persist()` 另用 `save-<uuid>`，进一步放大 context/记录归属不一致。
- 现象：CLI 返回 `{"error":"Unknown request record; pass a request ID that this workspace submitted"}`，退出码 1；但目标 DWG 实际已生成（旧 `s2.dwg` 32,021 B）。
- 修复：`write_id = write_reply.get('request_id') or request_id`，断言非空；finalize 查询与 context 都使用 `write_id`。源码在 `client/foundation.py`，运行时从 `20261116-fix1` 起。
- 修复后状态：`v11-wallA` 正向回归通过——第 2 批 9 步 `verification=passed`、`persisted=true`、fence 释放、`save_reply.saved_path` 指向 `s2fix.dwg`，`Unknown request record` 未再出现。
- 反向回归已补跑（`v11-neg1b/2b/3b`）：
  - 无 `save_to`：`verification_pending`、`verification=passed`、`persisted=false`、fence 未释放，`next_action=verified_not_persisted`；第二次 apply 被 fence 拒绝（`fence_held_by_earlier_unpersisted_write`）。
  - 重复 request id：只生成 1 个请求记录，第二次返回原记录，未重放；模型空间仅 1 个 Polyline（句柄 `1B2`）。
  - 保存失败（目标目录不存在）：save job `phase=failed`、`save_drawing_failed`、`persisted=false`、fence 保持 held、文件不存在，`next_action=save_failed`。
- 仍未构造的场景：finalize 查询本身失败但文件已存在。代码结构上 `persisted` 只在 finalize 查询返回服务器确认后置真，不以文件存在为判据；但该极端场景没有实测。
- 证据：`evidence/d02/D-02-save-identity.json`、`negative-regression.json`、`neg-dup-request.json`、`neg-savefail-request.json`。
- 证据：`v11-wallA` 请求记录；`WALL-BUGS-FIXED-AND-WORKAROUNDS.md` 1.7。

### D-03 保存后同一会话 `drawing_path_mismatch`（V1.1 BUG-05 未根治）
- 前因：SaveAs 改变文档路径，服务端环境变量与绑定快照仍固定旧 `work.dwg`。
- 实测结论：单独 `save` 不会回头 finalize 原写入，会被 fence 正确挡住；同一请求 + `--save-to` 可保存并 finalize，但保存到新路径后旧会话出现 `drawing_path_mismatch`；同一工作区在 SaveAs 后重新启动还会 fail-closed（`D1 ownership ... no matching ledger`）；原地覆盖当前 `work.dwg` 触发 `eFilerError`。
- 当前可行路径：**SaveAs 到新路径 → 换新工作区继续**。未绕过 fence，未粗暴改写身份绑定，也没有在未完成任务时自动换工作区写同一图纸；但尚未形成可复用脚本。
- 后果：每批一次会话；长任务被切成碎片。第三批的未保存不是崩溃，而是用户主动终止（见 D-05 更正）。
- 证据：`evidence/v11-buglog.md` BUG-05；`evidence/wall-errors.txt` 第 5、6 条；本轮 fix13 的 SaveAs 记录。
- 建议：保存后自动重启服务端并重建绑定，或提供 `rebind --drawing`；在这之前，客户端应明确提示“保存后必须新会话”，并在 `status` 中把 mismatch 标为预期状态。

### D-04 单计划 16 步硬上限 + 步骤 ID 必须 ASCII，文档与 Skill 未提示
- 现象 A：39 步计划被 `1..16 steps required` 拒绝；
- 现象 B：图层名直接拼进 id（`layer-图框`）被 `step IDs must be unique identifiers` 拒绝，规则是 `^[A-Za-z][A-Za-z0-9_-]*$`。
- 后果：中文工程图按“图层/构件”自然命名的计划会整批被拒；需要重写 id 与拆批。
- 证据：`evidence/wall-errors.txt` 第 1、2 条；`FoundationDrawingPlan.cs:43`。
- 契约结论：16 步/ASCII id 按既有接口契约处理，**不视为 Bug**；应补明确错误提示与自动分批策略，而不是放宽契约。首次未保存导致 fence 持有同样属于设计行为，应改善用户引导。

### D-05 长任务 15 秒前台转后台，以及第三批归因更正
- **归因更正：第三批不是 CAD 崩溃，也不是 Agent 自主执行失败。** 第三批 15 步已 `verification=passed`，但尚未保存；随后是**用户主动终止本轮任务**，保存未执行，所以 `s3.dwg` 未生成。后续以 `s2fix.dwg` 为输入重跑，第三批已正常保存为 `s3.dwg`（36,337 B）。
- 平台限制：15 秒前台转后台属于任务执行环境限制，不是插件 Bug。应采用可靠的异步轮询与持久化检查点，不要求突破平台限制。
- 建议：长任务显式 `run_in_background=true` 并轮询 `TaskOutput`；客户端提供幂等的“pending 保存恢复”命令，进程退出后能凭 journal 判断是否需要重放（当前禁止盲目重放）。

### D-06 空白模板与出图单位/比例（已解决）
- 事实：`acad.dwt` 实测 `INSUNITS=1`（英寸），不是 unitless；`plot_pdf` 最初只接受 0/6，触发 `file_output_requires_metres_or_explicit_unitless_metres`。
- 修复：`page_setup` 声明 `units="metres"`；`UnitMetadataMatches` 对带 page_setup 的米制计划接受 observed=1；提交后按 `units=="metres"` 设 `INSUNITS=6`。`plot_pdf` 的守卫已放宽到 0/1/4/6。该路径在 fix9 生效。
- 比例坑：米制模型若写 `1:20`，AutoCAD 把 8.4 m 当成 8.4 个图纸单位，PDF 只有中心一点；必须写 `1:0.02`。`s11.dwg` 按 1:0.02 保存后，8.4×5.94 m 正好铺满 A3。
- 证据：`s10.dwg` / `s11.dwg`；A3 PDF 1191×842 pt。

### D-07 PDF 能力已实际产出，但服务端最终判定未收口
- 事实：源码已有 `page_setup` / `plot_pdf` / `window_settings` 与 PDF PC3 校验；V1.1 报告曾写“PDF/CTB 出图未接入 2022 计划路由”。
- 现状：`v11-wallU` 产出可见 A3 PDF（20,193 B），尺寸文字为 0/2/1；fix13 把 `WALL_DIM` 的 `Dimlfac` 改为 1000 后，`v11-wallY` 产出 20,647 B PDF，尺寸文字正确为 2400/240/60/400/300/800/370。
- 未收口：客户端仍报 `foundation_plan_readback_mismatch,plugin_stage_not_passed`，`plugin_verification=passed`、`server_verification=failed`、`persisted=false`。PDF 产物与 header 校验可用，但**不能宣称 plot_pdf 链路 passed**。
- 证据：`v11-wallY/drawings/outputs/wall-section.pdf`；`v11_wall_pdf_check6.png`。

### D-08 V1.1 遗留的代码/测试缺口
- P1-A：2022 动态入口已实测成功，但“入口不可用/attach 返回非 0”的负向分支未构造（BUGLOG 附注）。
- P1-B：负向用例（写侧缺路径、文件移走/覆盖、同库多图纸）未跑完（REPORT 五）。
- P2-C/P2-D：回归断言、独立关闭重开、程序集解析影响面未补强（任务 #21 pending）。
- ED04 仍有 2 条 `leader_text_candidate` 待人工判读；2 个旧圆点未归档。
- 这些不是本轮砖墙引入，但会让“V1.1 完成”在外审时被质疑。

### D-09 外部评审分支缺少完整 C# 源码
- 反馈：`acad2022-v1.0-review` 分支主要是评审资料/测试脚本/运行时压缩包，不是完整 C# 源码分支；外部智能体只能审客户端与测试逻辑，无法审底层 C#。
- 影响：GPT-6 类审查会给出“有条件放行”，无法覆盖 Foundation 插件实现。
- 当前处理：本分支已补 `source-snapshot/`（client / shared / plugin-acad22 / plugin-acad26 / server 的真实源码）、`MANIFEST.json`、`BUILD-MAPPING.md` 与 `evidence/d02/` 最小脱敏请求响应；commit 与 MANIFEST 绑定，见分支根 `SOURCE-SNAPSHOT-README.md`。

## 3. 本轮砖墙任务执行记录（可复核）

1. 空白源：复制 `acad.dwt` → `blank.dwg`（64,148 B），`start --source blank.dwg --write --allow-save --headless` 成功（PID 21996）。
2. 设计（米制，按 1:0.02 出图，A3=8.4×5.94 m）：外框/内框、标题栏；C15 条形基础 800×300；370 砖放脚；240 砖墙 2400 高；双面 20 抹面；C25 压顶 60；地面线+土体填充；尺寸 2400/240/800/370/300/60；6 组 callout。
3. 第 1 批（15 步：7 图层+2 样式+3 图框+3 文字）：提交 `verification=passed`，因未带 save_to 进入 fence pending（D-01）。
4. 第 2 批（墙/基础/压顶/3 个填充）：新会话从第 1 批保存文件继续；修正后重新提交并保存为 `s2fix.dwg`（32,021 B），`persisted=true`，`Unknown request record` 未再出现。
5. 第 3 批（地面线/土体 2 填充/7 尺寸，15 步）：从 `s2fix.dwg` 起，提交 `verification=passed`，保存为 `s3.dwg`（36,337 B）。**此前“CAD 退出导致丢失”的归因作废，那一次是用户主动终止、保存未执行。**
6. 第 4 批（6 个 callout，6 步）：因 SaveAs 后同会话不可连续，换新工作区从 `s3.dwg` 起，保存为 `s4.dwg`（37,620 B）。
7. 页面设置：`page_setup(Model, A3, monochrome.ctb, extents, 1:0.02)`，保存为 `s10.dwg` / `s11.dwg`；`s11.dwg` 为最新页面版本。
8. 尺寸修正与出图：fix13 下把 `WALL_DIM` 的 `Dimlfac` 改为 1000，保存为 `s12.dwg`（38,006 B）；新工作区重开并 `plot_pdf`，产出 `wall-section.pdf`（20,647 B，A3，1191×842 pt），尺寸文字正确；服务端最终判定仍 failed（D-07）。

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

- PDF 已产出且尺寸文字正确，但 `plot_pdf` 的服务端最终判定仍 failed；不能宣称“出图链路 passed”。
- SaveAs 后同会话不能连续，当前只能“另存新路径 + 新工作区”；尚未形成可复用脚本。
- 保存链路正向回归与三项反向回归均已通过；仅“finalize 查询失败但文件已存在”的极端场景未实测，不能据此宣称该场景已验收。
- V1.1 的 35/35 是重开核验结论，不等于零 Bug；ED04 两条候选仍待人工判读。
- 任何“已完成”均以 DWG 文件、journal 记录、重开读回三者同时存在为准；第三批现在满足，但此前那次用户主动终止不满足。
