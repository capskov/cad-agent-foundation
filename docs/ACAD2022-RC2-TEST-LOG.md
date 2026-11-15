# ACAD2022-RC2 实测日志（客户端写入闭环 + callout 端到端）

- 机器：Windows 10 本机，AutoCAD 2022 R24.1.173.0.0.0（`F:\Program Files\Autodesk\AutoCAD 2022`）
- 生产图纸 PID 12948 **全程未绑定、未写入、未保存**；每轮测试前先 `Get-Process acad` 核对并只杀其他实例。
- 插件产物 sha256：`D16B21543C56D6FCAD601FA33859EEC75F85255BE8E3E03AEE3B44690F23AD64`
- 运行包：`artifacts\packages\20261115-132320`（含本轮全部修复）
- 编译：插件 net48 0 error；server net8 0 error（`build-logs\rc2-plugin.log`、`build-logs\server-net8.log`）

## 1. 默认 apply（写入+验证，不保存）

工作区 `cad2022-test\rc2-a3`，`start --write --allow-save`，`apply --request-id rc2e1`：

```
"outcome": "committed_verification_pending"
"write_fence_released": false
"persisted": false
"next_action": "verified_not_persisted: rerun with --save-to <absolute.dwg> (client save route) to persist and release the fence"
```

`dump_journal.py`：`rc2e1 foundation_apply_plan completed/committed/passed`，`plugin_write_fences` 仍有该 drawing 的 fence（守卫生效）。

## 2. fence 被占时的第二次写入

同工作区，`apply --request-id rc2e2`：

```
"status": "rejected_before_submission"
"error": "the D1 request or drawing risk fence conflicts with an existing operation"
"reason": "fence_held_by_earlier_unpersisted_write"
"next_action": "no_new_write_queued: the held fence belongs to an earlier verified-but-unpersisted write; persist or resolve that request first"
```

journal 只有 `rc2e1`，`rc2e2` 无行 —— 拒绝后**没有偷偷入队**，`journal_has()` 只读回读是判定依据。

## 3. 一步式 apply --save-to（写入+验证+保存+finalize）

工作区 `cad2022-test\rc2-b4`，`start --write --allow-save`，`apply --request-id rc2d --save-to ...\drawings\persisted.dwg`：

```
"status": "completed"
"outcome": "committed_verified"
"write_fence_released": true
"persisted": true
"saved_path": "F:\\AI\\cad-agent-foundation-acad2022\\cad2022-test\\rc2-b4\\drawings\\persisted.dwg"
```

`dump_journal.py`：

```
('rc2d','foundation_apply_plan','completed','committed','passed',1,1,...)
('save-a7a3c518fde14130adac1862ee22b139','save_drawing','completed','none','not_checked',0,0,...)
--- fences ---  (空)
```

`journal_finalized=1`、`fence_released=1`、fence 表为空。

## 4. 重开 DWG 核验（几何）

工作区 `cad2022-test\rc2-verify`，`start --source ...\rc2-b4\drawings\persisted.dwg`（只读），`read --handles 229,22A`：

- `229`：`type: Line`，`start (0,0)` → `end (100,0)`，`layer 0`，`color_index 7`，`ok: true`
- `22A`：`type: Circle`，`center (50,60)`，`radius 20`，`layer 0`，`color_index 7`，`ok: true`

两对象都带 `entity_digest`，证明 30,358 B 的 `persisted.dwg` 里确实存着写入的几何。

## 5. callout 端到端（2022 首个真实业务流程）

夹具 `cad2022-test\rc2-plan-callout.json`（单步 `callout`）：A `(50,80)` → B `(140,112)` → C `(200,112)`，文本锚点 `(206,108)`，`height 3.5`、`width 70`、`dot_radius 0.8`、layer `0`、`color 7`、`text_color 7`、`gap 0`。

工作区 `cad2022-test\rc2-callout`，`apply --request-id rc2callout --save-to ...\callout-persist.dwg`：

```
"outcome": "committed_verified"
"write_fence_released": true
"persisted": true
"objects": { "k1": { "text": "229", "leader": "22A", "dot_boundary": "22C", "dot_fill": "22D" } }
```

插件回执逐项核验（`verification_scope: declared_plan_properties`）：

| handle | 类型 | 关键事实 |
|---|---|---|
| 229 | MText | `text 交通安全设施\P测试标注 01`，`location (206,108)`，`text_height 3.5`，`width 70`，`actual_width 29.5056`，`actual_height 11.0540`，`attachment BottomLeft`，`text_style Standard` |
| 22A | Leader | `vertex_count 3`，`vertices_wcs (50,80)/(140,112)/(200,112)`，`has_arrow_head false`，`is_splined false`，`annotation_handle 229`，`annotation_reactor_present true`，`dimtad 0`，`gap 0`，`style_name ISO-25` |
| 22C | Circle | `center (50,80)`，`radius 0.8`，`color_index 2`（黄） |
| 22D | Hatch | `pattern_name SOLID`，`color_index 2`，`is_associative true`，`associated_handles ["22C"]` |

## 6. callout 重开核验

工作区 `cad2022-test\rc2-callout-verify`，`start --source ...\rc2-callout\drawings\callout-persist.dwg`（只读），`read --handles 229,22A,22C,22D`：

- `229` MText，文本保留（读回为 `交通安全设施\r\n测试标注 01`，`\P` 已转为换行）
- `22A` Leader，`vertex_count 3`，`annotation_handle 229`，`annotation_reactor_present true`
- `22C` Circle，`color_index 2`，`center (50,80)`，`radius 0.8`
- `22D` Hatch，`color_index 2`，循环边关联 `22C`

## 7. 本轮修掉的两个真实 Bug

1. `Pipe is broken`：插件 `ControlPipeListener` 写帧后未 `FlushAsync()`，且 `Disconnect()` 丢弃未冲刷字节。修复后同一场景不再出现（对照组 `rc2-a2`（修前）vs `rc2-a3`（修后））。
2. 重启 server 崩 `Stored plugin ownership cannot be reconciled to the allowlisted read-only tools.`：重归一致走 `TryValidateGovernedPluginRequest`（插件名），新增的保存路由只注册了 MCP 名分支。补齐后由 `rc2-b3` 复现、`rc2-b4` 验证通过。

## 8. 未通过 / 未测

- 保存后**重启**再 finalize：仅 `rc2-b3` 触发过一次（当时被上面第 2 个 Bug 挡住），修复后未重跑。
- `save_drawing` 作业 `effects=none` 语义待清理（不影响守卫判据）。
- 2026 运行回归：本机无 2025/2026，未运行。
- callout：只 1 条合成图流程，未做内框读取/分区排布/35 组批量/CTB PDF。
