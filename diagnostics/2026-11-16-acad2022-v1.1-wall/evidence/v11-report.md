# ACAD2022-V1.1 实战验收候选版报告

生成时间：2026-11-16 01:50（本地）
基线：ACAD2022-V1.0-candidate / 评审提交 24bf31d
本轮版本：1.6.1-acad2022.v11.3（包 20261115-172431）

## 管理摘要

V1.1 已完成南一路「路施25」真实案例的旧标注重做闭环：35 组（第一处 15、第二处 20）旧标注全部可恢复归档到 `V1.1-旧标注归档-不出图`，生成 `cleared.dwg`；Agent 按 Skill v0.6 规则独立重排 35 组并分批原生落图，生成 `after.dwg`。落图 3 批共 35 个 callout，全部 `verification=passed` 并保存成盘；在全新 AutoCAD 2022 进程中只读重开逐组核验，35/35 通过：普通三点 LEADER、单行 MTEXT 关联反应器、尾段水平、字框位置、字体 `_KCYD_DIM`、字高 1.5、文字色 7、引线色 2、`AnnotationOffset = C − 插入点`、圆点与填充齐全。几何预检 ED08、ED02 零冲突；ED04 仍余 2 条“引线邻近字框”保守候选，已在图面标注待人工判读。P1-A 已实现 2022 运行时动态解析 `acdb*.dll` 的 attachAnnotation 入口，并在 2022 上强制走原生附着实测通过；P1-B 已收紧保存证据（路径一致 + saved_size/saved_sha256 复核），新保存记录已带这两个字段。当前可直接用于受控实战试用；尚不建议作为生产默认。遗留：保存到新路径后会话绑定失效（需换会话继续）、PDF/CTB 出图未接 2022 路由、原生改字稳定性测试与 P2-C/P2-D 补强未完成。

## 一、案例与基线

- 原图：`D:\360云盘\...\03南一路_路施25道路拆除平面图_路口修订20261015After human optimized.dwg`（1,130,162 B，sha256 前 16 位 `4030366378bf401a`），只读保护。
- 副本：`before.dwg`，与原图字节相等。
- 35 组确认：6 个代理图框（`PP-PM-BJCTK`）内按 x 分组，ED08=15、ED04=14、ED02=6，合计 35，与 2026-10-15 认可成果数量一致；其余 ED05/06/07 共 31 组不属于认可范围，未动。

## 二、旧标注归档（cleared）

- 归档层：`V1.1-旧标注归档-不出图`（off + frozen + 非打印）。
- 35 组共 70 个 leader+text 实体全部归档，另含可追溯 handle、摘要、锚点、字框与样式记录（`scope_35.json`）。
- 分两个工作区分两批 apply：`v11-arch-a1`（30 targets）、`v11-arch-a2d`（40 targets），journal 均 `committed/verified`。
- 结果：`cleared.dwg` 1,076,771 B；新会话核验 `1D4A1/1D4A0/1D4A5` 等均在归档层，可见图面已无旧面积标注。
- 说明：archive 是可恢复归档，不是物理删除；旧圆点 3 个中仅 1 个随组归档，另 2 个仍留原层（遗留细节，未单独处理）。

## 三、Agent 独立重规划

- 依据：安装版 Skill `cad-callout-pair-layout` v0.6（HEAD `b4aa1a2`），读 SKILL.zh-CN 与 agent-planning / input-and-acceptance / transport-contract / nanyi-field-example。
- 策略：按帧读取内框（代理框）、按旧文字相对锚点上下分侧，同侧按 x 降序阶梯架，行距 2.9 m；文字优先锚点右侧 4 m（BottomLeft），出框改左侧（BottomRight）；C 与 B 精确构造，`annotation_offset = C − 插入点`；圆点 r=0.1875，引线/圆点色 2、文字色 7、`_KCYD_DIM`、`HyDim`、字高 1.5、gap 0、dimtad 0。
- 预检：`review_layout.py` 对三帧分别检查。ED08、ED02 为 `no_conflicts_in_supplied_geometry`；ED04 经多轮调整后仍有 2 条 `leader_text_candidate`（`co-1D7DA`↔`co-1EB26`、`co-1EB26`↔`co-1D811`），无文字重叠、无引线交叉、无越框。该 2 条保留为图面待判候选，未伪称清零。
- 计划：`plan-new-35.json`（35 个 callout step），拆为 `plan-A1`(15)、`plan-A2a`(14)、`plan-A2b`(6)。

## 四、原生落图（after）

- 批次：A1 → `after-a1.dwg` 1,082,916 B；A2a → `after-a2.dwg` 1,090,978 B；A2b → `after.dwg` 1,094,472 B。
- 每批 `apply --save-to`：journal `phase=completed`、`verification=passed`，保存记录含 `saved_path / saved_size / saved_sha256`。
- 对象统计（独立重开会话）：引线层 Leader 66（旧 31 + 新 35）、文字层 MText 66、圆点 38、填充 38。
- 逐组核验（`v11_verify_new.py`，按锚点匹配新 leader）：35/35 通过，检查项包括三点顶点、尾段水平（v1.y=v2.y）、`annotation_reactor_present=true`、关联目标为 MText、文字内容与计划一致、图层/样式/字高/颜色、`dimtad=0`、`gap=0`、偏移等于 C 减插入点。
- 重开：核验所用为全新 AutoCAD 2022 进程（PID 31196，只读会话，从 after.dwg 复制打开），非落图进程。

## 五、P1-A / P1-B 收敛

- P1-A：`FoundationPlanWriteHandler` 改为运行时按 `Application.Version` 解析 `acdb{major}.dll`，兜底 acdb24/acdb25；`LoadLibraryW`+`GetProcAddress`+委托缓存；失败给出 `callout_annotation_attach_entry_unavailable:<modules>`，成功校验 status、`leader.Annotation`、持久反应器。为可实测，`set_callout` 新增 `force_native_attach`（必须与 `repair_association` 同用）。2022 实测：对已有关联组强制原生附着，`verification=passed`，反应器与 offset 保持，证明 2022 上动态入口可达。
- P1-B：`HasCompletedSaveAfterCore` 改为失败关闭：写侧 drawing_path 缺失即 false；两侧路径 OrdinalIgnoreCase 相等；result_json 必须含 saved_path/saved_size/saved_sha256；新增文件存在/长度/SHA256 校验（FileShare.ReadWrite）。保存处理器返回新字段。实测三批保存记录均带 size+sha；旧 journal 缺字段不再获得保存信用。负向用例（写侧缺路径、文件被移走/覆盖、同库多图纸）尚未跑完，列为未验证。

## 六、本轮修复的缺陷

详见 `ACAD2022-V1.1-BUGLOG.md`：callout 创建缺 text_layer/annotation_offset、offset 3D 点被 2D 校验拒绝、net8 服务端 schema 未同步、`--save-to` 后会话绑定失效、`Number(step,"gap")` 编译错误。

## 七、未完成与限制（诚实清单）

1. `--save-to` 到新路径后，服务端进程的环境变量仍绑定旧 drawing，后续 status/apply 报 `drawing_path_mismatch`；客户端已同步 session 路径，但根治需重启服务端并重建绑定。当前以“每批一个新会话、从上一批保存文件继续”的方式绕过。
2. PDF/CTB 出图未接入 2022 计划路由，前后对比使用 CAD 原生视图截图（同一窗口坐标），未出 PDF。
3. 原生改字再恢复的稳定性冒烟测试未执行（无受控文本编辑 op），关联稳定性以读回反应器、offset、尾段水平为依据。
4. P2-C（run_regression 断言加强、独立关闭重开、区分预提交拒绝与失败作业）与 P2-D（程序集解析影响面审查）未补强。
5. ED04 两条引线邻近字框候选、两个遗留旧圆点，需人工图面判读。
6. 源码检查点已建本地 SHA256 清单，未获授权推远程分支。

## 八、交付位置

`F:\AI\cad-agent-foundation-acad2022\deliverables\ACAD2022-V1.1\`：before/cleared/after 三阶段 DWG、三组 before|cleared|after 并排对比图、after 分区截图、35 组核验报告、计划与清单、P1-A 测试结果、本报告与 Bug 记录。
