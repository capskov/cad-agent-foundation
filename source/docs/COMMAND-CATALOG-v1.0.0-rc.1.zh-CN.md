# 底座接口与能力清单 — v1.0.0-rc.1

[English](COMMAND-CATALOG-v1.0.0-rc.1.md) · [README](../README.zh-CN.md) · [验证记录](VALIDATION.md)

此清单核对公开源码标签 `v1.0.0-rc.1`（提交 `31795b860103a60a431d1575a3f24cee76ea8324`）和已有本地 `v1.0.0-local` 实机记录；本轮没有重新运行CAD。公开版仍为源码预览，没有可运行安装包。调用方式描述当前包接口；必须先有完整的带清单安装包与正确工作会话。

**V：已实现、在指定本地流程验证。I：已实现，但缺本版单项实机／端到端验证；离线和历史证据另列。N：当前底座配置未支持。** V不是所有参数／图纸／客户端兼容保证。仅有代码或AutoCAD原生能力，不足以标V。

## CLI入口：10项

以下每行均为 `foundation.cmd <action> <arguments>`；W是工作目录。`start --write`明确开启有限写入。`--headless`代码参数存在，但不构成独立无界面CAD宿主或无人值守运行验收。

| action | 参数 | 用途 | 状态 |
| --- | --- | --- | --- |
| `install` | `--workspace W [--cad CAD_DIR]` | 版本化可信原生安装，核对包文件。 | V |
| `start` | `--workspace W --source INPUT.dwg [--write]` | 启动自有工作副本，默认只读。 | V |
| `doctor` | `--workspace W` | 绑定并检查身份／插件元数据。 | V |
| `read` | `--workspace W --handles H1,H2` | 读取对象并查询原请求。 | V |
| `query` | `--workspace W --arguments query.json` | 提交对象过滤查询。 | I |
| `apply` | `--workspace W --plan plan.json [--request-id ID]` | 仅提交一次，之后查同一计划请求。 | V |
| `save` | `--workspace W --handles H1,H2 [--request-id ID]` | 读新摘要后提交独立保存计划。 | V |
| `status` | `--workspace W --request-id ID` | 查询工作目录requests中已记录请求。 | I |
| `stop` | `--workspace W` | 仅关闭自有、空闲、已保存且无未决栅栏的图纸。 | V |
| `mcp` | `--workspace W` | 通过同一已配置宿主提供stdio MCP入口。 | V |

`query`和独立`status`分支已实现，本版验收摘要未单列其CLI流程；原请求查询通过不自动证明所有CLI分支已验。`doctor`不是持续监控，`stop`不是强制结束。

## MCP：每个模式8项，共9个不同接口

RO为只读，W为明确启用写入。W用`dwg_apply_plan`替换RO的`dwg_inspect_lisp`，不是在8项外无限增加命令。经`tools/list`发现实际schema，再以`tools/call`和`{name, arguments}`调用。动态包装会加入`context_id`／`request_id`，不能只根据底层C#方法签名猜参数。读取提交后查询原job，保存写入后核验提交、读回和栅栏状态。

| 接口名 | 模式 | arguments | 功能 | 状态与证据边界 |
| --- | --- | --- | --- | --- |
| `dwg_bind_active_document` | RO/W | `{}` | 绑定当前目标端点与活动图纸，返回上下文及捕获身份。 | **V** — 本地CLI/MCP精确绑定通过；快照，不是持续监测。 |
| `dwg_list_layers` | RO/W | `context_id, request_id` | 读取图层标量属性。 | **I** — 已实现／开放；本版验收摘要没有可明确归到此接口的单项实机调用证据。 |
| `dwg_get_entity_properties` | RO/W | `context_id, request_id, handles: JSON-array string, include_geometry?` | 按1–64句柄读取受支持属性／几何与原生摘要。 | **V** — 创建／修改／保存重开读回通过；不支持或截断信息仍须如实处理。 |
| `dwg_query_entities` | RO/W | `context_id, request_id; entity_type?, layer?, color_index?, min_x?, min_y?, max_x?, max_y?, limit?, page_token?, include_geometry?` | 过滤模型空间顶层候选对象并分页。 | **I** — 已实现／开放，已有早期受限查询证据，本版未独立认证完整过滤／游标组合。矩形仅与外包框相交，limit为1–5000；修改图纸使游标失效。 |
| `dwg_job_get` | RO/W | `context_id, request_id, wait_ms: 0` | 查询原持久请求，不再次提交。 | **V** — 服务重启／重复计划后查询原请求通过；wait_ms必须为0。 |
| `dwg_job_get_historical` | RO/W | `context_id, request_id, observer_context_id` | 由另一已绑定观察端读取持久化dwg_list_layers历史请求。 | **I** — 仅限此历史路线，不是任意请求接管；未找到本版新增单项实机验收，历史／离线证据单列。 |
| `dwg_job_cancel` | RO/W | `context_id, request_id` | 仅请求取消本上下文拥有的排队任务。 | **I** — 取消／账本语义有离线合同覆盖，无本版单独CAD取消证明；不能撤销运行中或已提交写入。 |
| `dwg_inspect_lisp` | RO only | `file? (absolute local path), code?; at least one` | 本地静态检查Lisp模式，不执行。 | **I** — 无需CAD往返；不等于杀毒或执行授权，写模式目录不包含它。 |
| `dwg_apply_plan` | W only | `context_id, request_id, plan_json: JSON string` | 提交下表的有界计划操作。 | **V** — 两份本地原生流程及保存重开通过，不开放任意上游命令。 |

## 计划操作：12项，不是12个独立MCP工具

通过 `foundation.cmd apply --workspace W --plan plan.json`，或 `dwg_apply_plan` 的 `plan_json` 字符串调用。每步必须有唯一`id`和`op`，加下列所有字段；严格拒绝缺项／额外字段。顶层为 `schema: foundation-drawing-plan-v1`、`source_role`（annotation/existing/demolition/new_design/test）、`units: metres`、`steps`。二维点为`[x,y]`，polyline顶点可为`[x,y,bulge]`；rotation为度，hatch angle为弧度。

| op | 步骤字段（另加id/op） | 用途 | 状态与限制 |
| --- | --- | --- | --- |
| `layer` | `name, color` | 创建／复用颜色匹配的图层。 | **V** — 新建图层已验；不是任意图层状态或样式管理。 |
| `line` | `start, end, layer, color` | 新建直线。 | **V** — 原生创建／读取／保存重开已验。 |
| `circle` | `center, radius, layer, color` | 新建圆。 | **V** — 独立圆及标注点创建有证据。 |
| `polyline` | `vertices, closed, layer, color` | 新建二维多段线，可附顶点bulge。 | **V** — 闭合边界已验；开放／弧段参数组合未逐项完成本版实机验收，不编辑已有多段线形状。 |
| `text` | `text, position, height, rotation, layer, color` | 新建单行DBText。 | **V** — 内容／位置及默认样式创建已验；无显式样式／字体字段。 |
| `mtext` | `text, position, height, rotation, width, layer, color` | 新建多行MText。 | **V** — 创建已验；不是所有内嵌格式／字体／段落功能的验收。 |
| `callout` | `text, position, height, width, vertices, dot_radius, layer, color` | 普通Leader＋MText＋黄色Circle＋SOLID填充。 | **V** — 四成员关联已验。调用者给几何，不内置避让算法、工程样式工作流或MLeader。 |
| `hatch` | `boundary (earlier step id), pattern, scale, angle, layer, color` | 基于前序新建边界创建关联填充。 | **V** — 本地ANSI31和SOLID已验，不代表所有图案／孔洞／旧填充修复。 |
| `set_text` | `handle, expected_sha256, text, companions[]` | 带摘要保护的DBText／MText内容修改。 | **V** — 关联MText修改已验；DBText修改分支已实现但缺本版独立实机证明，须完整关联成员。 |
| `move` | `targets[{handle,expected_sha256}], delta` | 移动受支持对象／完整逻辑组。 | **V** — 四成员标注组移动已验。独立Line／DBText等分支未全部逐项实测，不宣称已有Polyline移动受支持。 |
| `archive` | `targets[{handle,expected_sha256}], layer` | 把受支持对象／组归档到关闭、冻结、不出图图层。 | **V** — 关联组已验；不是删除，也不是任意旧填充／多段线组管理。 |
| `save` | `expected[{handle,expected_sha256}]` | 整图保存及指定对象独立磁盘读回。 | **V** — 独立单步计划，1–64个预期对象。包含整图未保存改动，不证明所有对象几何完全正确。 |

计划1–16步、最多64对象结果、语义JSON128KiB。Polyline最多128顶点，abs(bulge)≤1；LEADER为2–16顶点；计划累计顶点≤1024。move/archive每步1–32目标。颜色ACI1–256；文字非空≤2048字符。当前写入需米制或明确为米的无单位图，不换算单位或投影。

## 重点核查与未支持项

| 能力 | 状态 | 实际边界 |
| --- | --- | --- |
| 普通引线标注组 | V | callout组合四原生对象；不是MLeader，完整样式／道路侧／避让由Skill提供。 |
| 单行／多行文字 | V（创建） | DBText／MText新建已验；MText关联文字内容更新已验；独立DBText更新无单项实机证据（I）。 |
| 高度、宽度、角度、图层、ACI颜色参数 | V（样本参数） | 只限计划明示字段，不等于完整样式管理或任意参数组合已验。 |
| 文字样式、字体、尺寸样式的列举／创建／修改 | N | 当前profile无独立样式管理接口；DBText／MText构造使用数据库默认值。Skill规则也不能凭空补出此接口。 |
| 原生线性／对齐／半径／直径DIMENSION | N | 上游DimensionTools等源码虽存在，当前profile不开放，未做本版实机验收。 |
| MLeader、多重引线样式 | N | 普通LEADER不是MLEADER。 |
| 旋转／缩放／复制／删除任意已有对象 | N | move/archive不等于通用编辑全集，归档不等于删除。 |
| 布局、打印、外参编辑、跨DWG、任意代码／Lisp执行 | N | 历史案例或上游存在此能力也不改变当前profile支持范围。 |
| 通用旧填充修复／布尔运算／复杂孔洞 | N | H1/J10已封板，不能把固定试点算成通用公开命令。 |

未开放工具示例：`dwg_create_leader`、`dwg_create_linear_dimension`、`dwg_create_aligned_dimension`、`dwg_create_radial_dimension`、`dwg_create_diameter_dimension`、`dwg_apply_unicode_style`。它们在保留源码中出现不等于可以通过底座当前目录调用；不要切换宽泛宿主绕过限制。

## 依据与版本维护

[本地实机摘要](VALIDATION.md)、[公开导出构建／离线检查](PUBLIC-EXPORT-VALIDATION.md)。前者是已有本地运行版证据，后者不是新二进制安装／CAD认证。每个后续Release须链接自己的版本清单，不以main分支未来能力替代历史版本事实。源码预览实际安装包限制见[BUILD](BUILD.zh-CN.md)。

核对源码：

- [foundation.py](../client/foundation.py)
- [readonly-entry.py](../scripts/readonly-entry.py)
- [Program.cs](../third_party/bimwright-dwg-mcp/fork/src/server/Program.cs)
- [QueryTools.cs](../third_party/bimwright-dwg-mcp/fork/src/server/Tools/QueryTools.cs)
- [FoundationExecutionTools.cs](../third_party/bimwright-dwg-mcp/fork/src/server/Tools/FoundationExecutionTools.cs)
- [FoundationDrawingPlanTools.cs](../third_party/bimwright-dwg-mcp/fork/src/server/Tools/FoundationDrawingPlanTools.cs)
- [FoundationDrawingPlan.cs](../third_party/bimwright-dwg-mcp/fork/src/shared/Execution/FoundationDrawingPlan.cs)
- [FoundationPlanWriteHandler.cs](../third_party/bimwright-dwg-mcp/fork/src/plugin-acad26/FoundationPlanWriteHandler.cs)
