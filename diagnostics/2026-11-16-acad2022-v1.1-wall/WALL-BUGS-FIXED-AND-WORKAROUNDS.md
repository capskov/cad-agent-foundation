# 砖墙截面任务：实际做了什么、修了什么、还剩什么

生成时间：2026-11-16（第二轮收敛后）
适用范围：本次“空白 DWG → 砖砌围墙截面 → A3 图框 → PDF”实战，以及其中调用的 CAD 智能底座 2022 写入/保存/出图链路。
说明：本文取代第一版中“第三批随 CAD 退出而丢失”“PDF 未产出”“未改插件代码”的旧表述。旧表述与后续确认的事实不符，已在此更正并保留更正记录。

## 0. 归因更正（必须最先看）

- **第三批不是 CAD 崩溃，也不是 Agent 自主执行失败。** 第三批 15 步提交后 `verification=passed`，但尚未保存；随后是**用户主动终止了本轮任务**，保存未执行，所以 `s3.dwg` 不是“崩溃丢失”，而是“未执行保存的已验证内存成果”。后续在同一台机器上用 `s2fix.dwg` 作为输入重新执行第三批，已正常保存为 `s3.dwg`（36,337 B），并有第四批 `s4.dwg`（37,620 B）。旧诊断文中的“CAD 退出/丢失”归因作废。
- 用户主动终止属于任务调度事件，不计入底座 Bug 统计；底座的 fence（首次未保存则持有）是设计行为，不是这次丢失的原因。
- 第一版“本轮没有改插件 C# 代码”只适用于最初那一次画图尝试；第二轮收敛已经实际修改并重编了 net8 服务端与 acad22 插件，见第 2 节。

## 1. 已解决并能拿出证据的

### 1.1 步骤 ID 不能含中文 → ASCII 计划 ID
- 现象：`layer-图框` 等 ID 被 `step IDs must be unique identifiers` 整批拒绝。
- 处理：图层显示名保持中文（图框/墙体/填充/标注-引线/标注-文字/尺寸/地面），计划 ID 改为 `layer-01..07`。
- 证据：`plan-01-frame.json`；首批 15 步提交 `verification=passed`。

### 1.2 单计划 16 步上限 → 四批拆分，填充边界不跨批
- 现象：39 步计划被 `1..16 steps required` 拒绝。
- 处理：拆为 15 / 9 / 15 / 6 步；`hatch.boundary` 必须引用同一计划内更早的闭合 polyline，所以墙体/基础/压顶与各自 hatch 同批；土体 hatch 与地面线同批。
- 证据：四份计划与四批回执；第三批 15 步、17 outcomes；第四批 6 步。
- 契约结论：16 步上限与 ASCII ID 按接口契约处理，不是 Bug；后续应在客户端补“明确错误提示 + 自动分批”，而不是放宽契约。

### 1.3 空白图单位与出图比例
- 空白模板 `acad.dwt` 实测 `INSUNITS=1`（英寸），不是 unitless。`page_setup` 将其改为 4（毫米）后，`plot_pdf` 最初只接受 0/6，触发 `file_output_requires_metres_or_explicit_unitless_metres`。
- 修复路径：`page_setup` 计划声明 `units="metres"`；服务端 `UnitMetadataMatches` 对带 page_setup 的米制计划接受 observed=1；提交后按 `units=="metres"` 设 `INSUNITS=6`。该路径在 fix9 生效。
- 比例坑：米制模型如果写 `1:20`，AutoCAD 把 8.4 m 当成 8.4 个图纸单位，PDF 只剩中心一点。必须写 `1:0.02`（1 图纸单位 = 0.02 m，即常规 1:20 mm 表达）；`s11.dwg` 已按 `scale_numerator=1 / scale_denominator=0.02` 保存。
- 证据：`s10.dwg` / `s11.dwg`；A3 媒体 `ISO_full_bleed_A3_(420.00_x_297.00_MM)`；`DWG To PDF.pc3`；`monochrome.ctb`；PDF 1191×842 pt。

### 1.4 砖墙几何计划修正（先修计划再落图）
- 墙高：`dim-wall-height` 0.60→3.00（地坪以上 2400 mm）。
- 抹面：`plaster-left` 3.48–3.50、`plaster-right` 3.74–3.76（各 20 mm），不再出现“计划 40 mm、文字 20 mm”的矛盾。
- 基础埋深：新增 `dim-found-depth` 0.30→0.60（300 mm）。
- 放脚宽标注 `dim-footing-width` 放在 y=0.80。
- callout 插入点：BottomRight 的插入点改为文字框右边界，C=(right+0.02, y_text-0.02)，并断言 `annotation_offset` 严格为 `(0.02,-0.02)`。
- 证据：`v11_build_wall_plan.py` 重跑；01=15、02=9、03=15、04=6 步。

### 1.5 砖墙四批已全部落图并保存
- 第 1 批：`wall-section.dwg`（31,246 B）。
- 第 2 批修正版：`v11-wallA/s2fix.dwg`（32,021 B），SHA256 `7E71198CFB3BB667077FAE10498D219671475F07EB550108C31E914E05D43B44`。
- 第 3 批：从 `s2fix.dwg` 起，15 步、17 outcomes、`verification=passed`，保存为 `v11-wallB/s3.dwg`（36,337 B），SHA256 `9ECA9864154022EC5856E3999E521E21F3178992CC3E6FDAFA6F6D45715DE080`。
- 第 4 批：因 SaveAs 后同会话不可连续，换新工作区从 `s3.dwg` 起，6 组 callout 保存为 `v11-wallC/s4.dwg`（37,620 B），SHA256 `151FB50B672D30FC6558A739EEEE80403D0FBC351308D5C90469956AAF12BE7F`。
- 结论：`s4.dwg` 是四批齐全的几何成果；旧 `v11-wall3/s2.dwg`（32,021 B，抹面 40 mm）仅作历史证据，不再使用。

### 1.6 PDF 链路已打通
- `page_setup` + 重新 bind 取 `layout_digests.Model` + `plot_pdf` 的路径已实测；`v11-wallU` 首次产出可见 A3 PDF（20,193 B），但尺寸文字为 0/2/1。
- 第二轮在 fix13 下把 `WALL_DIM` 的 `Dimlfac` 改为 1000，重开 `s12.dwg` 出图，PDF 20,647 B，A3 1191×842 pt；实测尺寸文字为 2400 / 240 / 60 / 400 / 300 / 800 / 370（毫米），矢量内容完整。
- 证据：`v11-wallY/drawings/outputs/wall-section.pdf`，SHA256 `EB6E5432EF14981AA0B4BCA551B93A45709798EFB2A64DAC3B208FEE68F5088E`；渲染图 `v11_wall_pdf_check6.png`。

### 1.7 `Unknown request record` 保存身份问题已修（正向回归）
- 根因：`apply_with_optional_save()` 在未显式传 `request_id` 时，`apply()` 内部生成 ID，后续 finalize 仍用原始 `None`；保存作业与 finalize 的请求身份不一致。
- 修复：`write_id = write_reply.get('request_id') or request_id`，断言非空；finalize 查询与 context 都使用 `write_id`。
- 正向回归：`v11-wallA` 以第 1 批 `wall-section.dwg` 为源，安装 fix1、启动隔离 CAD，提交修正后的第 2 批 9 步；`verification=passed`、`persisted=true`、fence 释放、`save_reply.saved_path` 指向 `s2fix.dwg`，`Unknown request record` 不再出现。
- 反向回归仍未执行：无 `save_to` 不得宣称成功、finalize 失败不得用“文件存在”冒充成功、重复 request id 不得重放。这一项目前是阻塞项，不能宣称“保存链路已完整验收”。

## 2. 本轮实际修改的源码与构建对应关系

### 2.1 保存身份
- `src/cad-agent-foundation-main/client/foundation.py`：`apply_with_optional_save()` 的 `write_id` 传递。
- 运行时包：`artifacts/packages/20261116-fix1` 起；客户端 SHA256 `B99B5473E7A0E6B3F516F9AA1DFF65C3C59A6ECBCA1CCB1AE746C582084790DB`。

### 2.2 页面设置/单位
- `third_party/bimwright-dwg-mcp/fork/src/shared/Execution/FoundationDrawingPlan.cs`：`UnitMetadataMatches` 允许带 page_setup 的米制计划接受 observed=1。
- `.../plugin-acad26/FoundationPrinting.cs`：`plot_pdf` 的 INSUNITS 守卫放宽到 0/1/4/6。
- `.../plugin-acad26/FoundationPlanWriteHandler.cs`：page_setup 提交后按 `units=="metres"` 设 `INSUNITS=6`。

### 2.3 尺寸毫米显示（第二轮实际修复）
- `.../plugin-acad26/FoundationExtendedOperations.cs`：`dimension_style` 写入/更新 `Dimlfac`；样式事实新增 `linear_scale_factor`（否则写进去也校验不过）。
- `.../shared/Execution/FoundationPlanExtensions.cs`：`dimension_style` 可选字段加入 `linear_scale_factor`；`VerifySymbols` 增加 `VerifyDimensionStyleChange`，允许对已存在尺寸样式的受控修改（此前任何已存在 dim style 的改动都会被判失败）。
- `.../shared/Execution/FoundationPlanExtensions.cs` 仍是服务端与插件共用；**关键教训：校验器同时存在于 net8 服务端**，只重编 acad22 插件会继续被旧服务端拒绝（V1.1 BUG-03 的重演）。

### 2.4 运行时包与构建产物对应
- fix10：插件已含 `linear_scale_factor` 写入，但 net8 服务端仍是 01:24 的旧 DLL，带字段的计划仍在服务端被 `unexpected or missing plan fields` 拒绝。
- fix11：只替换 net8 服务端（`Bimwright.Dwg.Server.exe/.dll/.deps.json/.runtimeconfig.json`），字段校验通过，但插件事实缺 `linear_scale_factor`、且 `VerifySymbols` 不允许修改已存在 dim style，预提交回滚。
- fix12：补插件样式事实；仍被 `VerifySymbols` 挡住。
- fix13：补 `VerifyDimensionStyleChange`，插件与服务端同步重编；`dimension_style` 修改通过 `verification=passed`，`WALL_DIM.linear_scale_factor=1000.0` 读回成立，PDF 尺寸正确。
- fix13 包：`artifacts/packages/20261116-fix13`；插件 SHA256 `77FC7C3EFA6079C63D1F87FCF725A9D37A89AB2E8B7951FA725347CF5D46E346`；bundle `BA928511F56684C5F83223EE6202730DCBCAB68FE276488C035480C7A3F9C024`；MVID `784f7287-08c2-4a0e-aa9b-e4192db2f327`。

## 3. 仍未修 / 仍阻塞

### 3.1 SaveAs 之后不能同会话连续
- 单独 `save` 不会回头 finalize 原写入，会被 fence 正确挡住。
- 同一请求 + `--save-to` 可保存并 finalize，但保存到新路径后旧会话出现 `drawing_path_mismatch`。
- 同一工作区在 SaveAs 后重新启动还会 fail-closed（`D1 ownership ... no matching ledger`）。
- 原地覆盖当前 `work.dwg` 触发 `eFilerError`。
- 当前可行路径：**SaveAs 到新路径 → 换新工作区继续**；未绕过 fence，也未自动换工作区写同一图纸。尚未形成可复用脚本。

### 3.2 `plot_pdf` 服务端判定仍失败
- PDF 已生成、header 校验通过、目视内容正确，但客户端仍报 `foundation_plan_readback_mismatch,plugin_stage_not_passed`。
- 当前状态：`plugin_verification=passed`、`server_verification=failed`、`persisted=false`；PDF 产物在 `drawings/outputs/` 下。
- 这是“产物可用”与“判定链 passed”不一致，尚未收口；不能把 PDF 存在当成链路 passed。

### 3.3 callout 图面质量
- `C25混凝土压顶60厚` 仍被拆成两行（“60”后断行），引线偏长，墙体偏画面左侧，标题栏只是简单矩形加文字。
- 尺寸已正确，但图面美化未完成；这属于计划排版/字宽问题，不是底座写入 Bug。

### 3.4 反向回归缺失
- 无 save_to 不得宣称成功；finalize 失败不得用文件存在冒充；重复 request id 不得重放。三项尚未跑，保存链路的验收仍不完整。

### 3.5 接口契约文档化
- 16 步上限、ASCII ID、首次保存 fence、15 秒转后台仍是契约/平台限制；客户端尚未加入“明确错误提示 + 自动分批策略 / 可靠异步轮询与持久化检查点”。

## 4. 当前砖墙图的真实进度

| 批次 | 内容 | 状态 | 证据 |
| --- | --- | --- | --- |
| 1 | 7 图层、2 样式、A3 双矩形图框、3 个图签文字 | 已保存 | `wall-section.dwg` 31,246 B |
| 2 | 800 基础、370 放脚、240 砖墙、双面 20 抹面、60 压顶、3 个填充 | 已保存 | `s2fix.dwg` 32,021 B |
| 3 | 地面线、左右土体填充、7 道原生尺寸 | 已保存 | `s3.dwg` 36,337 B，15 步/17 outcomes |
| 4 | 6 组工艺 callout | 已保存 | `s4.dwg` 37,620 B |
| 页面设置 | 米制、A3、1:0.02 | 已保存 | `s11.dwg` 37,549 B |
| 尺寸修正 | `WALL_DIM.Dimlfac=1000` | 已保存 | `s12.dwg` 38,006 B |
| 出图 | `plot_pdf` | PDF 已生成，判定未收口 | 20,647 B，A3，尺寸文字正确 |

所以：**几何与尺寸已经画完并能出可见 A3 PDF；但保存/出图的最终判定链和 callout 图面质量仍有明确阻塞，不能宣称“全链路 passed”。**
