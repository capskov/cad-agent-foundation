# 技术复核：客户端写入路径由谁负责 save 与 finalize

- 编号：TR-2026-11-15-02
- 状态：已观察到冲突，方案未定（需要方向判断）
- 严重程度：中高（影响 2022 写入闭环能否通过现成客户端完成；不涉及数据损坏）

## 1. 问题摘要

2022 上新增"保存前置守卫"后，`job_write_finalize` 要求同一图纸先有已完成的 `save_drawing`。但服务端现有的自动收口逻辑只存在于 D1 路径（`BoundPluginExecutionGateway.D1.cs` 第 260 行调用 `job_write_finalize`）；Foundation 的 `dwg_apply_plan` 走的是通用管线，**不自动调用 finalize**。

实测（`cad2022-test\e2e-work3`，客户端 `read` 路径成功；`apply` 未做）：

- 客户端 `apply` 提交写入后，服务端返回 `completed/committed/passed`，但 fence 仍持有（`write_fence_released=false`），因为没有 finalize。
- 若此时客户端/服务端补发 finalize，会被守卫拒绝 `write_requires_completed_save`（尚未保存）。
- 也就是说，2022 上"一次 apply 就完成"的旧路径在该开关下**不再成立**，必须显式插入保存。

## 2. 版本与环境

| 项 | 值 |
|---|---|
| AutoCAD 2022（R24.1.173.0.0），插件 `Bimwright.Dwg.Plugin.Acad22.dll`（net48，MVID `52abb6a7-…`） |
| 客户端 | `client/foundation.py`（已加 `--acad-year`），打包件 `artifacts/packages/20261115-123419` |
| 环境变量 | `BIMWRIGHT_FOUNDATION_WRITE_ENABLED=1`、`BIMWRIGHT_FOUNDATION_SAVE_ENABLED=1` |
| 涉及源码 | `client/foundation.py`（`Gateway.apply`、`query`）、`server/Execution/BoundPluginExecutionGateway.D1.cs`、`shared/Infrastructure/CommandDispatcher.cs`（`DispatchWriteFinalize`、`DispatchSaveDrawing`） |

## 3. 复现步骤

1. 用打包客户端 `start --write`，工作区 `e2e-work3`。
2. `apply` 一个单线计划（`foundation_apply_plan`）。
3. `status --request-id <id>` 观察 `write_fence_released`。
4. 再 `apply` 第二个计划，观察是否 `write_fence_conflict`。
5. 手动 `save`（客户端 `save` 动作）后再 `status`。

## 4. 预期 / 实际

| 步骤 | 预期 | 实际（由插件/服务端行为推断，待逐步跑实） |
|---|---|---|
| apply 后 | 要么自动收口（释放 fence），要么明确告知"待保存" | 停留在 `ver=passed` 且 fence 持有，客户端不提示 |
| 第二次 apply | 允许或被明确阻挡 | 被 `write_fence_conflict` 挡住（已有 7.3 实测） |
| 手动 save 后 | 可 finalize | 需要显式 finalize 命令，客户端当前无此动作 |

## 5. 候选方案

| 方案 | 做法 | 风险 | 评价 |
|---|---|---|---|
| A. 客户端编排 | `apply` 成功后自动 `save` + `finalize`（2022 保存开关下） | 客户端改变了"写完即返回"的语义；保存到哪需要参数，不能静默默认路径 | 语义最清晰，但需要客户端新增显式 `--save-to` |
| B. 服务端按宿主能力编排 | discovery 增加 `save_before_finalize: true`，服务端在 Foundation 写成功后先发 `save_drawing` 再 finalize | 服务端要生成保存路径，和"拥有副本"的既有约束耦合 | 对 Agent 透明，但把落盘决策搬到服务端 |
| C. 显式两步协议 | `apply` 返回 `verified_pending_save`，由 Agent 主动 `save` → `finalize` | 需要 MCP 工具面新增两步或复用现有动作 | 最贴近安全语义（不替用户决定落盘），但调用方要多一步 |
| D. 仅在校验场景关闭守卫 | 保持 2026 语义 | 放弃 2022 的静默丢失防护 | 不可接受 |

推荐 **C + A 的组合**：默认 `apply` 只到"已验证"，返回明确状态与 `next_action: save_drawing → job_write_finalize`；客户端 `apply --save-to <path>` 提供一步式便捷路径，底层仍是两步。

## 6. 希望独立判断的问题

1. `apply` 的完成语义应该是"已验证"还是"已落盘"？若为"已验证"，客户端 UI 是否会误读为成功。
2. 保存路径由谁决定：调用方显式给出，还是沿用"一次会话一个自有副本"的既有约定。
3. 2026 是否也应获得"待保存"状态（即守卫语义对齐），还是继续让 2026 保持"finalize 即完成、落盘由外部负责"。

## 7. 当前证据

- `e2e-work3`：客户端 `doctor`、`read` 成功（读回 handle 222）。
- `run-tx3/tx5`：finalize 后未落盘的静默丢失证据。
- `run-tx9/tx10/tx12`：守卫与指纹绑定后的正确行为。
- 服务端 `job_write_finalize` 调用点仅存在于 D1 路径（`grep` 结果）。
---

## 更正（2026-11-15 晚，实测推翻原结论）

原文判断"Foundation 通用管线**不会**自动 finalize"是**错的**。server\Execution\BoundPluginExecutionGateway.D1.cs 的 GetD1WriteJobAsync 明确存在自动 finalize 分支：写作业 committed/passed 后，服务端会带 6 参数调 job_write_finalize 并释放 fence。

因此 2022 上的真实冲突不是"没有 finalize"，而是**"自动 finalize 被 write_requires_completed_save 守卫拒绝"**：

- 未开保存：job 停在 completed/committed/passed，write_fence_released=0，fence 仍持有；
- 开保存后仍不落盘：同上（守卫要求"同 journal、同指纹、带 saved_path 的已完成保存行"）；
- 显式保存后重查：finalize 通过，fence 释放（c2-b4 / c2callout）。

最终实现采用的是原文"推荐 C+A"的变体，但保存改走**独立受管读作业** save_drawing（dwg_foundation_save_drawing），而不是计划里的 save op —— 因为计划里的 save 是写作业，会与持有的 fence 冲突。

证据：docs\ACAD2022-RC2-TEST-LOG.md 第 3、5 节；工作区 cad2022-test\rc2-b4、cad2022-test\rc2-callout。