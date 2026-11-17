# resolve --discard 诊断交接（本轮实测分层结论）

本轮不是"觉得难就退"，是真写了、真编、真在 AutoCAD 2022 里跑了 3 次。feature 已存在并可编译，但暴露出一个**分层根因**，特此固化给 GPT / 更高智模型。

## 已成立（不要重做）

- `--discard` 5 处改动 + 客户端动作全部落地：`RequestLedger.RecordAbandonedRecovery`、`Program.cs` reconcile 放行、`RecoverJobAsync`/`RecoverD1WriteJobAsync`、`dwg_recover` 工具、client `resolve --discard`。
- server 构建 0 错误、client py_compile OK、`gate.ps1` 四步 PASSED（`7495678`/`2898c5b`/`ffeaf07`）。
- 真实 CAD 复核：`CREATE: completed committed_verified`；健康写入**没有被 `--discard` 误废弃**（安全护栏成立）；`export_dxf` 仍正常。

## 分层根因（本轮逐步逼近）

### L1（已修）新进程内存归属为空 → 报 "applies only to owned write requests"
- 事实：CLI 每个命令是新进程；`TryGetOwnedRequest` 只查内存 `_requestOwners`；`--discard` 用 `g.context`（新 bind 得的新 context）与存盘 `rd-create` 的原始 context 不一致 → 归属查不到。
- 修法（已提交）：client `resolve --discard` 改用存盘请求记录里的原始 `context_id`（与 `query`/`status` 同源）。实测错误从 "not owned" 前进到 L2。

### L2（当前卡点）drawing_path_mismatch —— 图纸身份跨"保存 + 新进程重 bind"漂移
- 现象：改 context 后，`--discard` 走到 `RecoverD1WriteJobAsync` 的 `MatchesD1Ownership(ownership, bound, requestId, null)`，报 `drawing_path_mismatch`；同一工作区**新进程 `stop` 也报 `drawing_path_mismatch`**，导致该测试实例只能按 PID 强停（已强停 27504，无 acad 残留）。
- 推断：`apply --save-to` 保存后，归属记录里的 `complete_target`（图纸路径/身份）与新进程 `dwg_bind_active_document` 得到的活动文档身份不一致。本质上就是先前 D6"图纸身份/版本"问题的同族。

## 给 GPT 的核心问题

1. `dwg_recover` / 新进程 `stop` 在"保存已完成 + 新进程重新 bind"后，应如何**解析到原始图纸身份**：是以存盘归属的 `complete_target` 为准去匹配 bound，还是要求 ownership 携带一个**能跨保存稳定的图纸指纹**（路径+句柄+哈希）？
2. `MatchesD1Ownership` 的 drawing 校验是否应允许"同一受控工作副本、保存后路径不变"的情形？若是，具体放宽的判据是什么（不应无条件放宽，不能破坏未授权切换拦截）。
3. 是否需要在新进程 bind 时，用持久归属把 `_requestOwners` 回灌，使 `TryGetOwnedRequest` 在新进程也能命中？（当前只查内存，是 L1/L2 的共同土壤。）

## 复现与现场

- 工作区：`cad2022-test/resolve-discard-01`（每跑清空重建）。
- 测试脚本：`tools/resolve-discard-test.py`（install→start→apply(create+save)→resolve --discard→stop）。
- 运行时：server `330794B8…`、client `47FDD55A…`、plugin `750C0489…`（本轮未改 plugin）。
- 红线：不得为通过而放宽实体校验/跳过 fence；不得触碰生产 CAD（管立得2020）。
