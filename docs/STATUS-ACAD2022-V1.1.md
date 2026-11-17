# ACAD2022 V1.1 底座状态基线（唯一权威口径）

> 本文件是当前唯一应被引用的状态口径。RC1 的 `RC-NOTES.md`（基于 `0190523`）**已过时**，其中「`resolve --inspect` 未实现」「`export_dxf` 仍需再保存」等结论已被后续提交推翻，见 §4 更正。给 GPT / 高智模型时只给本文件 + 两份细节件，不要再塞重复叙述。

## 0. 版本标识

| 项 | 值 |
| --- | --- |
| 仓库 / 分支 | `capskov/cad-agent-foundation` / `acad2022-v1.1-src` |
| 当前 HEAD | `e7992b4` |
| 提交链（近端） | `ac2f0d8 → 76fc7d7 → 267ad39 → 6e83621 → 0190523 → df5b38d → 42cb7e3 → 314c11f → 7495678 → 2898c5b → ffeaf07 → e7992b4` |

## 1. 已解决（勿再重做，勿再作为“问题”上报）

| 项 | 结论 | 提交 / 证据 |
| --- | --- | --- |
| D5 跨 net48/net8 规范哈希不一致 | 代码修复，真实 ACAD2022 通过 | `ac2f0d8`；`tools/hash-regress` 16 项 ALL PASSED |
| D8 DXF 单位 | 支持 `INSUNITS 0/4/6`，保留原始坐标不换算；毫米图导出 `$INSUNITS=4`、6 个 DIMENSION | `ac2f0d8`；`dxf-finalize-01` |
| D1 标注字段 | 缺 `dimension_style` 返回字段级错误，不再“unexpected or missing plan fields”；6 条原生 DIMENSION 实测 | `76fc7d7`；`rc-07` |
| D6 保存后连续编辑（方案 A） | 同路径就地保存，同工作区 5 轮 `apply+save` 无 `drawing_path_mismatch` | `76fc7d7` / `267ad39`；`rc-07` |
| `--save-to` 预检 | 目标路径 / owned 身份 / 覆盖权限在 `apply` **之前**预检；非法目标不产生任何写入 | `6e83621`；`client/foundation.py:433` `precheck_save_target` |
| 交付副本 `publish-dwg` | 临时文件 → SHA256 → `os.replace` → 复核；默认拒覆盖；写审计；不改文档身份 | `267ad39`；`rc-07` RC1/2/3 |
| `stop` → `start --reopen` → 读回 | 重开后图形/标注/标题保持，`reopen_digest_recheck=matched` | `76fc7d7`；`rc-07` |
| `export_dxf` 纯文件产物 finalize | 不再要求“再保存一次才释放 fence”；`write_fence_released=True/finalized=True` | `42cb7e3`；`dxf-finalize-01` |
| `resolve --inspect` | `dwg_job_get`/`status` 已投影 `write_fence_released/finalized/global_fence/readback_*`，等价 inspect | `df5b38d` |
| 构建可复现门禁 | 干净检出 + `tools/gate.ps1` → plugin(net48)/server(net8)/hash-regress 全绿，产物哈希与包一致 | `0190523` / `6e83621` |
| 事务安全 | fence、请求幂等、图纸身份校验；重复 request_id 拒绝；异常路径不产“貌似成功”文件 | `rc-07` / `rc-02` |
| 源码可复现性（本轮补） | `support/readonly-entry.py`（客户端引擎 `E`）此前不在 git 仓库根，干净 clone 无法组装运行；**本轮已补入** | 见 §5 |

## 2. 有使用限制（已知语义，不是缺陷）

- `publish-dwg` 只发布 **DWG 副本**：`delivery_scope=dwg_copy_only`、依赖仅查 XREF；字体/光栅/底图/打印样式**未查**，`complete_engineering_package` 恒 `false`。
- 发布只保证“**已确认磁盘保存版本的副本**”：`last_saved_memory_disk_proven=false`（API `SaveAs` 后 `DBMOD` 仍可能为 1，不声称内存==磁盘）。
- 审计写失败 = **部分完成**：DWG 已原子落地则返回 `partial_completion=true` + `target_sha256`，按摘要追溯。
- 仅 AutoCAD **2022** 分支支持 `--allow-save`。
- 单工作区单工作图：`--save-to` 不接受别的路径，交付走 `publish-dwg`。

## 3. 未解决基线（本轮不修，交 GPT / 高智模型）

### B1. `resolve --discard` —— 已实现 5 处，卡在 L2 图纸身份（最高优先）
- 已落地：`7495678`（账本 `RecordAbandonedRecovery` + reconcile 早退）、`2898c5b`（`RecoverJobAsync`/`RecoverD1WriteJobAsync`/`dwg_recover`/客户端 resolve）、`ffeaf07`（工具目录 + 测试脚本）、`e7992b4`（客户端用原始 context_id）。
- 安全护栏已成立：健康 `committed_verified` 写入被 `--discard` 拒绝。
- **卡点**：真实 ACAD2022 实测，`--discard` 与同工作区新进程 `stop` 均报 `drawing_path_mismatch`。
- **已纠正的旧判断**：`drawing_path_mismatch` **不来自** `MatchesD1Ownership`，而是客户端 `check_binding_identity`（`support/readonly-entry.py:549`，比较 `snapshot['drawing_path']` 与 `session['drawing']`，两边均 `normcase+normpath`）。
- **本轮字段级取证**：干净保存后 `snapshot.drawing_path == session.drawing`（逐字符相同）、`owner_pid` 一致 → **bind 身份本应通过**。故 L2 根因待定（可能与时序/脏状态/早期工作区残留有关），**不要**先发明“跨保存图纸指纹”，也**不要**放宽 `MatchesD1Ownership`。
- 细节：`docs/resolve-discard-diag.md`（含给 GPT 的三个问题）。

### B2. `stop` 在 API 保存后可能 exit 1 —— 待干净复核
- `client/foundation.py:79` `stop` 要求 `document_count==1 and dbmod==0 and command_active==0`。
- 本轮 diag 中 `stop` 返回 exit 1、stdout 为空（疑似 `dbmod!=0`）。RC1 矩阵曾记录 `stop fences 0` 通过。**需一次干净 create→save→stop 复核后再定性**；若确为 API 保存后 `DBMOD` 残留，与 `last_saved_memory_disk_proven=false` 同源，应统一处理而不是放宽校验。

### B3. `resolve --recover`（未实现，需设计）
- 不能“读盘就宣布成功”。须先证磁盘版本确由该 request 产生（句柄+哈希+账本一致）再恢复。属新设计，**交 GPT 定方案**。

### B4. 其他暂缓
- `switch-revision`（把新路径 DWG 变为活动工作图）：仅保留边界设计。
- 工程交付打包（字体/图像/底图/打印样式依赖收集校验）：未开发。
- 国标标注样式体系（自动样式、比例联动）：未开发，仅用既有 `Standard`。

## 4. 对过时信息的更正（防止 GPT 据此误判）

| 过时结论（RC1 / `0190523`） | 现状 |
| --- | --- |
| 「`resolve --inspect` 未实现」 | 已由 `df5b38d` 实现（fence 状态可审计） |
| 「`export_dxf` 导出后仍需再保存一次」 | 已由 `42cb7e3` 修复（纯文件产物 finalize 豁免 save-after） |
| 「`resolve --discard` 未实现」 | 已落 5 处（`7495678`…`e7992b4`），但 B1 卡点未过，**不宣称可用** |
| 「20 余次 apply / 17 次成功」类统计 | 以 `rc-07` 验收矩阵为准，不再引用早期模糊数字 |

## 5. 本轮（e7992b4 之后本地）实际改动

1. `client/foundation.py`：`resolve --discard` 改用**存盘请求原始 `context_id`**（与 `query/status` 同源），不再用新 bind 的 `g.context`。
2. **新增** `support/readonly-entry.py`（`80EE42D4BA502E521D812EF2F21081E04FC938C0157C7A59035B6CFE7FD69EF3`，与 runtime / `artifacts/packages/20261116-fix2` 完全同一份）进 git 仓库根，修复“干净 clone 无法组装客户端”的入库缺口。
3. 新增 `docs/STATUS-ACAD2022-V1.1.md`（本文件）作为唯一状态口径；新增 `tools/resolve-discard-diag.py`（字段级 bind 身份取证脚本）。

## 6. 红线（不变）

- 不为通过而放宽实体校验 / 跳过 fence / 改验收标准。
- 不改 `--save-to` 既有语义（已解决，勿动）。
- 不触碰生产 CAD（管立得2020）；只清理本轮记录 PID 的测试实例。
- 每个修复 = 源码 + 编译 DLL + 回归 + 实测证据；未实现不得写成已实现。
