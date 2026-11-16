# 诊断包索引（2026-11-16，第二轮收敛后）

## 先读（按顺序）

1. `ACAD2022-DIAGNOSTICS.md`：两阶段问题总报告。**开头有“审查后归因更正”**：第三批是用户主动终止，不是 CAD 崩溃或 Agent 自主失败。
2. `WALL-BUGS-FIXED-AND-WORKAROUNDS.md`：砖墙任务实际改了什么、绕过什么、还剩哪些底座 Bug；含 fix10→fix13 的真实构建迭代。
3. `V11-SKILL-ACCEPTANCE-EVIDENCE.md`：第一阶段（引线标注 Skill 集成、真实案例、旧标注重做、验收）的成果、卡点、难度、Bug。
4. `BUILD-MAPPING.md`：client / acad22 插件 / net8 服务端分别由哪些源码构建，fix13 指纹与改动对应。
5. `MANIFEST.json`：源码快照逐文件 SHA256（276 文件，约 2.21 MB）。
6. `source-snapshot/`：当前本机改造后的真实源码（client、shared、plugin-acad22/24/26、plugin-journal-net8、server、tools）。
7. `evidence/d02/D-02-save-identity.json`：保存身份修复的最小脱敏请求/响应证据（正向通过；反向未跑）。

## 证据目录

- `evidence/wall-errors.txt`：本次砖墙逐条原始错误文本。
- `evidence/wall-request-summary.json`：四个工作区的请求 phase/verification/error/saved_path/size/sha。
- `evidence/wall-plan-01..04.json`：四批绘图计划原文（第一轮版本；修正后的计划见本地 `v11-wall` 目录与诊断正文）。
- `evidence/wall-batch3-server-tail.txt`：第三批 dwg_job_get 轮询尾部。
- `evidence/v11-report.md` / `v11-buglog.md`：V1.1 原始报告与 Bug 记录。
- `evidence/v11-data/`：35 组范围清单（`scope_35.json`）、计划（`plan-new-35.json`）、重开核验（`verify-new-35.json`）、P1-A 结果（`p1a-result.json`）、三帧 review。
- `evidence/v11-checkpoint.txt`：源码与三阶段 DWG 的 SHA256 检查点。
- `evidence/screenshots/`：before|cleared|after 对比图、after 全图、ED08/ED04/ED02 放大图。
- `evidence/environment.txt`：运行时包、插件 mvid/DLL sha、模板、实例基线。

## 新增（第二轮）

- `source-snapshot/` + `MANIFEST.json` + `BUILD-MAPPING.md`：补真实源码与构建产物对应关系，不再用旧 main 代表最新版本。
- `evidence/d02/D-02-save-identity.json`：D-02 的最小脱敏请求/响应，明确“正向通过、反向未跑”。
- 砖墙四批成品与 PDF 证据（本地）：
  - `s2fix.dwg` 32,021 B，SHA256 `7E71198C...D43B44`
  - `s3.dwg` 36,337 B，SHA256 `9ECA9864...5DE080`
  - `s4.dwg` 37,620 B，SHA256 `151FB50B...12BE7F`
  - `s11.dwg` 37,549 B（米制/A3/1:0.02）
  - `s12.dwg` 38,006 B（`WALL_DIM.Dimlfac=1000`）
  - `wall-section.pdf` 20,647 B，A3 1191×842 pt，尺寸文字正确

## 事实边界

- V1.1：35/35 重开核验通过；ED04 仍 2 条保守候选；P1-B 负向用例、P2-C/P2-D 未完成。
- 砖墙：四批几何均已保存（`s4.dwg`），A3 PDF 已产出且尺寸正确；但 `plot_pdf` 的服务端最终判定仍 failed，SaveAs 后同会话不能连续。保存链路正向 + 三项反向回归已通过；仅“finalize 查询失败但文件已存在”未实测。
- 第三批旧归因“CAD 退出/丢失”作废；那次是用户主动终止、保存未执行。后续重跑已正常保存。
- 任何“已完成”以 DWG 文件、journal 记录、重开读回三者同时存在为准。

需要复核时按主报告第 3、4 节逐项打开，不要只看摘要。
