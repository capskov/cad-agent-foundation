# 实战基础操作修复 · v1.2.0-beta.2

范围来自2026-10-22/23实战反馈。卫星图识别、影像边缘判读、等宽道路描绘和美观取舍归Agent/Skill，本轮不加入底座算法。原有代理读取与local.5路面表达修改保留。

## 新受控入口：dwg_view

当前AutoCAD2026 Foundation写入配置的目录增加`dwg_view`，和已有请求一样传context_id、稳定request_id；业务参数`request_json`为JSON字符串。CLI增加`view --arguments <JSON文件>`。虽然不修改工程实体，它会改变显示视图或写PNG，因此不是无副作用只读接口。仍经精确文档绑定、队列和原请求结果查询，不依赖剪贴板、焦点或任意命令。

- `{"action":"read"}`：返回实际文档/会话/布局/空间、视口、相机；支持时返回WCS XY范围。
- `{"action":"window","min_x":100,"min_y":200,"max_x":140,"max_y":240}`：适配窗口宽高比、保留4%范围余量。
- `{"action":"center","x":120,"y":220,"height":60}`：按WCS中心与原始图面视高定位。
- 可加`file_name:"view-a.png"`及`pixel_size:1600`，定位后重绘、读回并截图；独立截图为`{"action":"capture","file_name":"view-a.png"}`。

PNG限定为工作DWG同目录outputs下的新文件，不覆盖旧图。返回实际PNG路径、尺寸、SHA256、捕获时间、文档/相机上下文及原生重绘完成反馈。`display_settle_verified:false`明确不保证外部影像瓦片或显卡已视觉稳定；Agent仍看实际截图，不能以API返回替代画面判断。

定位仅支持模型空间单视口、正交俯视且无扭转/裁剪；不自动切布局或修改视口方向。WCS、DCS与纸空间明确分开，不支持场景如实拒绝；错误返回view_may_have_changed及禁止自动重试。只读启动配置尚不开放此新入口，不等于所有宿主通用。

## 原生光栅引用读取与路径修复

影像引用属于外部资源，但不等于DWG图框Xref。原生RasterImage连接RasterImageDef；修复使用.NET SourceFileName及Load，不调用卫星图Skill，也不移动、旋转或缩放图形。

实体读取增加光栅源路径、定义身份、原点/U/V轴、旋转、图面宽高和裁切等事实；独立reference_read给出ActiveFileName、相对DWG解析路径、存在/原生加载状态。IsLoaded不证明画面可见或地理对齐。

`set_raster_path`为单步计划，字段`id,op,targets,path`；targets必须只有一个原生模型空间RasterImage及新鲜expected_sha256，path为明确绝对影像文件路径。保留原有对象、源定义身份、原点、尺寸、朝向、裁切、显示参数；只允许路径字段变化。共享定义关联多个引用、专业代理/Geomap/Wipeout、缺失新文件、锁层等拒绝。共享定义不能悄悄影响其他插入，故本版不做批量外参管理或文件自动搜索替换。

DWG外参重挂接/重载、纸空间影像、专业影像对象的路径属性、地理校准不在本轮声明中。定位到新文件由Agent判断适用性，路径修复不是影像配准。

## 反馈修复

- 明确计划解析拒绝返回`rejected_before_submission,registered:false,effects:none`；CLI记录终态，不再盲等不存在的任务。MCP参数绑定的结构化-32602错误同样归类；超时、失联、其它未确认错误仍查询原请求，不按错误字符串放宽保护。
- 实体摘要独立于include_geometry尝试读取，摘要不可用时返回digest_error；底座不接受空摘要写入。详细几何展开仍由include_geometry控制。
- 代理/复合实体内存分解产生的DBText/MText可返回可见文字、原始MTEXT内容、真实坐标、角度及基本尺寸；单项文字限8192字符，空分解与专有字段仍如实不可读。
- set_layer仍需完整字段：name、expected_sha256、color、is_off、is_frozen、is_locked、is_plottable。先读当前层事实后补齐；不是“无法关闭层”，本轮不放宽共享层旧状态保护。
- 普通引线沿用先创建、读取ActualWidth、再调set_callout_format/set_callout的两阶段范式；字框换行由Agent调整，不新增全图排版引擎。

## 验证与剩余范围

离线契约与CLI分类检查、合成图CAD2026抽测分别记录，不相互替代。原生控制台仅准备合成夹具，不算MCP执行证明；最终运行包验收结果以本轮交接和配套回执为准。编译已有Roslyn版本冲突及WebClient过时警告保留，不能误报为零警告。

本轮已完成：97项C#契约检查、4项CLI拒绝/未知分类回归；独立合成DWG通过打包MCP九工具目录、基础读摘要与完整读摘要一致、复合块内MTEXT读取、当前视图、窗口/中心定位及原生截图。查看前后三个原生模型对象摘要一致。新路径缺失的计划无效果拒绝，明确非法计划入队前拒绝；随后有效光栅路径修复提交/读回通过，比较仅source_file变化。原生截图实际查看已显示新路径影像；整图保存独立磁盘核验通过。测试自有CAD进程已正常退出，两侧写入栅栏均为0；没有改动或关闭其它正在工作的CAD进程。

首轮集成发现新视图入口未加入原生队列允许表，被明确拒绝，未修改图形；补齐后在新的独立工作区通过上述抽测。旧失败回执保留，不改写为通过。合成样本不等于任意专业影像或工程图全部兼容，不把IsLoaded当作视觉验证；本轮实际可见结论仅针对抽查的合成PNG。

使用新版CLI的示例：`foundation.cmd view --workspace C:\CAD-work\demo01 --arguments C:\CAD-work\view.json`。先使用新版运行包install/start自有副本；不要拿新版入口绑定旧版在用会话。旧运行包与旧入口保留用于既有工作区，未知写入不升级重放。

专业管道/节点公开设计属性读取、旧宿主适配、DWG兼容格式另存、字体替代保存口径及更广外参操作保留后续项，本轮不为全部兼容扩大开发。未知写入、图纸身份与正式图保护不放宽。
