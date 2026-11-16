# CAD2022 智能底座：当前状态与未关闭问题（唯一有效版本）

更新：2026-11-16
分支：`acad2022-v1.1-diagnostics`
适用范围：ACAD2022 V1.1（南一路旧标注重做） + 砖砌围墙 A3 截面图 + `cad-callout-pair-layout` 2022 版 Skill。
阅读规则：**本文件是当前唯一有效的状态清单。** `diagnostics/archive/` 下所有旧报告只作历史证据，不再代表当前结论；旧版 callout 规则（`annotation_offset=(0,0)`、`text_gap=0.012`）均已过时。

---

## 1. 当前结论

- 南一路“旧标注重做”已在真实图上完成：35 组旧标注、70 个 leader+text 实体归档；新 35 组 callout 分 3 批落图，独立重开 **35/35** 通过。
- 砖砌围墙 A3 截面图已完成：六组 45°/135° callout，文字在横线之上，文字底边与横线空 **1/3 字高（0.02）**；A3 PDF 打印 `committed_verified`。
- 当前底座**还不能称为“生产默认”**：SaveAs 后会话不能连续、页面设置 finalize 未收口、旧标注只有“归档”没有“安全物理删除”、以及若干负向回归和 UX 契约问题仍存在。
- 已关闭的问题不再在本文展开；证据在 `archive/`，需要审计时按链接回看。

### 1.1 当前交付物

| 文件 | 大小 | SHA256 |
| --- | ---: | --- |
| `evidence-gap-third/s11-final5.dwg` | 37,748 B | `5E5ADE22CE55C98A0EA16F6092134D9739896D5DE2BF4625556A47011A2B5D45` |
| `evidence-gap-third/s11-page5.dwg` | 37,681 B | `64BAAF6C37091CC3A8DD61706D588A049AE2D58690137C51329769467CA2B46E` |
| `evidence-gap-third/wall-section.pdf` | 21,709 B | `09FB0546D19E2E61B46C9EBFC86FCF510157EA29E6F2D8A9CEF43B5490674DA8` |
| `evidence-gap-third/v11_wall_pdf_callout_v5.png` | 118,441 B | `5BDA295210DF3F3E16763025E8DF9B61F429A4DF91E096F09B2815BE7BE2A88A` |
| `evidence-gap-third/SKILL.md` | 10,693 B | 当前 1/3 字高规则版本 |

PDF：A3 1191×842 pt；六组 callout 无重影；底部 C15/室外地面平行不交叉。

---

## 2. 旧标注清理：做到了什么，没做到什么

### 2.1 已完成的部分（南一路路施25真实图）

| 项 | 事实 |
| --- | --- |
| 范围 | 6 个 `PP-PM-BJCTK` 代理图框内 35 组：ED08=15、ED04=14、ED02=6 |
| 旧实体 | 35 组共 **70 个 leader+text** |
| 处理方式 | 全部移动到归档层 `V1.1-旧标注归档-不出图`，层状态 **off + frozen + 非打印** |
| 执行批次 | 两个工作区 `v11-arch-a1`（30 targets）、`v11-arch-a2d`（40 targets），journal 均 `committed/verified` |
| 结果 | `cleared.dwg` 1,076,771 B；新会话核验旧标注不在可见图面 |
| 新标注 | 35 组重新排布、3 批落图、保存、独立重开 35/35 通过 |
| 审计证据 | `archive/2026-11-16-acad2022-v1.1-wall/evidence/v11-data/scope_35.json`、`plan-new-35.json`、`verify-new-35.json`、`screenshots/` |

**这不是物理删除。** 归档层里的实体仍在 DWG 内，只是不可见、不可打印；这是当前 foundation 计划里可控、可恢复的做法。报告和验收都按“归档”表述，不按“删除”表述。

### 2.2 没做到 / 仍是残留问题

**OPEN-01｜旧标注只有“归档”，没有“安全物理删除”**
- 当前 foundation 计划暴露的是 `archive`（整组移动到隐藏层），没有通用 `erase`/物理删除入口。
- 第三方工具目录里能看到 `dwg_erase_entities`，但它不属于当前 2022 foundation 写入路径，不能拿它冒充已验收能力。
- 需要专家决定：生产上是否允许“先归档、再物理删除”，还是永久保留可恢复归档层？如果要删除，删除前的组完整性、引线/文字/圆点/填充关联如何原子校验？

**OPEN-02｜旧圆点没有全部归档**
- 35 组旧标注的 3 个圆点中，只有 1 个随组进入归档层，另 **2 个仍留在原层**。
- 影响：图面可能残留旧圆点；本次没有单独处理，也没有伪称“全部清理”。
- 需要决定：圆点是否必须作为 callout 组成员纳入 archive 目标；如何按锚点匹配圆点和引线？

**OPEN-03｜“清旧图→重排”没有在砖墙这次复现**
- 砖墙这次是从 `s3-hztxt.dwg` 开始，apply 前读回 `Leader=0 / MText=0`，属于**新建标注**，不是“清理旧图再重排”。
- 所以砖墙图不能证明“旧标注清理”链路在新图上复现；它只证明“无旧标注时不会叠加”。
- 旧图清理的真实证据仍在南一路案例，不在砖墙目录里。

**OPEN-04｜ED04 密集区仍有 2 条人工判读候选**
- `co-1D7DA ↔ co-1EB26`、`co-1EB26 ↔ co-1D811` 两条 `leader_text_candidate`。
- 无文字重叠、无引线交叉、无越框，但“引线邻近字框”不能自动判为零风险。
- 需要人工图面确认，或由专家给出“邻近字框”的可接受阈值。

---

## 3. 底座当前未关闭问题

### P0｜阻塞“生产默认”的问题

**OPEN-05｜SaveAs 后同一会话不能继续（V1.1 BUG-05 未根治）**
- `--save-to` 到新路径后，服务端环境变量/绑定快照仍指旧 `work.dwg`，同会话 `status/apply` 报 `drawing_path_mismatch`。
- 同一工作区在 SaveAs 后重启会 fail-closed（`D1 ownership ... no matching ledger`）；原地覆盖当前 `work.dwg` 触发 `eFilerError`。
- 当前唯一可行路径：**SaveAs 到新路径 → 换新工作区从保存文件继续**。没有绕过 fence，也没有自动改写身份绑定。
- 影响：35 组被切成 3 个会话；长任务不能连续跑；尚未形成可复用恢复脚本。
- 需要专家回答：保存后自动重启服务端并 rebind，是否比新增 `rebind --drawing` 更安全？两者对 fence、ownership、ledger 的影响分别是什么？

**OPEN-06｜A3 页面设置 finalize 仍未收口**
- `s11-page5.dwg` 的 `page_setup` 请求返回：`verification=failed`、`persisted=false`、`save_succeeded_but_finalize_pending`。
- 同一图独立 `plot_pdf` 返回：`committed_verified`、`plugin_verification=passed`、`server_verification=passed`。
- 也就是说：**PDF 产物和打印链路通过，页面设置自身的 finalize 未确认**；不能据此宣布“页面设置闭环”。
- 需要专家回答：为什么 page_setup 保存成功但 finalize 查询失败？是请求身份、readback 校验，还是 page_setup 的 verification 与 plot 的 verification 用了不同判据？是否可以把“PDF 打印已 verified”作为 page_setup 的替代验收，还是必须单独修 finalize？

### P1｜能力/契约/图面问题

**OPEN-07｜16 步上限 + ASCII 步骤 ID 仍是硬契约，但缺少自动分批与明确前置提示**
- 39 步计划被 `1..16 steps required` 拒绝；`layer-图框` 被 `step IDs must be unique identifiers` 拒绝。
- 现有处理是人工拆成 15/9/15/6 步，计划 ID 全改 ASCII；这是契约，不是底座 Bug。
- 缺的是：客户端在提交前检查步数/ID 并给出明确错误码；以及按 hatch 边界依赖自动拆批，而不是让使用者在失败后自己重写。
- 需要专家回答：保持 16 步/ASCII 契约不变的前提下，自动分批应放在客户端、服务端还是 Skill 计划生成器？

**OPEN-08｜首次 apply 不带 save_to 会持有 fence，属设计行为，但用户引导不足**
- 现象：`verification_pending / verification=passed`、`persisted=false`、`next_action=verified_not_persisted`；下一次 apply 被 held fence 拒绝（`fence_held_by_earlier_unpersisted_write`）。
- 这不是 Bug，是 fence 设计；但如果使用者把“passed”误读为“完成”，就会反复提交、反复被拒。
- 需要补：明确错误文案、`status` 里的 fence 归属说明、以及“pending 保存恢复”命令。当前没有可复用恢复命令。

**OPEN-09｜15 秒前台转后台是执行环境限制，异步/检查点还没标准化**
- 平台会把长命令转后台；这属于任务执行环境，不是 CAD 插件 Bug。
- 当前做法是 `run_in_background` + `TaskOutput` 轮询；但没有统一的持久化检查点/幂等恢复，进程退出后只能靠 journal 人工判断。
- 需要专家回答：在不要求突破平台限制的前提下，客户端应把哪些状态写入持久化检查点，才能安全恢复“已提交未保存”的作业而不重放？

**OPEN-10｜V1.1 负向回归仍有未跑项**
- D-02 的“finalize 查询失败但文件已存在”极端场景未构造；`persisted` 只在服务器确认后置真，代码结构上正确，但没有实测。
- P1-A 的“attach 入口不可用 / attach 返回非 0”失败分支未构造。
- P1-B 的“写侧缺路径、文件被移走/覆盖、同库多图纸”负向用例未跑完。
- P2-C（run_regression 断言加强、独立关闭重开、区分预提交拒绝与失败作业）、P2-D（`NetFrameworkAssemblyResolver` 影响面）仍是 pending。
- 这些不阻塞当前图，但会让“生产默认”评审被质疑。

**OPEN-11｜生产实例的字体路径尚未验证**
- `F:\fonts` 已追加到 AutoCAD 2022 四套配置的 `ACAD` 支持路径末尾，原值备份在 `archive/2026-11-16-acad2022-v1.1-callout-v3/cad-font-path-before-20261116.json`。
- 生产 PID 12948 **没有重启**，它的旧会话是否已读取 `F:\fonts` 未验证；测试实例能读到，不等于生产实例已生效。
- 需要决定：是否允许重启生产实例做一次字体路径验证；如果不允许，后续所有依赖 `F:\fonts` 的图都不能宣称“生产已验证”。

### P2｜专业/度量口径待确认

**OPEN-12｜1/3 字高按“名义字高”还是“实际字高”？**
- 当前规则用 `height/3`，本图名义字高 0.06 → gap 0.02。
- `co-wall` 读回 `actual_height=0.0685`；若严格按实际字高 1/3，应约 0.0228。
- 其余五组实际高度 0.06，gap 0.02 正好 1/3。
- 需要确认：规则按计划字高还是 CAD 实际格式化高度？若按实际高度，需在落图后二次修正 gap，不能只按计划值验收。

**OPEN-13｜370 砖砌大放脚完全在地面以上，是否符合专业意图？**
- 当前几何：C15 基础 0.30–0.60；370 放脚 0.60–1.00；240 墙身 1.00–3.00；压顶 3.00–3.06；室外地面 y=0.60。
- 即：**370 放脚整段 400 mm 都在地面以上**，C15 基础顶面与地面同高；240 墙身本体 2.0 m，地面至墙顶 2.4 m。
- 如果设计意图是“大放脚埋在地下”，当前几何就是专业错误，需要把放脚下移、地面线/土体/尺寸/锚点一起改。
- 如果设计意图是“露出 400 mm 砖砌勒脚/放脚”，当前图成立，但应在图签或说明里写清。
- 这是本轮最需要工程专业确认的一项，不能只按“图能出”放行。

---

## 4. 已关闭项（不再展开）

以下问题已修完并有证据，不再作为当前未关闭项；需要审计时回看 archive：

- 保存成功却报 `Unknown request record`：请求身份传递已修，正向 + 无 save/重复 ID/保存失败三项负向回归通过；仅“finalize 查询失败但文件已存在”未实测（列入 OPEN-10）。
- 砖墙几何矛盾：2400 地坪至墙顶、抹面各 20 mm、基础埋深 300 mm、尺寸毫米显示 `Dimlfac=1000` 已在当前计划中一致。
- PDF 可见且内容正确：当前 `plot5` 为 `committed_verified`；页面设置 finalize 另列 OPEN-06。
- hztxt_e 横线切字：已由 1/3 字高规则取代，不再保留 0.012 特例。
- 12 组重复叠加：改为从 0 组旧 callout 的源图起，apply 前断言 0/0。
- 底部 C15/地面引线交叉：按锚点顺序重排文字行、统一 B.x，六组 45° 平行。
- 第三批归因：用户主动终止，不是 CAD 崩溃/Agent 自主失败；已在新文档中更正。
- 测试 AutoCAD 进程堆积：每轮结束清理，仅保留生产 PID 12948。

---

## 5. 给专家的最短问题清单

1. **SaveAs 连续会话**：自动重启+rebind 与新增 `rebind --drawing`，哪个不破坏 fence/ownership/ledger？最小可行改法是什么？
2. **page_setup finalize**：保存成功、PDF 打印 `committed_verified`，但 page_setup 自身 `verification=failed`；应修哪一层？能否用打印结果替代页面设置验收？
3. **旧标注物理删除**：是否需要在 archive 之后提供 `erase`？如果需要，如何保证引线/文字/圆点/填充整组原子删除且可恢复？
4. **旧圆点**：archive 目标是否必须包含圆点？如何按锚点/关联可靠匹配，避免漏 2 个圆点这类残留？
5. **16 步/ASCII ID**：在契约不变的前提下，自动分批应放在哪一层？错误码应如何在提交前暴露？
6. **15 秒转后台**：持久化检查点最小字段是什么？如何做到“已提交未保存”的安全恢复而不重放？
7. **1/3 字高口径**：按名义字高还是 CAD 实际格式化高度？`co-wall` 实际 0.0685 是否要单独补 0.0228？
8. **370 放脚标高**：400 mm 放脚露出地面是设计意图还是错误？若是错误，正确埋深/地面线应怎么定？
9. **生产字体路径**：是否允许重启 PID 12948 验证 `F:\fonts`？不允许重启时，如何给出可验证的替代证据？
10. **ED04 两条候选**：接受人工判读放行，还是必须调到零候选？若必须清零，允许的“引线-字框”最小间距是多少？

---

## 6. 证据入口

- 最新 1/3 字高证据：`diagnostics/CURRENT/evidence-gap-third/`（DWG、PDF、预览、plan、脚本、日志、SKILL、两个 validator）
- 旧标注清理证据（归档）：`diagnostics/archive/2026-11-16-acad2022-v1.1-wall/evidence/v11-data/scope_35.json`、`verify-new-35.json`、`plan-new-35.json`
- V1.1 源码快照（归档）：`diagnostics/archive/2026-11-16-acad2022-v1.1-wall/source-snapshot/`
- 旧版 callout 规则（已过时，仅审计）：`diagnostics/archive/2026-11-16-acad2022-v1.1-callout-final/`、`-v2/`、`-v3/`
- 字体路径原值备份：`diagnostics/archive/2026-11-16-acad2022-v1.1-callout-v3/cad-font-path-before-20261116.json`

**边界声明**：本文件只汇总“当前仍存在/待确认”的问题；不把归档成功写成物理删除，不把 PDF 存在写成页面设置 passed，不把“文件已生成”写成链路 finalize 通过，不把用户主动终止写成 CAD 崩溃。
