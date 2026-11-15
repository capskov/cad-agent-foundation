# AutoCAD 2022 RC3 实测日志（安全状态 / 恢复 / 客户端稳定性）

日期：2026-11-15 晚（Asia/Shanghai）
基线：RC2 终版包 `20261115-132320`
本轮涉及包：`20261115-134744` / `-134901` / `-135458` / `-135814` / `-140009` / `-140052` / `-140202` / **`-140318`（当前）**
环境：AutoCAD 2022（R24.1.173.0.0.0，net48 插件）＋ server net8 ＋ client Python；隔离工作区 `cad2022-test\rc3-w1`
生产实例 PID 12948 全程未触碰；所有测试实例均按 PID 精确终止。

## 1. 目标窗口构造（保存成功后、finalize 之前崩溃）

命令（探针 `cad2022-test\rc3_window.py`，需 `RC3_PKG` 指向打包目录）：

```
$env:RC3_PKG='<package>'
python rc3_window.py crash cad2022-test\rc3-w1 rc2-plan-write.json rc3w cad2022-test\rc3-w1\drawings\rc3w.dwg
```

输出：

```
[write] {"status":"verification_pending","outcome":"committed_verification_pending","fence":false}
[save]  {"phase":"completed","saved_path":"...\\rc3-w1\\drawings\\rc3w.dwg"}
[kill]  17812 SUCCESS
```

journal（`python cad2022-test\dump_journal.py <sqlite>`）：

```
('rc3w','foundation_apply_plan','completed','committed','passed',0,0,'…13:43:47.733','…13:43:47.748')
('save-d4924a342aeb44c9b6e9ad782338a106','save_drawing','completed','none','not_checked',0,0,…)
--- fences ---
('PATH:…\\RC3-W1\\DRAWINGS\\WORK.DWG','rc3w','2026-11-15T13:43:47.7335519+00:00')
```

磁盘：`rc3-w1\drawings\rc3w.dwg` 30,358 B。

## 2. 重启后查询（修复前：挂起）

`python rc3_window.py after cad2022-test\rc3-w1 rc3w`

```
[query-error] MCP tools/call response timed out.
[query-new-context-error] request_id is not owned by this context_id
```

`server.log` 关键行：

```
"dwg_job_get" completed. IsError = False.
method 'tools/call' request handler completed in 120470.0169ms.
```

诊断埋点（临时 `[d1-trace]`）揭示真正原因：

```
[d1-trace] enter rc3w
[d1-trace] validated discovery_pid=17812 stored_pid=17812
[d1-trace] sending job_get
[d1-trace] job_get replied ok=False     ← 120 s 后
```

即服务端绑定的 discovery 仍是**已死进程 17812** 的快照；`SendPipeAsync` 4 × 30 s 重试后失败。

## 3. 修复后重启查询（同时返回）

修复：`BoundPluginExecutionGateway.GetJobAsync` 入口用 `LiveOwnerPid()`（当前发布中的 discovery）与请求记录的 `owner_pid` 比较，不一致时完全不联系插件，直接由 ledger 回复。

输出（<1 s）：

```json
{"ok":true,"result":{
  "found":true,"state_unavailable":true,"recovery_required":true,
  "observed_owner_pid":17812,"current_process_pid":31296,
  "request_id":"rc3w","context_id":"2e535f198bc245118ca58dc38ac88546",
  "job":{"request_id":"rc3w","phase":"Completed","effects":"Committed","verification":"Passed","write_fence_finalized":false},
  "error":"write_owner_process_changed: the owning CAD process is no longer running; the fence stays held and finalization requires explicit operator recovery",
  "next_action":"operator_recovery_required: review the drawing and the saved file, then resolve the original request explicitly with dwg_recover; never replay the write"}}
```

含义：**不自动 finalize、不把历史 unknown/committed 作业改判为成功、fence 保持、给出可执行的人工入口**。

## 4. 操作员显式恢复（新增出口）

探针 `cad2022-test\rc3_resolve.py`（只走控制管道 `job_recovery_resolve`，`confirm=true` + `operator_note`）：

```
python rc3_resolve.py cad2022-test\rc3-w1\native\Dwg\acad-2022.json rc3w
```

修复前：`{"ok":false,"error":"job_not_in_recovery_state"}`
修复中：`{"ok":false,"error":"job_recovery_resolve_failed"}`（嵌套 operation lease 抛异常被 dispatcher 吞掉）
修复后：

```json
{"ok":true,"result":{"disposition":"applied",
  "released_risk_keys":["PATH:…\\RC3-W1\\DRAWINGS\\WORK.DWG"],
  "job":{"request_id":"rc3w","context_id":"2e535f198bc245118ca58dc38ac88546",
         "tool_name":"foundation_apply_plan", …}}}
```

journal 复核：

```
('rc3w','foundation_apply_plan','completed','committed','passed',1,0,'…13:43:47.733','…14:03:32.447')
--- fences ---
(空)
```

`write_fence_released=1`，`error_code=fence_released_by_operator_after_save`，`error_message` 为操作员备注。

## 5. 保存证据是否严格绑定本次写入（复核）

同一 journal 内取两行 `complete_target_json` 对比：

| 字段 | 写作业 `rc3w` | 保存作业 `save-d4924a34…` |
|---|---|---|
| `database_fingerprint` | `1f285df0-af5c-bc46-a227-70a52ecc9cfb` | 同 |
| `database_session_id` | `548ce9d7a5a545f29a7f6bbfd30652e2` | 同 |
| `drawing_path` | `…\rc3-w1\drawings\work.dwg` | 同 |
| 时间 | `updated_at 13:43:47.748` | `created_at 13:44:48.236`（严格更晚） |
| `result_json` | — | 含 `saved_path=…\drawings\rc3w.dwg` |

`HasCompletedSaveAfter` 的判据即：指纹相同 + 路径相同（两边都有值时）+ 保存时间晚于写作业更新时间 + 结果含 `saved_path`。任一缺失即不认账（fail closed）。

## 6. 恢复判定拒绝条件（未放宽）

- `job_not_found`：无此请求。
- `job_not_in_recovery_state`：既不是 `unknown/recovery`，也不满足"已完成 + 已提交 + 已验证 + fence 未释放 + 有严格匹配保存"。
- `job_owner_still_alive` / `job_owner_liveness_indeterminate`：owner 存活或无法判定时一律拒绝。
- `plugin_sequence_overflow` / `resolve_race_detected`：保护不变。
- 参数侧：`confirm_required`、`operator_note_too_short`、`invalid_internal_params`。

## 7. 本轮未通过 / 未执行

1. 重启后即使已完成 resolve，`dwg_job_get` 仍返回 `recovery_required`（服务端无法感知插件侧已释放），标签偏保守；权威状态需读 journal。
2. 保存失败路径（磁盘不可写/路径非法）未在 RC3 重跑。
3. 重复 `request_id`、fence 冲突后人工恢复、控制管道断线、状态与 DWG 内容不一致保护：RC1/RC2 有覆盖，RC3 未逐项重跑。
4. 双实例隔离、超时取消：沿用 RC1 结论，本轮未重跑。
5. 2026 运行回归：本机无 2025/2026，未做。
6. 手工覆盖安装目录 dll 会被 `Gateway` 的 `plugin_sha256` 校验拒绝（`Installed plugin changed`）——测试必须走 `install/start` 部署。

## 8. 复现命令汇总

```
# 1) 构造窗口（会强杀自己启动的测试 CAD）
$env:RC3_PKG='F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\artifacts\packages\20261115-140318'
python cad2022-test\rc3_window.py crash cad2022-test\rc3-w1 cad2022-test\rc2-plan-write.json rc3w cad2022-test\rc3-w1\drawings\rc3w.dwg

# 2) 重启并查询原请求（应 <1 s 返回 recovery_required）
python cad2022-test\rc3_window.py after cad2022-test\rc3-w1 rc3w

# 3) 操作员显式恢复（confirm + note）
python cad2022-test\rc3_resolve.py cad2022-test\rc3-w1\native\Dwg\acad-2022.json rc3w

# 4) 查库
python cad2022-test\dump_journal.py cad2022-test\rc3-w1\native\Dwg\execution\plugin-execution.sqlite
```
