# TR-03：AutoCAD 2022 保存路由与 finalize 守卫的协议关系

## 1. 摘要与严重程度

2022 上"写入 → 验证 → 落盘 → finalize"必须由谁编排、以及落盘能否绕开 fence，属**核心写入安全机制**（严重度：高）。本轮已实现并在隔离实例实测通过，但其中两个语义问题仍未收敛，需要独立判断。

## 2. 版本与环境

- AutoCAD 2022 R24.1.173.0.0.0；插件 net48（sha256 `D16B2154…23AD64`）；server net8。
- 源码：`shared\Execution\FoundationSaveRequest.cs`、`server\Tools\FoundationSaveTools.cs`、`server\Execution\BoundPluginExecutionGateway.FoundationPlan.cs`、`shared\Infrastructure\CommandDispatcher.cs`（`write_requires_completed_save`）、`plugin-journal-net8\SqlitePluginIntentJournal.cs`（`HasCompletedSaveAfter`）。

## 3. 复现步骤

1. `start --write --allow-save --source tx10.dwg`（插件侧 `BIMWRIGHT_FOUNDATION_SAVE_ENABLED=1`，server 侧 `BIMWRIGHT_DWG_FOUNDATION_SAVE_TOOL_ENABLED=true`）。
2. `apply --plan rc2-plan-write.json --request-id rc2e1` → 只写不存。
3. 同图纸再 `apply --request-id rc2e2` → 期望被 fence 拒绝。
4. 新工作区 `apply --request-id rc2d --save-to ...\persisted.dwg` → 一步式。

## 4. 预期 vs 实际

- 预期：写作业在未落盘时 fence 不放；落盘后才可 finalize；重复写入被拒。
- 实际：符合预期（`rc2-a3` vs `rc2-b4`），且拒绝时有明确 `reason`、`journal_has()` 回读确认无新行。

## 5. 关键证据

- `docs\ACAD2022-RC2-TEST-LOG.md` 第 1–6 节；journal 行与 fence 表见第 3 节。
- 插件回执：`objects.k1 = {text:229, leader:22A, dot_boundary:22C, dot_fill:22D}`，`leader.annotation_handle=229`。

## 6. 已试方案

- 计划内 `save` op：写作业，与持有 fence 冲突 → 放弃。
- 服务端自动 finalize 后再让客户端 save：finalize 已被守卫拒绝，不成立。
- 独立 `save_drawing` 读作业 + 客户端 `--save-to` 编排：可用，已实测。

## 7. 根因与不确定性

- 根因：`save` 被建模为"图形写"，而落盘在语义上是"文件写"，两者与 fence 的关系不同。
- 不确定性一：`save_drawing` 作业落库是 `effects=none` / `verification=not_checked`，与"确实写了文件"不一致；守卫只看 `saved_path` + 指纹，功能正确但审计语义含糊。
- 不确定性二：保存成功、finalize 前崩溃时，重启后由谁补 finalize（用户已定"不自动"）；当前只有 `job_recovery_resolve` + 操作员确认这条人工路径，缺产品化入口。

## 8. 候选方案

- A：保持现状（独立 save 作业 + 显式 `--save-to`），补齐 `effects=file_write` 语义。
- B：把 `save` 提升为"受 fence 保护的写作业"，让计划内 save 合法化 —— 改动大，且会重定义 fence 覆盖范围。
- C：保存与 finalize 合成一个原子审计记录（save 行携带 `finalize_target`），崩溃后由该行驱动补 finalize。

推荐 A（最小改动、已验证），并把 C 作为下一阶段设计。

## 9. 希望独立判断的问题

1. `save_drawing` 记 `effects=none` 是否会在后续审计/恢复逻辑中造成误判（尤其"同 journal 内后续保存成功"判据）。
2. 保存成功但 finalize 前崩溃，产品化入口应放在服务端恢复扫描还是客户端显式 `resolve`；如何保证"不虚报成功"。
3. fence 被"已验证未落盘"的写长期持有时，除人工 resolve 外是否需要自动超时（会改变任务语义，需谨慎）。