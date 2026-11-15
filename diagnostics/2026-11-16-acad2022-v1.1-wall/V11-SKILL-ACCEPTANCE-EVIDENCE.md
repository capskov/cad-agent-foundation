# V1.1 引线标注 Skill 集成与实战验收：成果、卡点、难度、Bug

本文件补齐诊断包中“第一阶段任务”的验收证据说明。V1.1 的目标不是“能画四个对象”，而是：安装 Skill → 找真实案例 → 旧标注可恢复移除 → Agent 独立重排 → 原生落图 → 保存重开 → 图面验收。

## 1. 最终成果（已交付）

| 项 | 结果 | 证据 |
| --- | --- | --- |
| Skill 安装 | `cad-callout-pair-layout` v0.6（HEAD `b4aa1a2`），含 SKILL.zh-CN、agent-planning、input-and-acceptance、transport-contract、nanyi-field-example | 本地 Skill 目录；报告第三节 |
| 真实案例 | 南一路路施25，原图 1,130,162 B，sha256 `4030366378BF401A…`；`before.dwg` 与原图字节相等 | `before.dwg` + 检查点 |
| 标注范围 | 6 个代理图框内共 35 组：ED08=15、ED04=14、ED02=6；其余 ED05/06/07 的 31 组未动 | `scope_35.json` |
| 旧标注处理 | 70 个 leader+text 归档到 `V1.1-旧标注归档-不出图`（off+frozen+非打印），生成 `cleared.dwg` 1,076,771 B | `cleared.dwg`、`scope_35.json` |
| 独立重排 | 35 组按 Skill 规则重新给坐标，不复制旧折点；ED08/ED02 预检零冲突，ED04 剩 2 条保守候选 | `plan-new-35.json`、三份 `*.review.json` |
| 原生落图 | 3 批 35 个 callout，`verification=passed` 并保存：after-a1 1,082,916 B、after-a2 1,090,978 B、after 1,094,472 B | 三阶段 DWG |
| 重开核验 | 全新 AutoCAD 2022 进程只读重开，35/35 通过：普通三点 LEADER、单行 MTEXT 关联反应器、尾段水平、字框、样式/字高/颜色、offset=C−插入点、圆点+填充 | `verify-new-35.json` |
| P1-A | 2022 运行时动态解析 `acdb*.dll` 的 attachAnnotation 入口；强制原生附着实测 `verification=passed`，反应器存在、offset `-0.375,-0.375` 保持 | `p1a-result.json` |
| P1-B | 保存证据失败关闭：写侧缺路径即 false；两侧路径相等；result_json 必须有 saved_path/saved_size/saved_sha256，并做文件存在/长度/SHA 校验 | 源码 + 三批保存记录 |
| 对比材料 | before/cleared/after 并排图、after 全图、ED08/ED04/ED02 分区放大图 | `screenshots/` |

## 2. 当时真正卡住的地方（按难度）

1. **原始 DWG 的确认，不是文件名确认**：公开仓库只有截图和 PDF，没有原始 DWG、字体、CTB。必须先在本机找到文件，再用大小+sha256+内容（代理框、35 组旧标注）证明它是原案例；找不到就必须降级为“替代案例”，不能冒充 35 组复现。
2. **代理图框的内框读取**：6 个 `PP-PM-BJCTK` 代理框能显示，但 extents 可能覆盖外框/图签。不能把一个矩形包围盒当内框；最终靠“连续矩形 + 相邻图幅 + 图签关系 + 区域截图”确认分幅。
3. **“旧标注重做”不是叠加**：必须先盘点 70 个实体的 leader/text/圆点/填充/样式/锚点，再整体归档；archive 只是可恢复归档，不是物理删除，报告里必须写清。
4. **35 组的独立重排**：同侧按 x 降序阶梯、行距 2.9 m、右侧 4 m BottomLeft、出框改左侧 BottomRight；ED04 顶部密集区反复调整后仍有 2 条 `leader_text_candidate`，没有伪称清零。
5. **保存链路把任务切碎**：`--save-to` 到新路径后 `drawing_path_mismatch`，只能“每批一个新会话，从上一批文件继续”；35 组因此分 3 个会话。
6. **没有原生改字 op**：要求“改一组文字再恢复”的稳定性冒烟测试没有受控入口，只能用读回反应器/offset/尾段水平作为替代证据，报告里明确列为未执行。
7. **PDF 没有接上**：V1.1 当时用 CAD 原生视图截图做对比，没有 PDF；这也是本轮砖墙任务首次尝试 `plot_pdf` 的原因。

## 3. 修掉的 Bug（V1.1）

- BUG-01：callout 创建不支持 `text_layer` / `annotation_offset`，C 会塌到文字插入点 → 增加 layerOverride、写 offset 并 `EvaluateLeader()`，读回校验。
- BUG-02：`annotation_offset` 传 3 维被 `2D point required` 拒绝 → 计划统一 2 维。
- BUG-03：net8 服务端计划校验器未同步共享 schema → 重编 server 并重打包。
- BUG-04：`Number(step,"gap")` 编译错误 → 改为 `Number(step["gap"])`。
- BUG-05：`--save-to` 后会话绑定失效 → 客户端同步 session 路径；**未根治**，服务端环境变量仍固定旧路径。
- BUG-06：ED04 密集区弯曲/穿字候选 → 调行序与侧别，最终剩 2 条待人工判读。

## 4. 仍未完成（诚实清单）

- BUG-05 根治（保存后自动重启+rebind）。
- P1-B 负向用例：写侧缺路径、文件被移走/覆盖、同库多图纸，均未跑完。
- P2-C：run_regression 断言加强、真正独立关闭重开、区分预提交拒绝与失败作业。
- P2-D：`NetFrameworkAssemblyResolver` 对 AppDomain 的影响面审查。
- 原生改字再恢复的稳定性测试。
- ED04 两条候选、2 个未归档旧圆点。
- PDF/CTB 出图（本轮砖墙任务正在补，但尚未成功）。

## 5. 为什么这些材料现在才补

第一轮诊断包只带了“报告 + Buglog + 砖墙计划”，没有带 35 组原始清单、核验 JSON、P1-A 结果、检查点和截图。现在按“成果 / 卡点 / Bug / 未完成”四类补齐，外部审查可以直接按文件复核，不必相信摘要。
