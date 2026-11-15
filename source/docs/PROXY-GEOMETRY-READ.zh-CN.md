# 代理及复合图形的只读几何读取（1.2.0-beta.1）

AutoCAD 2026/.NET 8。复用 `dwg_get_entity_properties`，`include_geometry=true` 时，每个成功对象额外返回 `geometry_read`。普通查询只找候选，不自动分解；按句柄读取才提取。既有 `entity` 与 `entity_digest` 保持原定义，提取结果不加入写入摘要。

```json
{"context_id":"绑定返回值","request_id":"本次读取唯一编号","handles":"[\"165D\"]","include_geometry":true,"geometry_offset":0}
```

通过 `dwg_job_get` 取得同一请求结果；CLI 的 `read` 返回第一页，继续页通过同包 MCP 接口读取。`geometry_offset` 可省略，填写时仅允许一个句柄、`include_geometry=true` 和整数 0..4096。按 `next_offset` 继续直到 `has_more=false`。每页比较 `entity_digest`、`document_session_id`、`drawing_revision`；任何变化都重新读取，不将不同快照拼接。没有服务端长期几何缓存。

## 返回含义

- `primitives`：WCS XYZ 原始坐标，角度为弧度。二维和三维读取均不换算单位。`path` 表示各层原生分解集合索引；临时子对象没有可写句柄。
- 直线：端点、长度。圆弧：端点、圆心、半径、法向、起止角及总转角。圆：圆心、半径、法向。椭圆：圆心、长短轴向量、法向及参数区间。
- 轻量多段线：有序 WCS 顶点、逐段 bulge、起止宽度、法向、闭合状态。bulge 定义在该多段线 OCS 平面，应结合 normal 解释，不能把所有曲线假定在 XY 平面。
- 三维多段线：顶点和 `polyline_type`；只有 `SimplePoly` 的顶点可直接视为直线连接，拟合类型不作这种保证。二维旧式多段线等复合类型尝试原生分解。
- 样条：NURBS 次数、控制点、节点、权重、周期、闭合状态及参数区间；未还原专业设计参数。
- `status=complete` 仅表示本次原生分解中可见几何已按支持类型读取，不证明原始专业对象完整、道路连续或图框成立。仍须取完所有页。`partial` 和 `unavailable` 的具体原因在 `issues`；正文不展开，作为跳过项记录。
- `extracted_primitive_count` 是有界提取数量；有预算问题时不代表整个对象总量。每批最多访问 4096 个节点、60000 个标量，递归深度 12；每页最多 64 个图元且约 48000 字节图元正文。问题最多显示128条，另有总数及截断标志。单个超大图元不能进一步分页时明确记录省略；多对象响应仍可能触发既有总返回上限，应改成单句柄读取。节点/标量/深度超限只返回部分结果，本阶段不继续扩展。

## 只读方式与边界

源实体只按 `ForRead` 打开。`Entity.Explode(DBObjectCollection)` 在内存中产生临时对象，遍历后逐项释放；不向图纸追加、不擦除、不变换源对象、不保存。代理调用原生分解，得到的是保存的代理显示几何，可能是近似圆弧/折线，不能据此宣称设计线形、高程或专有数据已恢复。块的插入变换由原生分解应用，返回放置后的 WCS 坐标。

连接、行进方向、图框用途及是否满足工程精度由 Agent 判断。保留原生集合顺序不表示保证首尾相接；例如实图有一段圆弧需反向使用。没有加入道路识别、自动拼接、曲线拟合、工程容差或排版算法。

无代理显示数据、原生分解返回空集合、拒绝分解、缺少外参、无法处理的子对象及预算超限均可能无法完整读取。不尝试绕开权限或解析私有二进制。带类名不等于验证过该软件的全部版本。

## 本轮验证范围

第一阶段使用私有道路样图A。实图通过打包 MCP 在自有只读副本上测试：39 个代理，其中10个有几何，29个横断面代理返回空集合。三条 `CRDAxisEntity` 中线共464段（366、5、93）；一段需反向后可连成三条连续路径。原图、工作副本磁盘哈希与 DBMOD、栅栏状态另存私有验收记录。

真实代理中实测直线、圆弧、多段线；样条、圆、椭圆、三维多段线、倾斜平面及旋转缩放图框另用已知坐标的合成样本通过 MCP 验证。合成样本由原生控制台测试命令准备，这一步不是 MCP 验收。道路样图读取到闭合参照矩形 `4EC4`。

第二阶段已使用不同类型的私有给水样图B，完成基础几何读取、CAD2026界面抽样叠合、实际布局内框读取，以及道路普通实体重建/偏移/定位。正式外框依赖缺失的外参；该旧图保存后文字样式字体文件归一化，严格磁盘核验失败，原请求保持保护。详见[实图收尾、抽样范围及发布限制](PROXY-GEOMETRY-FIELD-CLOSEOUT.zh-CN.md)。不因几何抽样通过而宣称外框或整图保存验收通过。

开发依据：[Autodesk ProxyEntity](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_ProxyEntity.html)、[NURBS 数据](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_NurbsData.html)。最终交接列出实测版本身份与本地证据；构建成功不等于 CAD 实测通过。
