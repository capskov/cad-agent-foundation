# 技术复核：finalize 释放 fence 与 DWG 落盘之间的静默丢失窗口

- 编号：TR-2026-11-15-01
- 状态：已定位、已实施协议顺序修复、已实测；仍有一个残余场景待复核
- 严重程度：**高**（可能导致"任务显示成功、图形实际未落盘"，属于静默丢失）

## 1. 问题摘要

迁移到 AutoCAD 2022 后，写入闭环路径为 `write → job_write_finalize → save_drawing`。

- `job_write_finalize` 的语义是"内存写入已验证 + **释放写 fence**"。
- `save_drawing` 是独立作业，调用 `Database.SaveAs`，把**整库快照**写到磁盘。

两者之间没有任何先后约束，于是出现一个窗口：**fence 已被释放，但 DWG 还没落盘**。此时：

- 若进程崩溃：journal 记录 `phase=completed / effects=committed / verification=passed / write_fence_released=1 / write_fence_finalized=1`，而磁盘上从未有过这次写入 —— 作业"成功"，图形丢失。
- 若保存失败：同上，且 fence 已释放，后续写入可以继续进入，被丢的那次写入不会被任何机制发现。

这违反"不允许把未解决的崩溃状态自动清成成功"的安全基线。

## 2. 版本与环境

| 项 | 值 |
|---|---|
| AutoCAD | 2022（`acad.exe` 24.1.173.0.0，R24.1） |
| 插件 | `Bimwright.Dwg.Plugin.Acad22.dll`（net48），TFM `.NETFramework,Version=v4.8` |
| 运行时 | AutoCAD 进程内 CLR（.NET Framework 4.8），SQLite 经 `SQLitePCLRaw 2.1.13` |
| 环境变量 | `BIMWRIGHT_FOUNDATION_WRITE_ENABLED=1`、`BIMWRIGHT_FOUNDATION_SAVE_ENABLED=1` |
| 测试根 | `cad2022-test\run-tx3`、`run-tx4`、`run-tx5`、`run-tx6`、`run-tx8`、`run-tx9` |
| 涉及源码 | `plugin-journal-net8\SqlitePluginIntentJournal.cs`、`shared\Infrastructure\CommandDispatcher.cs`、`shared\Drawing\DrawingSaveService.cs`、`shared\Execution\PluginJobModels.cs` |

## 3. 复现步骤

前置：隔离实例，`BIMWRIGHT_DWG_ROOT` 指向独立测试根，未保存的新图（Drawing1）。

**场景 A：finalize 后崩溃**
1. 提交写入 `tx3-w1`（单线，`foundation_apply_plan`），等待 `completed/committed/passed`。
2. `job_write_finalize`（6 参数取自 journal 行）→ `applied`。
3. 不保存，直接 `Stop-Process -Force` 杀掉 acad.exe。
4. 同 root 重启实例，`dump` journal；再提交新写入 `tx3-w2`。

**场景 B：保存失败**
1. 写入 `tx5-w1`，finalize → `applied`。
2. 提交 `save_drawing`，`output_path` 指向**不存在的目录**。

**场景 C：finalize 与 save 之间插入新写入**
1. 写入 `tx4-w1` → finalize。
2. 提交 `tx4-w2`（同一图）→ 完成。
3. 再提交 `save_drawing`。

## 4. 预期 / 实际

| 场景 | 预期 | 修复前实际 |
|---|---|---|
| A | fence 不应在落盘前释放；崩溃后不得显示"完成" | `tx3-w1 rel=1 fin=1`，磁盘无该实体；重启后 `tx3-w2` **可正常写入**，被丢的写入无从发现 |
| B | 保存失败时 fence 应保持 | `tx5-w1 rel=1 fin=1`，save 作业 `failed save_drawing_failed`，fence 已释放 |
| C | 允许写入但状态必须可区分 | 允许；`tx4-w2` 处于"已落盘但未 finalize"（`rel=0`），当时不可判定 |

## 5. 关键证据

修复前 `run-tx3`：

```
tx3-w1: foundation_apply_plan phase=completed eff=committed ver=passed rel=1 fin=1 err=None
[fences] (none)
重启后：
tx3-w2: ... phase=completed eff=committed ver=passed rel=0 fin=0
  说明：tx3-w1 已在 journal 里"完成"，但其图形从未写盘，且 fence 已放开
```

修复前 `run-tx5`：

```
tx5-w1: ... rel=1 fin=1
tx5-save: save_drawing phase=failed err=save_drawing_failed
[fences] (none)
```

写入与落盘确实可用的对照（`run-tx6` / `run-tx8` 保存后重开核验）：

```
{"entities":[{"handle":"222","ok":true,"entity":{"handle":"222","type":"Line","layer":"0","color_index":7}}]}
```

## 6. 涉及代码与改动

修复点 1 —— 协议顺序守卫（`CommandDispatcher.DispatchWriteFinalize`）：

```csharp
if (stagePending && App.FoundationSaveEnabled
    && snapshot.ToolName == FoundationDrawingPlan.PluginToolName
    && !App.PluginJournal.HasCompletedSaveAfter(snapshot.RequestId))
    return CommandResult.Fail("write_requires_completed_save");
```

修复点 2 —— journal 判定（`SqlitePluginIntentJournal.HasCompletedSaveAfter`），最终形态是**按图纸指纹绑定**：

- 取写作业的 `updated_at_utc` 与 `complete_target_json` 中的 `database_fingerprint`；
- 候选保存必须同时满足：`tool_name='save_drawing'`、`phase='completed'`、`created_at_utc > 写作业 updated_at_utc`、**`database_fingerprint` 与写作业相同**、`result_json` 里带 `saved_path`；
- 写作业 `drawing_path` 与保存作业 `drawing_path` 都非空时还必须一致；
- 写作业缺少指纹（无法核验）→ **一律不给保存信用**（fail closed）。

早期只按"同 journal + 时间戳"判定，在 root 被多张图共享时会误判（A 图的保存可能替 B 图的写入背书）。改为指纹绑定后该风险消除，实测见第 9 节。

```sql
-- 旧判据（已废弃）：同 journal 内任一更晚的已完成保存
SELECT COUNT(*) FROM plugin_jobs saved
WHERE saved.tool_name='save_drawing' AND saved.phase='completed'
  AND saved.created_at_utc > (SELECT updated_at_utc FROM plugin_jobs WHERE request_id=$id);
```

接口新增 `bool HasCompletedSaveAfter(string requestId)`。

改动范围仅限 `BIMWRIGHT_FOUNDATION_SAVE_ENABLED` 打开且作业为 `foundation_apply_plan` 时，**2026 语义零变化**。

## 7. 已尝试方案及失败原因

| 方案 | 结果 |
|---|---|
| 用 `plugin_sequence > staged_sequence` 判断"之后有保存" | 失败。序列号按作业推进，写作业的 staged 序号远大于保存作业的序号，判断恒假 → 保存后仍被拒 |
| 用 `complete_target_json` 相等 + 时间戳 | 失败。`complete_target_json` 内含每次 `bind_active_document` 都会变的 `context_id`，等值比较不成立 |
| 时间戳 + 同 journal 范围 | 通过，但存在跨图纸误判风险（见下） |
| 时间戳 + `database_fingerprint` 绑定 + `saved_path` 存在（最终采用） | 通过。注入伪造行测试见第 9 节 |

## 8. 当前推测根因与不确定性

根因：`finalize` 的语义被设计为"内存验证 + 释放 fence"，落盘是独立作业，二者没有顺序契约；而迁移侧新增的 `save_drawing` 是唯一落盘入口，导致"完成"与"持久化"脱钩。

不确定性：

1. `HasCompletedSaveAfter` 用时间戳排序，依赖 `created_at_utc` 的 `O` 格式字符串可比（同偏移下成立）。若将来引入不同时区写入，需改为数值时间戳。
2. ~~判定按"同一 journal 内任一已完成保存"~~ **已解决**：改为 `database_fingerprint` 绑定 + `saved_path` 存在 + 路径一致（两侧都有路径时）。剩余不确定性：指纹关闭重开同一 DWG 是否恒定、`SaveAs` 到新文件后指纹是否变化——当前只验证了同一会话内一致。

## 9.0 残余不确定性（指纹语义）

- `database_fingerprint` 来自 `AcDbDatabase.FingerprintGuid`：本次只验证了"同会话内写作业与保存作业一致"、以及"伪造不同指纹不被采信"。
- 未验证：关闭图纸再打开同一 DWG 后指纹是否不变；`SaveAs` 到新路径后指纹是否随库内容变化。若会变，则跨会话的保存信用会失效（表现为 finalize 被拒，属于安全侧失败，不会误放行）。

## 9. 实测（判据严格化后）

回归（`run-tx10`）：`write` → finalize 被拒 `write_requires_completed_save` → `save`（29,975 B）→ finalize `applied` → `rel=1 fin=1`、`fences (none)`。

严格性注入（`run-tx11`，脚本 `cad2022-test\rc1_strict.py`）：真实保存行被删除，只留一条**指纹被改成全零**、时间戳更晚、同样 `phase=completed`、同样带 `saved_path` 的伪造行：

```
tx12-w1: foundation_apply_plan completed created=2026-11-15T12:26:22.4650057+00:00 fp=1d5750a6-…
fake-save-bad: save_drawing completed created=2026-11-15T12:26:25.5158393+00:00 fp=00000000-…
finalize tx12-w1 -> ok=False err=write_requires_completed_save   ← 伪造行不被采信
再做真实保存 tx12-save2 -> finalize ok=True disp=applied；fences (none)
```

## 9.1 残余场景（需要独立复核）

**崩溃在"保存成功之后、finalize 之前"**：DWG 已有数据、journal 为 `rel=0 fin=0`（fence 仍持有）。当前恢复逻辑保留该作业（正确），但 fence 只能等人/客户端再发一次 finalize。需要判断：

- 这种情况是否允许"保存已完成 + 验证通过"作为自动 finalize 的依据？
- 若不允许，客户端重试策略与超时清理由谁负责？

## 10. 希望另一位 AI 独立判断的问题

1. 把"落盘成功"作为释放 fence 的**前置条件**是否正确？是否存在应当允许"先 finalize 再延迟保存"的合法场景（例如只做内存验证、稍后批量保存）？若存在，是否需要显式的"未持久化"状态标记而不是拒绝。
2. 以"同一 journal 内时间戳更晚的已完成保存"作为判据是否足够严格？在多图纸共用 root 的部署下会否误判。
3. 崩溃在 save 之后、finalize 之前时，自动 finalize 与人工 finalize 的取舍，哪个更符合上游 2026 的任务治理语义。

## 11. 附：最小复现资料

- 脚本：`cad2022-test\rc1_tx.py`（`w` / `f` / `s` / `dump` / `handles` / `recheck`）
- 日志证据：`run-tx3`、`run-tx5` 的 `native\Dwg\execution\plugin-execution.sqlite`（`plugin_jobs` / `plugin_write_fences` 两表）
- 修复后验证：`run-tx9`（未保存拒绝 → 保存 → applied → fence 清空）
