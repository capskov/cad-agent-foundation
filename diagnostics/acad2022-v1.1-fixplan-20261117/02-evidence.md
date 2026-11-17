# 02 执行时间线（22 个工作区，一次真实出图）

时间基准：2026-11-16 23:40 → 2026-11-17 01:30（UTC+8）。
所有路径以 `cad2022-test/` 为根。

## 一、水表井阶段（作废，1-7）

| 工作区 | 内容 | apply | 结果 | 备注 |
| --- | --- | --- | --- | --- |
| pilot-loop-01 | v1 水表井 | — | 未 apply | 口径错误，作废 |
| pilot-loop-02 | handoff 产物 | — | 实例已死 | 见 D7 |
| pilot-loop-03 | v1 原地改图 | apply-v2.json | `committed_verification_failed` | 栅栏未释放，工作区报废 |
| pilot-loop-04 | v2 | apply-v2.json | committed_verified | 抓图 pilot02_full.png |
| pilot-loop-05 | v3 | apply-v3.json | committed_verified | 抓图 pilot03_full.png |
| pilot-loop-06 | 空目录 | — | — | 未使用 |
| pilot-loop-07 | v4 | apply-v4.json | committed_verified | 抓图 pilot04_full.png |

## 二、闸阀井 V1.0 → V2（8-14）

| 工作区 | 内容 | apply | 结果 | 备注 |
| --- | --- | --- | --- | --- |
| pilot-loop-08 | 尺寸标注 plan | apply-v5.json | **`rejected_before_submission`** | **D1 证据**：`unexpected or missing plan fields` + held fence |
| pilot-loop-09 | V1.0 base | apply-5a.json / apply-5a2.json | **`Request ID already belongs to different parameters`** / committed_verified | **D3 证据** |
| pilot-loop-10 | 阀 + 支墩 | apply-5b.json | committed_verified | 抓图 pilot05b_full.png |
| pilot-loop-11 | 净 4800 base | apply-6a.json | **`This workspace already has a CLI or MCP session`** | **D4 证据** |
| pilot-loop-12 | 同上重试 | apply-6a.json | committed_verified | 等 start 结束即成功 |
| pilot-loop-13 | 阀/支墩 | apply-6b.json | committed_verified | |
| pilot-loop-14 | 收口 | apply-6c.json | committed_verified | 抓图 pilot06_full.png |

## 三、set_text 事故与 V3（15-19）

| 工作区 | 内容 | apply | 结果 | 备注 |
| --- | --- | --- | --- | --- |
| pilot-loop-15 | set_text 改标题 | apply-6d.json（**0 字节**） | 进程被打断，输出空文件 | 工作区报废 |
| pilot-loop-16 | 同操作重试 | apply-6d.json（25967 B） | **`foundation_plan_readback_mismatch` / `plugin_stage_not_passed`** | **D5 完整证据**，见 `logs/apply-6d-ws16-summary.json` |
| pilot-loop-17 | V3 base（重建整套） | apply-7a.json | committed_verified | |
| pilot-loop-18 | V3 阀/支墩 | apply-7b.json | committed_verified | |
| pilot-loop-19 | V3 收口 | apply-7c.json | committed_verified | 抓图 pilot07_full*.png |

## 四、V4 交付（20-22）

| 工作区 | 内容 | apply | 结果 |
| --- | --- | --- | --- |
| pilot-loop-20 | V4 base（真实法兰面长度） | apply-8a.json | committed_verified |
| pilot-loop-21 | V4 阀/支墩 | apply-8b.json | committed_verified |
| pilot-loop-22 | V4 收口（交付图） | apply-8c.json | committed_verified；`pilot-08c.dwg/.dxf` + `outputs/pilot08_full.png` |

## 五、统计（v2 更正，2026-11-17）

> 原 v1 写"22 次 apply、17 成功、5 失败"是错的：把**工作区数**当成了 apply 次数。
> 下面是按 `pilot-loop-*/apply-*.json` 逐个解析的真实统计（20 个文件，含 1 个 0 字节）。

| 结果（按文件解析） | 次数 | 明细 |
| --- | --- | --- |
| `committed_verified`（verification=passed） | **14** | ws04/05/07/09-5a2/10/12/13/14/17/18/19/20/21/22 |
| `committed_verification_failed` | **2** | ws03（原地改图）、ws16（set_text） |
| 仅报错未提交（`error` 字段，无 status） | **2** | ws09 apply-5a（request_id 冲突）、ws11 apply-6a（会话占用） |
| `rejected_before_submission` | **1** | ws08（标注 plan） |
| 0 字节（进程被中断） | **1** | ws15 |
| **合计 apply 尝试** | **20** | — |

- **工作区 22 个 ≠ apply 20 次**：ws01/02/06 没有 apply 记录（ws02 是 handoff 试验、ws06 空目录）。
- 因缺陷额外消耗的工作区（按 v2 评审口径重算，2026-11-17）：
  - **纯报废 / 不可恢复**：ws08（D1 标注 plan 直接被拒）、ws11（D4 控制台路径错误）、
    ws15（进程中断 0 字节）、ws16（D5 fence 锁死）= **4 个**。
  - ws17/18/19 是 V3 重建的真实构建过程，**不算纯报废**，只算"额外重建 3 次"。
  - 弃用旧口径"6 个 / 27%"。
- 每张图的常态成本：base + 设备 + 收口 = 3 次 apply ≈ 3 个目录 + 3 次 install/start。

## 六、外部（GPT）复核时间线

1. v1 DXF → 判「几何自洽但不合图集规范」
2. v2 → 判「核心几何通过；支墩到上内壁只剩 350mm；管道必须用实际外径」
3. V1.0 闸阀井 → 判「V2几何方案内部讨论可以交付；完整内部评审尚不能收口」
4. V3 → 判「**V3 可以交付**，正式定位《内部方案评审版 V3.0》」
5. V4 → 判「**V4 可以交付**；457/406/292 均正确；井室主体无需再改」
   附验收边界：DN200 保温外径与井室净高仍需施工图阶段结合实际设备核定。
