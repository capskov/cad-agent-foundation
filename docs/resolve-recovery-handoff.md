# resolve 恢复面交接件（ACAD2022 V1.1）

本文件是给下一位执行者（高智模型或我本人）的精确施工图，**不是缺陷复盘**。所有落点都已在本轮从真实源码逐行核实。

## 已完成并验证（不要在重复调查）

| 提交 | 内容 | 验证 |
|---|---|---|
| `df5b38d` | `BoundPluginExecutionGateway.ProjectJobSnapshot` 补投 `write_fence_released/finalized/global_fence/readback_revision/readback_digest/committed_result_digest` | `gate.ps1` 全绿；`dwg_job_get`/`status` 现在能审计 fence 状态 = `resolve --inspect` 已打通 |
| `42cb7e3` | `shared/Infrastructure/CommandDispatcher.cs` `DispatchWriteFinalize`：对 `effects_scope=output_file_only` 且带 `output_file.sha256` 的纯文件产物写入豁免 save-after | 真实 ACAD2022（工作区 `dxf-finalize-01`）：export_dxf 后 `write_fence_released=True/finalized=True`，save 的 `pending_write_requests=[]`，`stop` fences 0，`start --reopen` reconcile 通过。复现脚本 `_tools/dxf-finalize-test.py` |

新 DLL：plugin `750C0489…`（MVID `537846a8-d531-4471-b2c2-e89e2804889c`）、server `9FDBEF3F…`，已部署到 Preview 运行时并更新 manifest。

## 关键架构事实（后续一切改动的依据）

1. **save-after 强制只在插件一处**：`shared/Infrastructure/CommandDispatcher.cs` `DispatchWriteFinalize` 中 `stagePending && App.FoundationSaveEnabled && ToolName==FoundationDrawingPlan && !HasCompletedSaveAfter` → `write_requires_completed_save`。journal 的 `FinalizeVerifiedWriteFence` 本身不要求 save。
2. **启动 reconcile 是 fail-closed**：`server/Program.cs` `IsConsistentRecoveredD1Write`。
   - `Verification==Failed` ⇒ 必须 `!WriteFenceReleased`（line ~359-360）。
   - 无 readback 状态的写入：`!hasReadbackState` 分支 ⇒ `!WriteFenceReleased || (Effects==None && Phase∈{Failed,Cancelled})`。
   - 因此「`abandoned_after_recovery`（failed + released）」当前两个分支都会 fail → 重启必 fail-closed。
3. **插件侧 `--discard` 原语已存在**：`IPluginIntentJournal.ResolveUnknownJob`（`SqlitePluginIntentJournal.cs` ~357-382）对 `phase='unknown' AND observation='recovery' AND write_fence_released=0` 的写入置 `phase='failed', error_code='abandoned_after_recovery', write_fence_released=1, plugin_sequence+=1` 并删 fence。管道命令 `job_recovery_resolve`（`CommandDispatcher.cs:207/496-529`，ACAD2022 构建已含，因 csproj 定义了 `ACAD2026`）。参数 `{request_id, confirm:true, operator_note(≥4字)}`，返回 `{disposition, released_risk_keys, job}`。
4. **服务端写入快照镜像会拒 abandoned**：`BoundPluginExecutionGateway.D1.cs` `TryAcceptD1PluginSnapshot` 的 `atStage/finalized` 分支（line ~420-431）要求 `verification=passed` 才算 finalized；abandoned 写入 `verification` 非 passed → 不能复用，需账本侧新迁移。

## `--discard`（显式废弃 unknown/recovery 写入）需要的 5 处协调改动

> 这是**多文件架构延伸**，不是一行豁免。每处都给了精确落点。

1. **账本新迁移** `server/Execution/RequestLedger.cs`
   - 新增 `RecordAbandonedRecovery(requestId, operatorNote, expectedSequence)`：`UPDATE request_jobs SET phase='failed', error_code='abandoned_after_recovery', error_message=$note, write_fence_released=1, plugin_sequence=$seq+1, risk_keys_json='[]', server_unavailable=0, updated_at_utc=$now WHERE request_id=$id AND phase='unknown' AND observation='recovery' AND write_fence_released=0`。
   - 参照现有 `MarkUnavailable`（line ~683）与 `Advance` 的 fence-release 写法（line ~269-281）保持一致的风险键/序号处理。

2. **服务端 gateway 方法** `server/Execution/BoundPluginExecutionGateway.cs`（新增，仿 `CancelJobAsync` line ~1343）
   - `RecoverJobAsync(contextId, requestId, confirm, operatorNote)`：校验写入归属 → `_send(bound.Discovery,"job_recovery_resolve",new{request_id,confirm,operator_note})` → 成功后调上面的 `RecordAbandonedRecovery` 镜像 → 返回 `ProjectJobSnapshot(job)`（已含 fence 字段）。

3. **新 MCP 工具** `server/Tools/FoundationExecutionTools.cs`
   - `dwg_recover`（`ReadOnly=false,Idempotent=false`），参数 `confirm:bool, operator_note:string`；在 `CreateToolInstances` 的 policy switch 加 `"dwg_recover"=>(Context:true,Request:true)`。描述须写明「仅废弃 unknown/recovery 写入并释放 fence；绝不重放」。

4. **reconcile 放行** `server/Program.cs` `IsConsistentRecoveredD1Write`（在 readback 检查**之前**加早退分支）
   ```csharp
   if (job.Phase == JobPhase.Failed
       && string.Equals(job.ErrorCode, "abandoned_after_recovery", StringComparison.Ordinal)
       && job.WriteFenceReleased)
       return job.PluginSequence.HasValue && job.PluginSequence.Value >= 0;
   ```
   - `LedgerJob.ErrorCode` 字段已存在（`RequestLedger.cs` error_code 列）。

5. **客户端** `client/foundation.py`（+ runtime 副本同步 + `_tools/update-manifest.py` MVID）
   - `status` 的 `resolve_tools` 目前恒为 `None`（line ~641），改为暴露 recover；新增 `resolve --inspect/--discard` 子命令分别映射 `dwg_job_get` / `dwg_recover`；`--recover`（恢复为可信）**不在本次范围**，保持显式未实现错误。

## `--recover`（把已提交但验证失败的写入恢复为可信）—— 需 GPT 架构定方案

不能「读盘就宣布成功」。必须先证明磁盘版本确由该 request 产生（句柄+哈希+账本一致），再决定恢复或废弃。属于新设计，交 GPT 审核后再动，低智模型不要做。

## 验收（4.1 flash 或第三方执行后必须跑）

- `tools/gate.ps1 -AutoCadDir "F:\Program Files\Autodesk\AutoCAD 2022"` 四步全绿。
- `_tools/resolve-discard-test.py`：对一个正常 committed_verified 写入调用 `--discard` 必须被拒（不在 recovery 态）；构造一个 unknown/recovery 写入后 `--discard` 成功且 fence 释放；`stop`+`start --reopen` 不 fail-closed。
- 红线：不得为通过而放宽实体校验或跳过 fence；不得触碰生产 CAD（管立得2020）。
