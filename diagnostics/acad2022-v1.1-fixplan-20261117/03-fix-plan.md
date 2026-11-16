# 03 修复计划（针对 ACAD2022-V1.1 底座）

**原则**：先修"能出图"，再修"出得好"；每一期以"一条真实图纸能从头到尾不换工作区跑完"为验收。
**不改 schema 版本号之前不动 v1 契约**（`foundation-drawing-plan-v1` 仍需向后兼容，改动以"放宽"为主）。

---

## P0 —— 让标注可用、让写入失败不再毁工作区（目标 3 天）

### P0-1 标注链路（对应 D1）

1. `FoundationPlanExtensions.cs`
   - `linear_dimension / aligned_dimension / radial_dimension`：`dimension_style` 从 required 移到 optional；
   - 缺省时：先查 `dimension_styles` 里是否有 `DIM_STYLE`，没有则**自动创建**（text_style 用现有 `Standard`，
     text_height=200，arrow_size=100，overall_scale=1，gap=50）；
   - 保留显式传 `dimension_style` 的能力（用于定义符合国标的箭头/字高）。
2. `FoundationDrawingPlan.cs` 的字段校验：把"缺字段"改为字段级错误
   —— `{"error":"missing_field","field":"dimension_style","op":"linear_dimension","hint":"可选；缺省用自动样式"}`。
3. 新增 plan 级 `dimension_style` 操作（已存在于 schema，需要在 2022 插件侧确认可执行）。
4. 验收：一条 plan 里同时出 `linear_dimension` ×6（井室长宽、壁厚、管中心、阀中心、套管），
   DXF 里 `DIMENSION` 实体数 ≥ 6，且 `dimension_text` 与实际量值一致。

> 本轮交付图 `DIMENSION=0` 就是这个缺口，补上之后才能进"施工图深化"。

### P0-2 事务与栅栏（对应 D2 / D3 / D5）

1. **拒绝路径必须释放 fence**：参数校验失败 → 回滚登记 → fence 释放，不留悬挂请求。
2. **新增 `foundation resolve`**：`--request-id X --discard`（丢弃悬挂写）、`--inspect`（列出悬挂请求与差异）。
3. **request_id 语义**：
   - 同 id + 同参数哈希 → 幂等返回原结果（不报错）
   - 同 id + 不同参数 → `{"error":"request_id_conflict","suggested_id":"...-2"}`
4. **`set_text` readback 只比对声明属性**：`text` 内容 + `handle` + `layer`；其余（宽度因子、对齐点、
   字型缓存差异）不参与比较；失败时输出 `{"expected":{...},"actual":{...},"diff":[字段]}`。
5. **失败语义统一**：`committed_verification_failed` 不再 `write_blocked=true` 永久锁；
   改为"当前 revision 标记为 untrusted，允许 `resolve --discard` 后继续"。
6. 回归测试：把 `pilot-loop-16` 的 `set_text` plan 作为固定用例，要求 `verification=passed`。

### P0-3 会话就绪（对应 D4）

- `apply` 前 `wait_for_session(timeout=90s)`：轮询 `status`，未 ready 则等待；
- 超时才报 `workspace_busy`，并在错误里给出 `wait_seconds_observed`；
- `start --wait-ready` 选项，供脚本使用。

**P0 出口标准**：14 条固定 plan（几何 + 标注 + 改字）在**同一工作区**跑完，
无一次需要换目录，无一次 `write_blocked`。

> 短期绕过（不改代码）：`linear_dimension` 显式传 `"dimension_style":"Standard"`（blank.dwg 已内置），
> 可立即产出真实 DIMENSION 实体，用于先补交付图的标注缺口；正式修复仍按 P0-1 做。

---

## P1 —— 工作流可用性（目标 1 周）

| 编号 | 对应 | 内容 | 验收 |
| --- | --- | --- | --- |
| P1-1 | D6 | `foundation revision --from <dwg>` 或 `apply --new-revision`：save 后自动重开事务 | 一张图 5 次修改只需 1 个目录 |
| P1-2 | D7 | 修 `session_handoff.py`：不再杀新实例；`stop` 与 `status` 语义对齐 | handoff 后 `apply` 连续 3 次成功 |
| P1-3 | D8 | 实现 `export_dxf`（插件侧命令封装）或 `foundation export-dxf`（`accoreconsole /s` + 超时 + 回读校验） | 产出 DXF 且 `DIMENSION/TEXT` 数量与 DWG 一致 |
| P1-4 | D10 | `view` 只读化，不依赖写事务所有权 | 保存后 `view` 首次即可截图 |

---

## P2 —— 表达力与规范（目标 2 周）

| 编号 | 对应 | 内容 |
| --- | --- | --- |
| P2-1 | D9 | `text` 增 `alignment/justify`；`layer`/实体增 `linetype`（含虚线 `DASHED`） |
| P2-2 | D11 | `archive` 支持 preflight（层不存在则创建并设状态） |
| P2-3 | D12 | `capture` 支持 `fit=entities` 自动框；`file_name` 放宽 Unicode |
| P2-4 | D13 | `export_dxf` 固定 `$DWGCODEPAGE` 与内容编码一致（UTF-8 或真 ANSI_936） |
| P2-5 | 附2 | plan 支持 `provenance`（A/B/C + 来源页），让"图集值 / 推断值 / 占位值"可区分 |

---

## 风险与兼容性

1. **schema 兼容**：把 `dimension_style` 从 required 改 optional 是**放宽**，不破坏 v1；
   新增 `provenance` 属于**加字段**，旧 plan 不受影响。任何收紧都要升 v2 并保留 v1 解析器。
2. **fence 语义变更**风险最大：`write_blocked` 一旦从"永久锁"改为"可丢弃"，必须保证
   **不会在 DWG 里留下半完成的写入**。方案是"失败即 revision 作废 + 强制从上一版 dwg 重开"，
   而不是"就地继续写"。
3. **readback 放宽**有掩盖真实错误的可能：只放宽 `set_text`，几何实体（polyline/circle）的 readback 保持严格。
4. **accoreconsole 导出**是外部进程，必须加超时与退出码检查，否则会静默卡住（本轮踩过）。
5. **不要动的东西**：`units=raw_drawing_units`、16 步 / 64 句柄 / 128 KiB 上限。本轮所有图都远未触顶，
   上限不是瓶颈。

---

## 交付节奏建议

- 第 1 天：D1 代码改 + 单测（离线用 plan→JSON 校验器即可验证字段语义）
- 第 2 天：D2/D3 fence 与 request_id
- 第 3 天：D5 readback + 字段级 diff；跑 P0 出口标准
- 第 4-7 天：P1（revision / export-dxf / view）
- 第 2 周：P2 表达力，并用"三管闸阀井施工图版"作为收官验证图（含剖面、配筋说明、标注、设备表）
