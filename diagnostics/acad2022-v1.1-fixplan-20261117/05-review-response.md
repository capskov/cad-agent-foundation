# 05 对评审意见的逐条回应（v2 修订）

评审对象：commit `db7f432`（分支 `acad2022-v1.1-fixplan-20261117`）。
本文记录**接受 / 部分接受 / 反驳**及本轮新查到的证据。`db7f432` 保留不动，本文与后续改动作为新提交。

---

## 1. 构建源码基线不一致 —— **接受，且已查实（见 06-build-baseline.md）**

评审说：`plugin-acad22` 的 csproj 与归档 snapshot 不一致。实际情况比这更严重：

- `_review-repo`（本仓库）的 csproj **2257 B**，缺 `..\plugin-acad26\*.cs`、`..\plugin-journal-net8\*.cs`、`..\shared\**\*.cs` 三条 Link
  → **用本仓库根本编不出正在运行的插件**。
- 实际构建树是 `src\cad-agent-foundation-main\third_party\...`（csproj 3614 B，带三条 Link + SQLite/System.Text.Json 包）；
  `build-logs\rc5-plugin22.log` 明确显示编译目标是这棵树，0 错误。
- 连 `FoundationDrawingPlan.cs` / `FoundationPlanExtensions.cs` / `FoundationDxfExport.cs` 三个关键文件在两棵树里 **哈希都不同**。

**因此修正**：D1 的根因引用行号从 `FoundationPlanExtensions.cs:43-45`（镜像）
改为构建树路径（同一逻辑在构建树中同样成立，已复核）。

## 2. 统计数字对不上 —— **接受，是我算错了**

按原始 apply 文件逐个统计（20 个非空/空文件全列，脚本可复现）：

| 结果 | 次数 |
| --- | --- |
| `committed_verified`（verification=passed） | **14** |
| `committed_verification_failed`（verification=failed） | **2**（ws03、ws16） |
| 仅报错、未提交（`error` 字段） | **2**（ws09 apply-5a、ws11 apply-6a） |
| `rejected_before_submission` | **1**（ws08） |
| 空文件（进程被中断） | **1**（ws15） |
| **合计 apply 尝试** | **20** |

原写"22 次 apply / 17 成功 / 5 失败"是把**工作区数（22）当成 apply 数**，且把 `verification_pending` 当成成功。
已在 `02-evidence.md` 更正。**22 个工作区 ≠ 22 次 apply**（ws01/02/06 没有 apply 记录）。

## 3. D2 悬挂 fence —— **接受，撤回因果结论**

评审指出：ws08 的 `next_action`（`the held fence belongs to an earlier verified-but-unpersisted write`）
只能证明"存在更早的未持久化写"，**不能证明是本次参数拒绝制造的**。

**修订**：D2 从"参数拒绝路径泄漏 fence"降级为**未证实（needs controlled reproduction）**，
证据等级：C（仅有一条间接文案）。要定性，必须做受控实验：
同一新工作区，先制造一次"通过校验但未持久化"的写，再拒绝一次参数，然后观察 fence 归属。

## 4. D3 request_id 冲突 —— **接受，不是 BUG**

同 id + 不同参数必须拒绝，这是正确的幂等保护。**修订**：D3 从"S1 缺陷"降为
"**错误提示/文档缺陷**（应给出 `request_id_conflict` 错误码 + 建议新 id）"，优先级 P2。

## 5. D5 set_text —— **接受：不猜根因，先做字段级 diff；且不放开 fence**

评审关键提醒：`VerifyReadback()` 常规路径是"原始回执与独立读回严格相等"，随后才是若干恢复分支
（archive roundoff / save font / save xref cache / **text anchor** / block cache）。我原来把
"文字宽度、字型属性"当根因是**推测**，没有证据。

本轮补到的硬事实（`pilot-loop-16/apply-6d.json`）：

```
job.verification        = passed          ← 插件侧通过
verification            = failed          ← 服务端 D1 readback 失败
verification_message    = foundation_plan_readback_mismatch,plugin_stage_not_passed
write_fence_released    = false
write_fence_finalized   = false
readback_revision       = 4
plugin_sequence         = 4
outcome_summary.plugin_verification = passed / server_verification = failed
```

**证据缺口**：服务端比较用的 `verification_result_json` **没有落盘**（保存的 JSON 里没有该字段），
所以现在无法给出字段级 diff —— 这正是下一轮要补的第一件事。

**修订后的做法**（同意评审）：
1. 先让"原始回执 / 独立读回"两份 JSON 都落盘，再做逐字段 diff，**不预先放宽任何字段**；
2. **写入已提交但验证失败时，不自动放 fence**；改为"保留数据 + 标记 revision 不可核验 + 提供人工/工具恢复路径"，
   恢复动作显式、可审计；
3. 只有在拿到 diff 之后，才判断是恢复分支前提不满足，还是真不一致。

## 6. D6 一图一保存 —— **接受：是设计约束，不是加个参数**

评审指出：这是"事务与图纸身份绑定"的既有约束。**修订**：D6 归为**设计决策项**，
需要明确"revision 生命周期"（谁创建、谁可写、失败后归谁），不能用 `--new-revision` 开关糊过去。

**同时修正 P0 出口标准**（评审指出的自相矛盾）：不再要求"14 条 plan 在同一工作区跑完"，
改为"**一条 plan 链最多跨 1 次 revision 切换**，全程无 `write_blocked`、无需换新目录重来"。

## 7. D8 export_dxf —— **接受：功能已存在，先查为什么 2022 不执行**

源码有 `FoundationDxfExport.cs`（构建树 4347 B / 41 行）。但对运行中的插件 DLL 做符号扫描：

| 符号 | 插件 DLL | 服务端 DLL |
| --- | --- | --- |
| `export_dxf` | 0 | 1 |
| `FoundationDxfExport` | 0 | 0 |
| `INSUNITS` | 0 | 1 |

→ 2022 插件的构建产物里**没有这段实现**。所以 D8 改为"**装配缺口**"，
并且附加一条具体门槛：`INSUNITS` 必须为 0 或 6，毫米图（4）会被直接拒绝。
**本轮未实测该图的 INSUNITS**（DXF 头里没有 `$INSUNITS` 记录），列为待测项，不写成结论。

## 8. 关于"严格事务保护不能削弱" —— **完全接受**

原 `03-fix-plan.md` 里"`committed_verification_failed` 不再 `write_blocked` 永久锁"这句话表述有误，
容易被读成"体验优先、牺牲安全"。**修订为**：
- fence 不自动释放；
- 新增显式恢复通道（`resolve --inspect` 看 diff、`resolve --recover` 重读回写、`resolve --discard` 从上一版 dwg 重开）；
- 所有恢复动作写入审计日志，并在 `status` 里可见。

## 9. 评审之外、我自己又发现的两处，一并记录

1. **归档 snapshot 的 csproj（3689 B）与构建树（3614 B）也不同** —— 三份 csproj、三种内容，谁都可能被当成"真源"。
2. **`$DWGCODEPAGE=ANSI_936` 与 TEXT 实际 UTF-8 内容不一致**（D13）本轮依旧存在，
   且 accoreconsole 导出的 DXF 里找不到 `$INSUNITS`，说明导出脚本对头的控制很弱，需要一起修。
