# 第二阶段开发与验收记录

2026-10-15，用户批准A～P作为后续开发范围，本轮自主处理上限一小时。顺序为底座可靠性→常规CAD命令→工程图纸操作→检查与兼容。M1～M5保留；M6批量打印、M7实体打印机监控若独立且需要实际设备反馈则后置。本轮确认两者可分离，未开放。

## 当前交付性质

本轮版本为 **1.2.0-local.1** 本地开发预览，接续公开1.1.0-rc.1。不是A～P全部完成，也不是新的公开Release。既有1.1安装和私有cad-foundation-2026保留；新插件使用独立版本/哈希目录安装，未修改全局MCP配置或两个专业Skill。

25项既有计划操作扩为47项。CLI/MCP仍沿用既有账本、单次提交、持久写栅栏、原生事务、原生独立读回。MCP每模式仍8项工具，新命令经dwg_apply_plan提供，不直接放开上游默认关闭工具。

## 本轮实际实现

| A～P | 已接入内容 | 尚未完成的范围 |
|---|---|---|
| A 环境/发现 | 实际原生运行时、进程架构、CAD API版本；操作目录、计划限额、图层/布局/简单块摘要；打印和资源目录 | 全客户端一键安装、全面字体诊断和每个宿主认证 |
| B 执行/回执 | 失败步骤/操作定位、已执行步骤不等于提交的标记；CLI原请求有界续查，失败期限届满保持unknown；保存核对页面设置及块定义 | 回执竞态原因尚未完整定位；跨CAD重启接管和未知写入自动恢复未开放 |
| C 查询 | 新增点位置、对象线型/线宽、表格单元格与格式、块属性及覆盖说明；绑定时命名块/线型/外参元数据 | 纸空间通用查询、精确几何筛选、嵌套/代理完整读取、通用交点/测量接口 |
| D 绘图 | point、arc、ellipse、rectangle | 样条、云线、遮罩及更广对象范围 |
| E 编辑 | set_properties、set_line、set_circle、set_open_polyline、offset、mirror_copy | 通用曲线偏移/镜像、修剪/延伸/打断/连接、倒角/圆角/阵列、通用删除 |
| F 填充 | set_hatch_pattern，要求完整的单填充/边界组，实际路径独立核验 | 旧填充通用修复、多孔洞、多边界和布尔 |
| G 文字/尺寸 | set_text_format、set_dimension_text、angular_dimension | MLeader、通用查找替换/背景、尺寸变换与完整实时关联 |
| H 图层/样式 | 对象使用已存在线型和原生线宽；资源读取；保留既有图层/样式操作 | 线型加载、图层改名/状态/隔离、当前样式和系统变量扩展 |
| I 图块 | block_define、block_insert、set_block_attributes，复用既有属性服务 | 动态/嵌套/代理块、分解和外部DWG导入 |
| J 表格 | 原生table、set_table_cell；读取文字/行高/列宽/字高/文字样式 | 合并/拆分、增删行列、数据提取和复杂表格 |
| K 视图 | 保留既有边界；PDF结果可在图外渲染复核 | 图内定位/截图/恢复、命名视图和UCS命令 |
| L 工程图纸 | 布局页面设置读取/修改；外参只读元数据 | 布局增删复制、视口和外参编辑 |
| M 打印 | 设备/打印样式/PC3纸张目录；页面设置；当前布局设备/纸张预检查（不含内容适配及字体审计）；当前布局原生单张PDF | 窗口范围、FIT、自定义偏移、独立原生预览、图像导出；M6/M7后置 |
| N 文件 | export_dxf，独立DXF读取指定对象；输出不覆盖 | 另存DWG、模板、跨图复制及身份映射 |
| O 检查 | 本轮变更的拒绝/回滚/保护/保存重开验证 | 通用审计、图纸差异、重复/自交检查、清理和性能基准 |
| P 兼容 | 明示实际已加载运行时，不用SDK版本冒称CAD宿主兼容 | 毫米、2022、更新.NET10宿主及其他客户端实机认证 |

上表未完成项继续保留原开发范围，未擅自取消。下一轮先完成当前受限命令的实际工程反馈和C/E/H缺口，再推进K/L及O/P；没有把专业工程判断或排布算法移入底座。

## 22项新增计划操作及参数

通用外层不变：`schema: foundation-drawing-plan-v1`、`source_role`、`units: metres`、`steps`。旧对象`targets`成员为`{handle,expected_sha256}`，摘要由实际读取提供。下列每项都必须有唯一`id`及`op`。

| op | 其他参数 | 支持范围 |
|---|---|---|
| point | position,layer,color | 原生DBPoint，二维WCS |
| arc | center,radius,start_angle,sweep_angle,layer,color | 角度单位度；正向扫角大于0且小于360 |
| ellipse | center,major_axis,radius_ratio,layer,color | 完整椭圆，major_axis为向量；比例(0,1] |
| rectangle | min,max,layer,color | 原生闭合Polyline，不是独立RECTANGLE实体类型 |
| set_properties | targets,layer,color；可选linetype,lineweight | 支持对象及完整关联组；目标层必须已存在、可见、未锁且非外参层；线型须已存在；线宽为原生百分之一毫米枚举 |
| set_line | targets,start,end | 一条无持久反应器的零厚二维直线，保持身份与样式 |
| set_circle | targets,center,radius | 一个无持久反应器的零厚WCS圆；不改关联填充圆 |
| set_open_polyline | targets,vertices | 一条开放零宽二维多段线，2～128点，可带受限bulge；不改闭合属性 |
| offset | targets,distance | 一条独立Line/Circle，生成新对象、保留原对象；直线正值向左，圆正值向外 |
| mirror_copy | targets,axis_start,axis_end | 一条独立Line/Circle，按给定轴生成镜像副本；不删除源对象 |
| set_hatch_pattern | targets,hatch_handle,pattern,scale,angle | 单个关联HATCH及其圆/闭合多段线边界；angle为弧度；保持边界几何 |
| set_text_format | targets,position,height,rotation,text_style；MText另须width | 单个独立DBText/MText；DBText限基线左对齐，旋转单位度；关联引线文字继续走set_callout_format |
| set_dimension_text | targets,text,position,height | 既有四种受支持尺寸；text空串重置覆盖；固定文字移动方式2，保存实际测量值及尺寸线几何 |
| angular_dimension | center,ray1,ray2,position,dimension_style,layer,color | 原生LineAngularDimension2；共同顶点、逆时针内角小于180度、弧位置在该角内；几何给定而非源线实时关联 |
| table | position,cells,row_height,column_width,text_height,text_style,layer,color | 原生未合并TABLE；最多16行/列、128格；行高不能小于原生文字及边距最小值，不能满足时拒绝/回滚 |
| set_table_cell | targets,row,column,text | 一个受限表格内已存在的格；行列从0开始，保留其他内容和格式 |
| block_define | name,members | 新命名块，不覆盖；1～32个line/circle/attribute成员，最多16个唯一属性；成员用0层/ByBlock色；不是从既有对象任意建块 |
| block_insert | name,definition_sha256,position,scale,rotation,attributes,layer,color | 已读取并有摘要的简单平面命名块；正统一缩放、度数旋转；不导入外部文件/动态块 |
| set_block_attributes | targets,attributes | 一个本地非动态命名块的简单属性；未知tag拒绝，不自动新建属性 |
| page_setup | layout,expected_sha256,device,media,plot_style,rotation,plot_area,scale_numerator,scale_denominator,center | 独立单步；layout/extents范围、固定比；纸张单位明确毫米；旋转0/90/180/270；layout范围不居中 |
| plot_pdf | layout,expected_sha256,file_name | 独立单步；仅显式当前布局及PDF PC3；简单ASCII.pdf文件名；输出到工作图目录outputs，已有文件拒绝覆盖；不是实体打印 |
| export_dxf | file_name,expected | 独立单步；expected为1～64个句柄/摘要；outputs内ASCII.dxf文件名；独立DxfIn核对指定对象后才发布最终文件 |

新增三种block成员：`{kind:line,start,end}`、`{kind:circle,center,radius}`、`{kind:attribute,tag,text,position,height}`。属性tag限大写字母起始及大写字母/数字/下划线。

页面比例参数是原生“纸张毫米/模型绘图单位”的数值比，不是自动转换的工程比例。当前模型以米为绘图单位，例如工程1:200应写5/1（1米画成5毫米），不能直接填1/200。专业Skill负责选择工程比例，底座按显式值执行并读回。

save兼容旧参数，并新增可选`expected_layouts`字典。新版CLI保存自动带绑定时布局摘要；保存前校验、磁盘DWG独立读取后核对，避免只验模型对象。

块目录最多256个命名定义，每个最多读取32成员，目录共享128个成员详细读取预算；超预算者明确不完整；外参/动态块内容和不支持成员明确未覆盖，不假装可插入。表格、属性和打印目录同样有独立上限和完整性字段。目录的`complete`不等于每个打印设备可用或工程语义完整。

## 执行证据与限制

每个成功结论以最终交付记录指定的包、插件MVID和原生流程为准。开发中间包也保留，但不能拿前一个二进制的证据替代最终包。测试通过打包Foundation的MCP stdio入口执行，CLI仅负责既有安装/自有副本启动/安全停止；没有直接COM脚本、任意Lisp或上游裸命令绕过底座。

实测样例含中文、弧跨零角、椭圆、开放bulge、四种新建/既有尺寸路径中的本次分支、简单属性块、TABLE、完整填充组、线/圆编辑及偏移/镜像、页面设置、PDF、DXF、保存/重开。DXF独立读回本轮只指定Line/Circle，不宣称已逐类验证整个DXF。

拒绝样例：旧摘要、未知块属性tag、已有PDF不覆盖；回滚样例：同一批先建临时线、后因目标层不存在失败，模型对象集合及既有对象保持。没有开放通用删除或取消已执行操作。

一次中间原生流程出现回执身份核对错误。原请求仅提交一次，两端栅栏保留；随后仍经原请求查询，确认已提交且验证通过，并由正常机制释放栅栏、保存和停止。未改库/删除账本/强制解锁。现有现场只能证明原请求可续查，不能断言底层竞态原因已修复；新版CLI增加有界查询重试，离线测试证明重试不重复提交。原始MCP客户端仍须按契约查询原请求。

原生PDF使用Autodesk绘图驱动。独立检查纸张、页数及渲染，另记录驱动生成文件的元数据/解析警告；不自动重写PDF掩盖警告。运行命令自身核验文件头、长度和SHA256，不冒称内置了完整PDF解析器或视觉验收。

## 后续顺序

1. 继续补常规CAD命令的缺口及实际工程反馈：查询/选择、更多曲线编辑、图层状态/线型加载。
2. 扩工程图纸操作：旧填充修复/复杂边界、更多文字/尺寸/图块/表格、视图、布局/视口/外参、打印窗口/FIT及独立预览、另存/跨图。
3. 通用检查、清理、性能和兼容认证。
4. M6/M7维持后置；本轮不消耗实体打印纸张、不连接打印队列验证。

最终包和证据路径、验证结果、安装同步情况另见同日交付摘要。历史公开清单和验证记录按版本保留，不回写成新版已全量通过。
