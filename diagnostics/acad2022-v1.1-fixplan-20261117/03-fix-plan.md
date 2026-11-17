# 03 修复计划（v2，按评审意见重排）

**原则**
1. 先"能可信地写"，再"能写得好看"；**先统一基线，再动任何代码**。
2. **严格事务保护不削弱**：fence 不自动放、失败不静默继续写；恢复必须显式、可审计。
3. v1 契约（`foundation-drawing-plan-v1`）只放宽、不收紧。

> v1 版本的"P0=标注优先"已被评审推翻：标注只是缺能力，而基线不一致会让所有改动落错文件。
> 结论：P0-A 基线 → P0-B 写入状态确认 → P0-C 标注。

---

## P0-A 统一基线与证据（先做，预计 0.5 天）

| # | 动作 | 验收 |
| --- | --- | --- |
| A1 | 明确**唯一构建源** = `src\cad-agent-foundation-main`；`_review-repo` 降级为只读镜像并在 README 声明 | 镜像顶部写清"非构建源，仅为评审快照" |
| A2 | 补"源码↔二进制"对照：构建树目录哈希、plugin dll sha256、server dll sha256（本轮已核对 plugin `77FC7C3E…` = manifest 一致） | `foundation doctor` 能打印三者 |
| A3 | 实测 `INSUNITS`（只读），确认 `export_dxf` 的 `∈{0,6}` 门槛是否会命中毫米图 | 一个布尔结论 + 证据文件 |
| A4 | **D5 证据补齐**：让服务端比较用的 `verification_result_json` 与 `result_json` 同时落盘，对 `pilot-loop-16` 做字段级 diff | 得到"哪几个字段不一致"的清单（不允许猜） |
| A5 | **D2 受控实验**：新工作区 → 一次"通过校验但未持久化"的写 → 一次参数拒绝 → 观察 fence 归属 | 证实或证伪，写进文档 |
| A6 | 统计口径固定：以 `apply-*.json` 文件为准（20 次 / 14 成功），工作区数不参与 | 与脚本一致 |

---

## P0-B D5：写入后状态确认闭环（最关键，预计 1.5 天）

硬事实：`job.verification=passed`、服务端 `failed`、`verification_message=foundation_plan_readback_mismatch,plugin_stage_not_passed`、
`write_fence_released=false`、`write_fence_finalized=false`、`readback_revision=4`。两份回执都在，但只有一份落盘。

1. **先测量**：按 A4 拿到 diff，判断是"恢复分支前提不满足"还是"真不一致"。
2. **不预放宽**：`VerifyReadback` 的严格相等保持；恢复分支（text anchor 等）在前提不满足时**给出原因**，而不是静默 false。
3. **失败语义**：`committed_verification_failed` → revision 标记 `unverifiable`，fence **保持**，提供三条显式恢复命令：
   - `resolve --inspect`：列出悬挂请求 + 两份回执 diff
   - `resolve --recover`：从磁盘重读回填，重建可核验状态（**不重放写入**）
   - `resolve --discard`：以上一版 dwg 重开 revision（原文件保留，不覆盖）
4. **禁止重放**：已提交的写永不自动重发（把现有 `next_action` 提示变成硬约束）。

出口：`pilot-loop-16` 的 `set_text` plan 可复现；要么通过，要么失败时能拿到字段级 diff 且能安全恢复。

---

## P0-C D1：标注能力（预计 1 天）

1. **保持显式标注样式**（不硬编码自动样式）：`dimension_style` 仍必填，错误改为字段级
   `{"error":"missing_field","field":"dimension_style","op":"linear_dimension"}`。
2. 文档补：`blank.dwg` 已内置 `Standard` / `Annotative`；plan 写 `"dimension_style":"Standard"` 即可。
3. **必须实测**：六条真实 DIMENSION（井室 5000×4800、壁厚 300、管中心 1000/1400/1400、阀中心 X=2800、套管、支墩）
   出图 + 回读；DXF 里 DIMENSION 实体数 ≥ 6，量值与几何一致。
4. 只有实测通过后，文档才允许写"标注能力可用"。

出口：图上出现真实 DIMENSION 且回读 passed；`DIMENSION` 数不为 0。

---

## P1 工作流可用性（预计 1 周）

| # | 缺陷 | 内容 | 验收 |
| --- | --- | --- | --- |
| P1-1 | D4 | `apply` 等待 session ready（≤90s），超时 `workspace_busy` + 实际等待秒数 | 连续 10 次"start 后立刻 apply"全成功 |
| P1-2 | D6 | **先做 revision 生命周期设计**（谁创建、谁可写、失败归谁），再实现；不是加开关 | 一张图 5 次修改只需 1 次 revision 切换 |
| P1-3 | D7 | 修 `session_handoff.py`：不连带杀新实例；`stop`/`status` 语义对齐 | handoff 后连续 3 次 apply 成功 |
| P1-4 | D8 | 查清 2022 插件为何没编进 `FoundationDxfExport`（构建/装配），先归因再决定改法；再议 `INSUNITS` 门槛 | 2022 上 `export_dxf` 成功产出 DXF，实体数与 DWG 一致 |
| P1-5 | D10 | `view` 只读化，不依赖写事务所有权 | 保存后 `view` 首次即出图 |

---

## P2 表达力与规范（预计 1–2 周）

| # | 缺陷 | 内容 |
| --- | --- | --- |
| P2-1（原 D3） | request_id 提示 | `request_id_conflict` 错误码 + 建议新 id（**不是功能缺陷**） |
| P2-2 | D9 | `text` 增 `alignment/justify`；`layer`/实体增 `linetype`（虚线） |
| P2-3 | D11 | `archive` preflight（层不存在则创建并设状态） |
| P2-4 | D12 | `capture` 支持 `fit=entities`；`file_name` 放宽 Unicode |
| P2-5 | D13 | `export_dxf` 固定 `$DWGCODEPAGE` 与内容编码一致；补 `$INSUNITS` 写入 |
| P2-6 | 附2 | plan 支持 `provenance`（A/B/C + 来源页），区分"图集值 / 推断值 / 占位值" |

---

## 验收标准（v2 修正，解决与 D6 的冲突）

- **P0 出口**：一条 plan 链**最多跨 1 次 revision 切换**，全程 `write_blocked=false`、`verification=passed`，
  失败可诊断（有字段级 diff）、可恢复（三条 resolve 命令之一）。
  ~~原标准"14 条 plan 在同一工作区跑完、无需换目录"~~ → 与 D6 现状冲突，作废。
- **交付级出口**：施工图深化版（剖面 + 配筋 + 设备表 + 标注），一张图从 plan 到交付全程无需人工介入 CAD。

---

## 风险

1. 基线统一前改代码 = 白改（本轮已踩到：镜像与构建树三个关键文件哈希不同）。
2. `fence` 语义调整风险最高 → 坚持"不自动放、只提供显式恢复"。
3. `INSUNITS` 门槛是写死的行业假设（只认米/无单位），毫米图是常态；需产品决策：放宽到 0/4/6 还是强制换算。
4. `accoreconsole` 导出是外部进程，必须带超时 + 退出码检查，否则静默卡死（本轮踩过）。
5. **不要动**：`units=raw_drawing_units`、16 步 / 64 句柄 / 128 KiB 上限（本轮远未触顶）。
