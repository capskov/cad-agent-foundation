# 诊断包索引（2026-11-16）

## 先读

- `ACAD2022-DIAGNOSTICS.md`：两阶段问题总报告（V1.1 + 砖墙），含根因、证据、给 GPT-6 的 5 个问题。
- `V11-SKILL-ACCEPTANCE-EVIDENCE.md`：第一阶段（引线标注 Skill 集成/真实案例/旧标注重做/验收）的成果、卡点、难度、Bug。
- `WALL-BUGS-FIXED-AND-WORKAROUNDS.md`：砖墙任务里我实际改了什么、绕了什么、还剩哪些底座 Bug。

## 证据目录

- `evidence/wall-errors.txt`：本次砖墙逐条原始错误文本。
- `evidence/wall-request-summary.json`：四个工作区的请求 phase/verification/error/saved_path/size/sha。
- `evidence/wall-plan-01..04.json`：四批绘图计划原文。
- `evidence/wall-batch3-server-tail.txt`：第三批 dwg_job_get 轮询尾部。
- `evidence/v11-report.md` / `v11-buglog.md`：V1.1 原始报告与 Bug 记录。
- `evidence/v11-data/`：35 组范围清单（`scope_35.json`）、计划（`plan-new-35.json`）、重开核验（`verify-new-35.json`）、P1-A 结果（`p1a-result.json`）、三帧 review。
- `evidence/v11-checkpoint.txt`：源码与三阶段 DWG 的 SHA256 检查点。
- `evidence/screenshots/`：before|cleared|after 对比图、after 全图、ED08/ED04/ED02 放大图。
- `evidence/environment.txt`：运行时包、插件 mvid/DLL sha、模板、实例基线。

## 事实边界

- V1.1：35/35 重开核验通过；ED04 仍 2 条保守候选；P1-B 负向用例、P2-C/P2-D 未完成。
- 砖墙：第 1、2 批已保存（`s2.dwg` 32,021 B）；第 3 批校验通过但保存前进程退出而丢失；第 4 批与 PDF 未执行。

需要复核时按主报告第 4 节逐项打开，不要只看摘要。
