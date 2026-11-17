# 01 缺陷清单（13 条）

严重度：S0 = 阻断出图能力；S1 = 阻断工作流/需换工作区重来；S2 = 体验/表达力缺失。
每条给出：现象 / 报错原文 / 复现 / 根因假设 / 证据。

> ## 修订记录（v2，2026-11-17，按评审意见）
>
> 本文保留 commit `db7f432` 的原始判断，修订只在此处登记，正文不删改。逐条理由见 `05-review-response.md`。
>
> | 缺陷 | v1 判定 | v2 修订 | 证据等级 |
> | --- | --- | --- | --- |
> | D1 标注不可用 | S0 根因=`dimension_style` 被列为必填 | **维持**；但行号须引用构建树路径（镜像行号无效）；构建树中同样成立（已复核） | A |
> | D2 悬挂 fence | S1 根因=参数拒绝路径泄漏 fence | **降级为"未证实"**：现有文案只说明存在更早的未持久化写，因果关系未证明 | C |
> | D3 request_id 冲突 | S1 缺陷 | **降为提示/文档缺陷（P2）**：同 id 不同参数必须拒绝，这是正确的幂等保护 | A |
> | D5 set_text 锁死 | S0 根因=readback 把宽度/字型属性也比了 | **根因未定**：先落盘两份回执做字段级 diff；不得预先放宽；fence 不得自动释放 | B（现象）/ C（根因） |
> | D6 一图一保存 | S1，建议加 revision 开关 | **改判为设计约束**：需定义 revision 生命周期，不是加参数 | A |
> | D8 无 export_dxf | S1 功能缺失 | **改判为装配缺口**：源码有实现，运行插件 DLL 里无该符号；另有 INSUNITS∈{0,6} 门槛 | A |
>
> 另：原文"22 次 apply / 17 成功 / 5 失败"已更正为 **20 次尝试 / 14 次 committed_verified**，
> 详见 `02-evidence.md` 第五节。

---

## D1 [S0] `linear_dimension` 等标注操作不可用，错误信息误导

**现象**：4 条 `linear_dimension` 的 plan 提交即被拒，图上一个 DIMENSION 实体都没有。

**报错原文**（`cad2022-test/pilot-loop-08/apply-v5.json`）：

```json
{
  "status": "rejected_before_submission",
  "request_id": "pilot-05",
  "error": "unexpected or missing plan fields",
  "persisted": false,
  "next_action": "no_new_write_queued: the held fence belongs to an earlier verified-but-unpersisted write; persist or resolve that request first"
}
```

**提交的 plan（片段）**：

```json
{"id":"dimA","op":"linear_dimension","start":[300,1800],"end":[5300,1800],"position":[2800,640],"rotation":0,"layer":"CF_DIM","color":256}
```

**根因（本轮已定位到代码行，待评审确认）**：
`src/shared/Execution/FoundationPlanExtensions.cs:43-45`

```csharp
case "linear_dimension":case "aligned_dimension":
 KeysWithOptional(s,new[]{"id","op","start","end","position","dimension_style","layer","color"}, ...);
```

`dimension_style` 被写进了 **required** 数组。Agent 按直觉省略它 → 触发
`KeysWithOptional` 的 `"unexpected or missing plan fields"`。这条错误文案不指明缺哪个字段，
导致排查方向被带偏（先怀疑是 2022 移植不支持标注）。

**影响**：任何需要真实 DIMENSION 实体的任务都做不了；只能用手绘线条 + TEXT 造假标注。
GPT 在复核里明确指出：**"尺寸标注是基础能力，重要性比眼下这张井图还高。"**

**已确认的可用绕过（不改代码即可出图）**：blank.dwg 里已有 `Standard` 与 `Annotative` 两个标注样式
（证据：`apply-6d` 的 `result_json.symbols_before.dimension_styles`），
因此 plan 里显式写 `"dimension_style":"Standard"` 即可提交；或用 `dimension_style` 操作先创建 `CF_DIM`。

**修复方向（二选一，需评审拍板）**：
1. 把 `dimension_style` 降为 optional：为空时取当前/`Standard` 样式；无任何样式时自动建一个 `CF_STD`；
2. 保持 required，但把错误改成字段级：`missing required field: dimension_style (linear_dimension)`，
   并在 CLI 文档 / JSON schema 里显式列出。

> 注意：无论选哪种，**错误的文案必须改**——现在的 `unexpected or missing plan fields`
> 无法区分"字段缺失"和"多传了未知字段"，本轮排查方向被它带偏过一次。

---

## D2 [S1] 被拒的请求留下写栅栏（held fence），工作区作废

**现象**：D1 那次拒绝之后，同一工作区后续任何写入都提示要先解决"上一个已验证但未持久化的写"。

**报错原文**：见 D1 的 `next_action` 字段。

**根因假设**：fence 在"请求登记"阶段就获取，拒绝发生在参数校验之后、登记回滚之前；
或者校验失败路径没有走 fence 释放。

**影响**：一次参数写错 = 一个工作区报废（本轮 `pilot-loop-08`、`pilot-loop-11` 都因此弃用）。

**修复方向**：拒绝路径必须释放 fence；并新增 `foundation resolve --request-id xxx --discard`
让用户显式丢弃悬挂请求。

---

## D3 [S1] `request_id` 与参数强绑定，不接受重试

**现象**：修正 plan 后沿用同一 `request-id` 重提。

**报错原文**（`pilot-loop-09/apply-5a.json`）：

```json
{"error": "Request ID already belongs to different parameters"}
```

**影响**：Agent 必须记住"每次改 plan 就换 id"，容易踩坑；且错误没有给出可用的新 id 规则。

**修复方向**：同 id + 同参数 → 幂等返回原结果；同 id + 不同参数 → 明确 `request_id_conflict` 错误码 +
建议 id（如 `pilot-05a-2`）。或者干脆支持省略 request-id 由服务端生成。

---

## D4 [S1] `apply` 与 `start` 竞争：workspace already has a CLI or MCP session

**现象**：`start`（后台）刚返回、立刻 `apply`，报：

```json
{"error": "This workspace already has a CLI or MCP session. Close it first."}
```

等 `start` 的后台任务真正结束（约 30-60s）后重试同一命令即可成功。

**影响**：脚本化批量出图时无法可靠地"启动即可写"，每次都要靠重试。

**修复方向**：`apply` 内部等待 session ready（带超时）；或 `start` 支持 `--wait-ready` 返回可写状态；
错误文案给出"等待 N 秒或换工作区"的具体建议。

---

## D5 [S0] `set_text` 原地改字：插件通过、服务端失败、fence 不释放

**现象**：改一处标题文字（`set_text` handle 24B）结果：

```
status: verification_pending
effects: committed
verification: failed
write_fence_released: False
verification_message: foundation_plan_readback_mismatch,plugin_stage_not_passed
next_action: save_succeeded_but_finalize_pending: query the original write request; never replay the write
save_reply: {"saved_path": "...\\pilot-06d.dwg", "phase": "completed"}
outcome_summary: {"outcome":"committed_verification_failed","plugin_verification":"passed","server_verification":"failed","write_blocked":true,"next_action":"inspect_existing_handles"}
```

**关键矛盾**：
- 插件侧 `passed`（AutoCAD 里确实改成了）
- 服务端 `failed`（readback 比对不一致）
- 同时 `effects=committed`、文件也 `save_succeeded`，但 `write_blocked=true` 永久锁住

**证据**：`cad2022-test/pilot-loop-15/apply-6d.json`（0 字节，进程被打断）、
`pilot-loop-16/apply-6d.json`（完整，见上）、`pilot-loop-16/server.log`。

**根因假设**：`set_text` 的 readback 校验把 DBText 的**非目标属性**（如实际宽度、对齐点、字型缓存）
也纳入了比较；服务端与插件读到的属性集合不一致。失败后未触发 fence 释放/事务回滚。

**影响**：任何"改一个字"都要重建整张图（本轮为此重建了 3 个工作区：pilot-loop-17/18/19）。

**修复方向**：
1. readback 只比对本次操作声明的属性（text 内容 / 指定 handle），其余忽略；
2. 失败时统一走 `committed_verification_failed` → 释放 fence + 标记 revision 不可信（而不是永久锁）；
3. 输出**字段级 diff**（expected vs actual），而不是只给两个错误码。

---

## D6 [S1] 一图一保存（OPEN-05），每次改动都要新目录 + install + start

**现象**：同一工作区在 `apply ... --save-to` 之后不能再写，必须
`新目录 + install + start --source 上一版 dwg`；否则触发 OPEN-05。

**影响**：一版图被拆成 3 个 plan、3 个工作区（本轮 V3/V4 各 3 个）；时间成本 ×3，便于出错。

**修复方向**：引入"revision"概念：save 之后自动重开事务（`--new-revision`），
或明确提供 `foundation revision --from <dwg>`。

---

## D7 [S1] `tools/session_handoff.py` 失效

**现象**：按官方脚本交接后，原实例被停掉，新实例也立刻退出：

```
Owned CAD process has exited
```

`stop` 子命令同样报这个错。

**影响**：官方给的"换工作区"标准路径不可用，只能靠手工 `install/start --source`。

---

## D8 [S1] 2022 移植无 `export_dxf`

**现象**：`Operations` 列表里登记了 `export_dxf`，但 2022 移植版执行不通；只能：

```
accoreconsole.exe /i <dwg> /s <scr> /l zh-CN     # 脚本内 DXFOUT + 16
```

注意：**必须用 `/s` 而不是 `/b`**（`/b` 会停在命令行不动，且不报错）。

**影响**：DXF 交接（给外部评审）依赖外部工具，链路脆弱；脚本化时会无提示卡住。

**修复方向**：实现 `export_dxf` 到插件侧（`AcadDocument` + `DXFOUT` 命令封装），或至少把
`accoreconsole /s` 包成 `foundation export-dxf` 并加超时。

---

## D9 [S2] 表达力缺失：无 linetype、text 无对齐、无自动换行

- 无 `linetype` 参数 → 人孔盖板投影、假想线只能用**实线 + 文字说明**（本轮图上已注明"实线代虚线"）。
- `text/mtext` 无 `alignment/justify` → 做不出水平居中；标题过长会被裁，只能拆行 + 调字号。
- 后果：图上"看起来对"，但制图规范不合规，评审模型一眼能看出。

**修复方向**：`text` 增 `alignment`（9 种），`layer`/实体增 `linetype`（允许加载内置线型表）。

---

## D10 [S1] 保存后 CLI `view` 起不来 MCP server

**现象**：`foundation view` 在保存之后启动失败，`server.log`：

```
Stored D1 write ownership has no matching pre-existing request ledger row
```

**绕道**：直连插件：`bind_active_document` 取 context_id → `job_submit(tool_name=foundation_view)`
→ `job_get`。本轮所有截图都是这么来的（`view_v2.py`…`view_v8.py`）。

**修复方向**：view 不应该依赖写事务所有权；把 view 划到只读通道。

---

## D11 [S2] `archive` 需要目标图层"已存在且已关闭 + 已冻结 + 不打印"

**现象**：在新 plan 里 `layer` 新建 + `archive` 同一层，直接失败：

```
archive_layer_existing_state_mismatch
```

必须先 `set_layer`（带 handle + expected_sha256）把状态改成"已关闭/冻结/不打印"，才能 archive。

**修复方向**：`archive` 支持"层不存在则按需创建并设置状态"的 preflight（或用 `--create-archive-layer`）。

---

## D12 [S2] `capture` 不自动框范围

**现象**：直接 `capture` 抓到的是全模型空间（`wcs_xy_bounds` 到 -944..6544），图形缩在角落；
必须先 `action=window` 指定 `min_x/min_y/max_x/max_y`。另外 `file_name` 必须匹配
`^[A-Za-z0-9][A-Za-z0-9_-]{0,79}\.png$`，中文名直接失败。

**修复方向**：`capture` 支持 `fit=entities|window`，默认按实体范围自动框；文件名规则放宽到 Unicode。

---

## D13 [S2] DXF 头写的编码与实际内容不一致

**现象**：`$DWGCODEPAGE=ANSI_936`，但写入的 TEXT 内容是 UTF-8；外部解析器（含评审模型）按
`ANSI_936` 解码会出现乱码风险。GPT 在 v1 复核时就点过这一条。

**修复方向**：导出前统一代码页（`_DWGCODEPAGE`→UTF-8 或按 ANSI_936 写真实字节），
并在 `export_dxf` 里固定 `$DWGCODEPAGE` 与 `$ACADVER`。

---

## 附：不属于底座缺陷、但影响了本轮判断的两件事

1. **任务口径曾经错了**：v1~v4 按"水表井原样复现"画（水表2/蝶阀4/止回阀2/伸缩接头2），
   用户实际要的是"三条管道 + 三个闸阀"的闸阀井改造。**底座无责**，但说明"计划先对齐口径"这一步不能省。
2. **几何尺寸来源分级要显式**：A 级=图集标注值，B 级=按箭头语义推断，C 级=示意占位。
   v1~v3 把 B/C 级当 A 级用，被 GPT 反复退回。建议底座 plan 支持 `provenance` 字段（值域 A/B/C + 来源页）。
