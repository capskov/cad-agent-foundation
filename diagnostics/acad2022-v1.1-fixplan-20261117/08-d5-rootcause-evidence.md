> ## ⚠️ 本文结论已被推翻（2026-11-17）
>
> 本文主张的"`extents` 跨会话漂移 / 两份富回执 `DeepEquals` 恒 false"均不成立。
> 实测证据：`pilot-15` 与 `pilot-16` 的 `receipt.before["24B"]` 17 字段零差异，连小数位数都一致；
> 服务端 `verification_result_json` 存的是**瘦报告**（`foundation-plan-readback-v1`），不存在"两份富回执比对"。
>
> **真实根因**：net48（插件）与 net8（服务端）对同一个 double 序列化出不同文本——
> 插件出 17 位（`5619.8324022346369`），服务端出最短往返形式（`5619.832402234637`），
> 于是同一实体算出两个 SHA256，插件在 precommit 算出的 `expected_sha256` 服务端永远复现不出来。
> net48 的真实规则经实测为"G15 能往返就用 G15，否则退回 G17；文本既无小数点也无 E 再补 `.0`"。
>
> 修复：`shared\Execution\FoundationDrawingPlan.cs` 内自实现 `CanonicalText/NumberText/Quote`，
> net8 完全复现 net48 的 canonical 文本与历史哈希（`9D37…`/`D32E…` 双基准 16/16 PASS）。
> 本文以下内容仅作为**已被证伪的推理过程**保留，勿再据此设计修复方案。
> 最新状态见 `09-dev-status-20261117.md`。

# D5 根因证据：set_text 验证失败的字段级回放（P0-A / A4）

> 数据来源：`pilot-loop-16` 的三个 SQLite 账本，**未重跑任何 CAD 写入**。
> 失败请求：`pilot-06d2`（`dwg_apply_plan`，两步：`fix_title` 改标题 + `sub_title` 新建副标题）。

## 一、两份回执的真实关系

服务端账本 `request-ledger.sqlite` 的 `verification_result_json` 是**服务端 staging 后的瘦校验报告**
（schema=`foundation-plan-readback-v1`，字段只有 `passed/failure_codes/source_handles/
plugin_readback_stage_digest/readback_sha256/validator_version`）。

真正的两份完整回执都在插件账本 `native/Dwg/execution/plugin-execution.sqlite` 的 `plugin_jobs` 行里：
- `result_json`：插件执行结束时的富回执（schema=`foundation-plan-receipt-v1`）
- `verification_result_json`：插件读回后二次比对的富回执

对 `pilot-06d2` 做递归 deepdiff：**0 处差异，两份富回执完全一致**。
对照成功案例（pilot-08c / pilot-07a / pilot-05a2）瘦报告均 `passed=true`。

→ 排除"插件自己算了两份不一致的东西"。失败不在字段比对本身。

## 二、服务端为什么判失败

`BoundPluginExecutionGateway.D1.cs:640-646`：
`passed = readback.Passed && pluginJob.verification == "passed"`，
其中 `readback.Passed` 走 `FoundationDrawingPlan.VerifyReadback(result, observed, plan)`：

```csharp
JToken.DeepEquals(value, observed) && VerifyReceipt(value, plan)
|| VerifyArchiveRoundoffRecovery(...) || VerifySaveFontRecovery(...)
|| VerifySaveXrefCacheRecovery(...)  || VerifyTextAnchorRecovery(...)
|| VerifyBlockCacheRecovery(...)
```

DeepEquals 已经为真（两份富回执一致），所以只剩两种可能：
`VerifyReceipt()` 返回 false，或某个 recovery 分支没接住。

## 三、用 Python 复刻 VerifyReceipt 逐条判定

按 `FoundationDrawingPlan.cs:24` 的 `Hash(JToken)`（递归按 key 排序后 JSON 紧凑序列化再 SHA256，大写）
本地复刻，结果：

| 检查项（行号） | 期望 | 实测 | 结论 |
|---|---|---|---|
| schema == foundation-plan-receipt-v1 (136) | 是 | 是 | 通过 |
| plan_sha256 == Hash(plan) (136) | D32EDDE6…8B23 | D32EDDE6…8B23 | 通过 |
| outcome == committed_verified (136) | 是 | 是 | 通过 |
| effects == committed / commit_invoked / commit_returned | true/true | true/true | 通过 |
| error == null | null | null | 通过 |
| **set_text: Hash(before[24B]) == step.expected_sha256 (158)** | **9D3780…E9CA8** | **3FDF01…34E14FA** | **失败点** |
| after[24B].text == "三管共用钢筋混凝土闸阀井" (158) | 相等 | 相等 | 通过 |
| 去掉 text/extents/actual_width/actual_height 后 DeepEquals(old, new) | 相等 | 相等 | 通过 |
| companions（空数组） | 无 | 无 | 通过 |

## 四、根因

`fix_title` 步骤里 `expected_sha256` 是 plan 构造时对句柄 `24B` 整条实体的指纹。
指纹里包含 **`extents`**（由 AutoCAD 字体度量当场算出的浮点包围盒，
例如 `min.y=5619.832402234637 / max.y=5885.754189944134`）。

这个 plan 是在前一个工作区里 `read` 出来后带到 ws16 重放的；
新 AutoCAD 会话打开同一份 DWG 时，字体度量重算得到的 extents 在末位小数上漂移，
整条实体的 SHA256 就变了 → `Hash(before[24B]) != expected_sha256` → `VerifyReceipt` 返回 false。

而文字本身**已经正确改上了**（`after[24B].text` 与目标一致；除 computed 字段外其余字段零差异）。

## 五、为什么 recovery 分支没接住

`VerifyTextAnchorRecovery` 第一条就要求 `proof.schema == "foundation-text-anchor-recovery-v1"`
且 `original.outcome == "committed_readback_failed"`。
本案 proof 是服务端瘦报告（schema=`foundation-plan-readback-v1`），
original.outcome 是 `committed_verified` —— 两个前置条件都不满足，recovery 直接返回 false。

结果就是：**写已经成功落到图纸上，但账本判定 `verification=failed`，
fence 不释放、不 finalize，工作区被永久锁死**（`fence_release_authorized=false`、
`journal_finalized=false`，与账本字段一致）。

## 六、修复方向（P0-B 再动，此处只记证据）

1. `set_text` 的 `expected_sha256` 不要把 `extents` / `actual_width` / `actual_height`
   这类字体度量派生字段算进预检指纹；或预检只比对"稳定身份字段"。
2. `VerifyTextAnchorRecovery` 的前置条件不应硬编码 `outcome=="committed_readback_failed"`，
   否则合法但预检漂移的写入永远走不进恢复路径。
3. `resolve --recover`（见 `07-gpt-v2-ruling.md` 裁决 3）必须能在
   "after 内容正确、仅 before 指纹漂移"这种情形下证据化地恢复，而不是要求重写。

## 七、复现脚本

agent workspace：`verify.py`（本地复刻 VerifyReceipt 逐条判定）、
`diff5.py`（两份富回执 deepdiff）、`diff4.py`（插件账本完整回执导出）。
账本路径：
- `cad2022-test\pilot-loop-16\server\Dwg\request-ledger.sqlite`
- `cad2022-test\pilot-loop-16\native\Dwg\execution\plugin-execution.sqlite`
