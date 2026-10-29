# 底座能力整体推进

用户2026-10-29要求：整体推进未完善能力，不在单一点过度验证。按批交付基础操作，离线关键保护检查加代表性原生操作通过后转入下一批。已有正确成果与已发布版本保留。域内判断、取景、美观由Agent负责，不开发自动工程判断或排版引擎。

|批次|交付范围|状态|
|---|---|---|
|1 能力真实性和常用原语|同步过时能力描述；圆弧/完整椭圆明确参数修改；原坐标PDF/DXF单位一致性；可续查的客户端逐幅PDF批处理|实现及离线检查完成，原生验收进行中|
|2 查询、布局与外参|从已实现纸空间清点接入明确布局查询；补具体布局、视口与外参生命周期缺口|指定布局分页查询已实现，原生服务级检查通过；完整协议验收及其他缺口继续推进|
|3 常规编辑与图层|按明确目标补独立对象删除/曲线编辑、线型/图层缺口，复用现有端点和顶点修改|待推进|
|4 文件与复杂对象|另存及跨图操作身份；表格及专业对象相关能力按实际依赖分块|待推进|
|5 兼容与交付|声明实际已验收宿主、单位和客户端；未验收不冒称；整理发布文档及签名包|随各批推进|

范围不是一次性实现所有AutoCAD命令。每批记录已实现、已原生验收、仍受限、未开发；真实执行故障优先修复后继续。物理打印机监控依赖实际设备，跨CAD重启接管涉及任务身份，不能为了“完成清单”取消保护。管立得放在后面：先由管立得运行后提供它自己的专业对象编辑能力，再评估底座协作挪移文字，不直接修改代理。

## 第一批接口

- `set_arc`：单个独立零厚度WCS圆弧，目标摘要、center/radius/start_angle/sweep_angle。角度为度，逆时针弧长0～360之间，不含端点。
- `set_ellipse`：单个独立完整WCS椭圆，目标摘要、center/major_axis/radius_ratio；不将椭圆弧隐式改为完整椭圆。
- PDF/DXF：与其他原坐标计划共用`UnitMetadataMatches`；raw模式不转换坐标，`expected_insunits`可选；旧metres声明仍限定0/6。页面纸张毫米与模型单位分开。
- `foundation.cmd plot-batch --workspace ... --arguments batch.json`：客户端顺序协调明确图幅，每幅仍为独立受保护`plot_pdf`，非原生原子批量事务，不提交实体打印任务。1～128幅，不覆盖PDF；原计划和摘要保留；同批次同参数续查原请求，遇未知/失败停止后续幅。

```json
{
  "schema":"foundation-pdf-batch-v1",
  "batch_id":"sample-batch",
  "units":"raw_drawing_units",
  "expected_insunits":4,
  "sheets":[{
    "sheet_id":"01","layout":"Model","file_name":"sheet-01.pdf",
    "window_settings":{
      "device":"AutoCAD PDF (General Documentation).pc3",
      "media":"ISO_full_bleed_A3_(420.00_x_297.00_MM)",
      "plot_style":"monochrome.ctb","rotation":0,
      "min":[0,0],"max":[420,297],
      "scale_numerator":1,"scale_denominator":1,"center":true
    }
  }]
}
```

调用前从真实目录选择设备、介质和样式；示例不是对每台机器存在的保证。批处理日志在`plot-batches/<batch_id>`；各幅请求仍在`requests`下。整体不是原子提交，前幅已成功不会因后幅失败而回滚。

## 当前验收与待处理记录

- 已发布及项目使用版本仍为 `1.2.0-beta.3.18`；本轮源码属于开发候选，未替换公共入口、未发布。
- 第一批圆弧和完整椭圆明确参数修改，在 AutoCAD 2026 独立毫米测试图中已通过创建、编辑及保护正常释放。
- 毫米 DXF 原请求 `capbatch1-dxf-mm` 返回 unknown，context `e2fc00b24222451d9b235d89a1da3217`；保护仍在 `artifacts/batch1-native/work-r3`，没有重放、强制释放、换工作区绕过，也没有继续 PDF 或保存操作。
- 独立只读重新读入其 pending DXF：圆弧、圆和完整椭圆几何存在，完整椭圆终点由 `6.283185307179586` 变为 `6.283185307179585`。源码已将 DXF 的完整椭圆端点比较限定放宽至 `1e-12` 弧度；源图前后比较保持原规则，真实半椭圆、比例及起角变化仍拒绝。这不等于原 unknown 已解决或整项原生验收通过。后续需针对原请求正常独立核验，保留全部历史结果。
- 原生未知结果的错误消息增加具体失败原因及 pending 路径，后续诊断不再只得到泛化提示；原历史回执不修改。
- `query_entities` 新增可选 `layout`，只查询指定布局顶层对象，不切换当前布局；不传仍为模型空间。游标绑定布局、会话、图纸、修订及过滤条件。AutoCAD 原生服务级临时测试图确认：模型文字0条，纸空间第一页2条、第二页1条；未读写受保护工作图。尚未称为完整网关协议验收。
- 当前关键检查：161 项契约检查、17 项 Python 测试通过；服务端与 AutoCAD 2026 插件编译通过。新版包尚未验收，不能覆盖正式版本。

## 第二批读取接口

`dwg_query_entities` / 原生 `query_entities` 支持 `layout: "布局1"`，可以单用明确布局名限定查询范围，结合 `entity_type`、`layer`、`color_index` 和矩形范围进一步过滤。使用返回的 `next_cursor` 继续；布局变化不得沿用前页游标。读取的是布局顶层实体，块内部仍按现有块读取接口处理。

## 阶段完成后的Notion同步
用户已授权持续维护：每个阶段完成后更新Notion当前正文、标题及数据库摘要/状态、发布入口、已验收范围、已知问题和下一步；避免只追加日志仍让页首显示旧现状。2026-10-29已整理三个核心页，历史保留为折叠档案：
- 项目总览：https://www.notion.so/3edaeac2c209815c86e9d27d86c1ac76
- 能力与待办：https://www.notion.so/3eeaeac2c20981f49a90f6ab546ba69a
- 简介与发布入口：https://www.notion.so/3efaeac2c209810bbd7fd63d32c96835
本次区分已发布beta3.18与开发候选，保留原DXF未知请求未结案状态。以后按实际阶段证据更新，不将离线通过写成整体实机通过。

## 第二批读取一致性检查点
新增 `count_entities`、`select_by_layer`、`select_by_type` 的可选 `layout`，与分页 `query_entities` 一致；MCP参数及原生schema已同步。返回scope/layout，不改变当前布局或pickfirst。未指定layout仍读取模型空间；明确布局不存在时报错，不回退模型空间。查询的分页游标继续绑定布局；计数无分页，句柄筛选仍受既有limit限制。
AutoCAD2026原生服务级临时Database检查：纸空间3个DBText，第一页2、第二页1、计数3、limit2的句柄2；模型空间对应计数及句柄均0；不存在布局明确拒绝。证据 `artifacts/batch1-native/query-layout-batch2-native.json`。服务端/插件编译通过；不称完整网关协议验收，不动正式beta3.18入口。
原DXF请求已再次通过原context的dwg_job_get查证：unknown、effects unknown、verification notchecked、write_fence_released false。证据 `work-r3/capability-batch1/unknown-recheck-20261029.json`。实时核实CAD进程与session匹配；当前已加载journal的正常API不允许对terminal unknown重新登记提交或核验，不调用这些拒绝路径，不修改SQLite、不强行解锁或切换工作区绕过。继续独立能力开发，正常恢复能力与完整原生验收仍待处理。

## 未知诊断及纸面视口位置增量
- 后续 unknown 写入可在原账本 ResultJson 保留合法JSON诊断回执，包含pending文件、原始前后事实及错误；非法JSON不保存。效果仍unknown、verification未核验、围栏保留且队列停止，不能将诊断回执当成已提交或通过证据。68项PluginJobContract通过，包含新增诊断保留及非法输入仍有围栏检查。此变更不修改历史DXF请求、不提供强制释放或重放。
- `set_paper_viewport` 新增可选2D `center_paper`，原始纸面Z值保留；与可选纸面尺寸同时使用，模型view_center/view_height仍由原有明确参数指定，未提供的字段受原有事实核验保护。仍只支持既有正交无扭转浮动视口，排除布局总体视口。
- 167项FoundationExtensionContract通过，新增纸面中心及尺寸组合、错误中心和模型视域/锁定变化拒绝检查。AutoCAD2026原生临时Database中已实际移动视口中心(100,80)至(200,180)、宽120至140，提交后读回模型视域及比例未漂移，专用回执比较通过。证据 `artifacts/batch1-native/viewport-placement-native.json`。这属于原生基础操作检查，不冒充完整网关及保存交付验收，未操作原protected工作区或工程图。
