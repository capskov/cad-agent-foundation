# CAD Agent Foundation — AutoCAD MCP／CLI 智能体操作接口

## 首要任务：为智能 Agent 提供可靠操作，而非编程替代判断

用户于2026-10-16反复明确，本条置于最前，指导后续开发、取舍和验收：

- 底座开发是实战任务的辅助工作。首要目标是提供可靠、可控的基础CAD操作和真实反馈，让Agent能够执行自己的判断并持续向前推进。
- 按Agent会越来越聪明来设计：审美、结构理解、样图学习、规律归纳、复杂取舍和最终看图复查由Agent承担；不要提前把这些能力固化为程序、规则引擎、拟合算法或强制验证流程。
- 现有操作足以执行，且Agent综合判断可以继续时，就推进主线。不要为了代替Agent判断，再写一套程序或反复验证。
- 编程解决明确、小范围的基础操作问题。除用户明确要求增加的小功能外，不主动顺带扩功能；遇到实战阻塞先指出具体基础能力缺口，限定补充范围，不扩大成完整自动化工程。
- 验收关注操作是否可靠、反馈是否真实。画面是否满意由Agent复查判断；抽查或全面复查按用户当前要求，不自行加码。
- 保留图纸身份、正式图保护、未知写入不重放等执行安全边界；它们属于可靠操作，不是审美或工程判断算法。

本地单位收尾版：`1.2.0-local.4`，延续既有有界操作，原坐标计划的INSUNITS核对改为可选，保留旧米制接口。[单位、精度与出图字高](docs/DRAWING-UNITS-AND-TYPOGRAPHY.zh-CN.md)。公开Release仍为下文的`v1.1.0-rc.1`；本轮仅本地交付，不表示A～P已全部完成。

**从“能调用 CAD”走向“适合 AI Agent 持续执行 CAD 任务”。** 很多 CAD MCP 方案首先解决 AI 如何连接、查询和操作 CAD；CAD Agent Foundation 更重视命令发出后的**执行闭环**：绑定正确图纸、追踪请求与状态、反馈实际结果、核验写入和保存，并据此安全地推进多步骤任务。

**专业规则由 Skill 提供，CAD 操作与执行反馈由底座承担。** 自行开发或第三方专业 Skill 可以复用底座开放的操作接口，组合实现不同工程任务。这不仅是一组 CAD 命令，更是一套面向 AI Agent 协作的执行基础设施。


**公开源码预览v1.1.0-rc.1（有界原生操作与Windows运行包预览）。** 本次提供源码、文档与带清单Windows运行ZIP，前置软件仍须独立安装。[构建前置条件与限制](docs/BUILD.zh-CN.md)。已验收本地运行版 v1.0.0-local 为独立基线；两个独立Skill已公开：引线v0.3.0-rc.1、病害v0.1.0-rc.1。

[English](README.md) · [安装与快速入门](docs/QUICKSTART.zh-CN.md) · [能力与限制](docs/CAPABILITIES.md)

底座已注册官方MCP Registry：`io.github.capskov/cad-agent-foundation`。[MCPB安装与检索入口](docs/MCP-DISCOVERY-AND-INSTALLATION.md) · [标准注册信息](server.json)。仍需本机Windows／AutoCAD环境；注册不会自动获得图纸操作权限。

**通过MCP／CLI与原生.NET API，让AI Agent以结构化接口读取和操作AutoCAD图纸。**

支持有界二维绘图、文字、填充、普通引线等AutoCAD自动化操作；图纸身份、请求记录、写后读回及保存核验由底座管理。MCP即Model Context Protocol，底座是CAD操作桥接接口。

让智能体明确绑定图纸，持久记录请求归属与执行状态，在回应不明确时查询原请求，并在写入和保存后核对指定对象。当前实现集成在经过修改、固定来源版本的 [Bimwright DWG MCP](https://github.com/bimwright/dwg-mcp) 宿主中。

维护者：**capskov**。当前预览为**v1.1.0-rc.1**，附独立构建运行ZIP和局部实机证据；已验收v1.0.0-local基线保留，不承诺所有AutoCAD2026补丁兼容。

## 为什么开发

项目来自道路土建 CAD 实战：圈边界、做填充、统计数量、安排关联标注，再经过多轮修改、保存和续作。这个过程中，智能体除了需要能用的绘图命令，还需要回答：

- 我操作的究竟是哪张图、哪个实例？
- 这次请求已经执行了吗，还是只是回应丢失？
- 实际对象与预期一致吗，磁盘上的图是否已保存？
- 如果继续尝试，会不会重复创建或重复修改？
- 目前是已核验、失败，还是仍无法确认，应该怎样安全停下？

这也是最初的项目思路：在现成 CAD 执行能力上建立可追踪、可核验的执行约束，支持更长的真实工程工作流。工程范围和排布规则保留在独立 Skill；底座维护身份、请求、状态、写入与读回之间的约束。

增加持久记录和核验会带来额外开销。目前没有对照数据证明比其他项目更快、更省算力或更高效，也不宣称事务、去重、状态机等思想由本项目独创。

## 新版原生能力与安装

下载[v1.1.0-rc.1运行包](https://github.com/capskov/cad-agent-foundation/releases/tag/v1.1.0-rc.1)，核对SHA256SUMS.txt并保留完整解压目录。需合法兼容的Windows x64 AutoCAD2026、.NET8桌面运行时和Python3.11+。不再分发Autodesk程序集或SHX字体。[安装](docs/QUICKSTART.zh-CN.md)／[25项操作清单](docs/COMMAND-CATALOG-v1.1.0-rc.1.zh-CN.md)／[新版验收](docs/VALIDATION-v1.1.0-rc.1.md)。

本版补已安装字体样式、尺寸样式、四种原生DIMENSION、图层状态、闭合边界／单关联填充续改、普通引线位置／格式，以及受支持完整组的旋转／缩放／同图复制。工程范围和排布算法继续留在Skill。

## 当前能力总览

成熟程度：有限能力的本地运行版已有实机验收；当前提供带依赖许可、哈希和插件MVID的运行ZIP。按实际开放目录判断支持范围。

| 类别 | 当前能力 | 状态／限制 |
| --- | --- | --- |
| 基础绘图 | 新建图层、直线、圆、二维多段线、前序新边界关联填充 | 指定本地流程已验；非任意对象编辑／所有填充场景。 |
| 文字与引线 | DBText单行、MText多行、普通LEADER／MText／黄色实心点组 | 创建已验；完整工程样式和排布由Skill提供。 |
| 修改与对象管理 | 摘要保护的关联文字修改、完整标注组移动／归档 | 指定组流程已验；其他对象分支未全部逐项实测。 |
| 执行与交付 | 图纸绑定、原请求查询、防重复、整图保存及磁盘核验 | 本地流程已验；未知写入不自动重放或解锁。 |
| 读取与辅助 | 对象属性／摘要、图层列表、筛选分页、静态Lisp检查 | 对象读回已验；其他接口有实现，但缺本版部分单项端到端证据。 |
| 新增有界操作 | 四种原生DIMENSION、命名匹配样式、图层状态、边界／引线续改、旋转缩放复制 | 实际参数和逐项证据见25项版本清单。 |
| 当前未开放 | MLEADER、共享样式覆盖、任意对象编辑、复杂布尔孔洞、布局打印外参 | 不宣称覆盖AutoCAD全部对象与命令。 |

[完整接口、调用方式与逐项验证状态](docs/COMMAND-CATALOG-v1.1.0-rc.1.zh-CN.md)。底座提供可复用原生操作、执行约束、状态和对象管理；独立Skill提供行业规则、样式参数、排布算法与业务流程，通过底座实际开放接口调用。接口或兼容组合缺失时须说明，不能用Skill安装代替原生能力实现。

## 执行约束

先绑定目标图，核对进程／会话／插件身份；提交前持久记录稳定请求，回应未知时查原请求。旧对象修改需要新读到的摘要和完整关联成员，分别核对提交、独立读回与栅栏释放。保存影响整张工作图。详见[架构](docs/ARCHITECTURE.md)与[版本接口清单](docs/COMMAND-CATALOG-v1.1.0-rc.1.zh-CN.md)。

## 运行与验收范围

当前目标是 Windows x64 的 AutoCAD 2026，向二维模型空间写入有界计划。坐标、长度和标注尺寸按图纸单位解释；WCS 表示 CAD 坐标参照，并不规定米制，也不等于高斯投影。底座不进行地图投影或单位换算。

**本地1.2.0-local.4普通写入按DWG原始数值执行**：推荐 `units: raw_drawing_units`，仅任务需要一致性核对时填写可选的 `expected_insunits`。未知单位、无单位不阻止原坐标操作；不换算坐标、不主动缩放图形、不修改INSUNITS。旧 `units: metres` 保留原有0/6元数据约定。[单位、精度及出图字高](docs/DRAWING-UNITS-AND-TYPOGRAPHY.zh-CN.md)说明Agent职责；PDF/DXF仍有独立限制。默认关闭写入，打开时仍只暴露有限计划入口。已在两份不同来源图纸的工作副本中完成创建、读取、关联文字修改、完整组移动、归档、保存、关闭重开和读回；CLI/MCP 与本地安装包也有验收记录。

这些证据不等于任意工程图、任何 AutoCAD 2026 补丁或每个 MCP 客户端均已验证。软件发现的身份未知、提交未知和核验未知必须如实保留，不能统一写成成功，也不能把提交后核验失败当作已经回滚。

首个目标选择 AutoCAD 2026，是因为它匹配实际工作环境及采用的 Bimwright 原生插件路线，便于围绕一个宿主建立安装和执行证据。当前本地基线使用 .NET 8、文档锁和事务；2025 也使用 .NET 8，较早版本也有自动化接口，MCP 由桥接服务提供。[选择依据](docs/WHY-AUTOCAD-2026.md)

实测宿主记录为 R25.1.74.0.0；2026.1.2 引入 .NET 10 更新后的兼容性尚未验证。[官方运行时更新](https://blog.autodesk.io/autodesk-desktop-products-2025-2026-net-10-updates/)

已补四种原生尺寸及有界闭合边界／单填充修改。未开放：多重引线MLEADER、通用复杂填充／布尔孔洞、任意多段线属性、跨图复制、代理语义、布局出图、自动工程判断、未知写入自动解锁。CLI 的保存作用于整张工作图，而不是仅保存所列对象。

## 按任务选择组件

| 任务 | 所需组件 | 版本与限制 |
| --- | --- | --- |
| 已支持的基础 CAD 操作 | 仅 `cad-agent-foundation` | 本地基线 `v1.0.0-local`；公开源码预览`v1.1.0-rc.1`，提供带清单运行ZIP，前置软件独立安装。有界二维操作，local.4按原始数值执行，单位元数据核对可选，仅有记录的 Windows／AutoCAD 2026 宿主实测。无需两个 Skill。 |
| 本项目的引线标注样式与排布 | 底座 + `cad-callout-pair-layout` | 引线公开预览v0.3.0-rc.1（沿用v0.3规则）。默认与验证来自米制道路图；v1.1已验证样式／位置接口及有限合成Skill联动，完整实战与背景覆盖仍按任务验收，写入前须核对后端覆盖。 |
| 本项目的病害／拆除标注工作流 | 底座 + `cad-distress-annotation` + `cad-callout-pair-layout` | 病害规则公开预览v0.1.0-rc.1。需要工程依据、分类规则及标注规则；完整流程仍需首版配置之外的能力，尚未验证仅靠这三个组件即可全部复现。 |

基础操作不需要两个 Skill；Skill 规则并未内置到底座，安装它们也不会补出缺少的原生操作。底座源码与两个独立Skill预览均已公开。[安装入口、版本与 Agent 检查](docs/COMPONENTS-AND-INSTALLATION.zh-CN.md)。

## 入门、Skill 和来源

已有[匿名道路拆除案例](examples/road-demolition/README.zh-CN.md)：标注前后各四页，附主要工程量、路面工程量和路口桩号依据表。这展示配套Skill的历史实战效果，不作为全流程仅依靠底座首版完成的证据。

使用入口为 `foundation.cmd`，安装和调用详见[快速入门](docs/QUICKSTART.zh-CN.md)。使用本版独立构建、带清单的公开运行ZIP；旧稳定本地包保留。

底座负责“在指定图纸上执行什么操作，以及执行结果是否有证据”；Skill 负责“根据专业任务，应该画什么、怎样排布与检查”。底座本身不判断植物品种、病害处理范围或工程量含义。

已独立发布[cad-callout-pair-layout v0.3.0-rc.1](https://github.com/capskov/cad-callout-pair-layout)与[cad-distress-annotation v0.1.0-rc.1](https://github.com/capskov/cad-distress-annotation)。[引线安装](https://github.com/capskov/cad-callout-pair-layout/blob/main/docs/INSTALL.zh-CN.md)／[病害安装](https://github.com/capskov/cad-distress-annotation/blob/main/docs/INSTALL.zh-CN.md)。前者管理引线文字、圆点和有限排布，后者管理道路病害/拆除的证据、范围与工程规则。引线标注这种方法也可用于园林植物等分类说明；现有 Skill 的验收背景和默认规则仍来自米制道路图，其他专业和毫米图需要适配尺寸、样式与排布规则，尚未验证。两项 Skill 保留当前节点，尚未确认所有流程都能仅用 Foundation v1.0 完成，不能写成已完全迁移。

其他 MCP、COM 或工具可以另行使用；**只有经过当前底座入口的受支持操作，才进入它的请求记录、执行约束和核验流程**。它不是自动包住任意外部工具的保护层。外部写入不自动共享事务、请求身份或写入阻隔，不能以切换工具绕过未知写入状态。详见[底座、Skill 与外部工具的关系](docs/FOUNDATION-SKILLS-AND-OTHER-TOOLS.md)。

研究过程在首页只保留简述：先按真实工程任务考察现成 CAD MCP/后端，再以固定版本 Bimwright 原生宿主集成执行约束，最后对受限流程做 CAD 写入、读回与保存重开核验。具体采用、参考与评测关系放在[背景与相关项目](docs/BACKGROUND-AND-RELATED-PROJECTS.md)，感谢与许可按实际来源列明。

Bimwright 是当前集成宿主，来源固定在 [4d0f756](https://github.com/bimwright/dwg-mcp/commit/4d0f75646631186612af926944d7e33affc4757d)。公开源码采用 Apache-2.0，保留原第三方许可和署名；首个公开源码预览为 `v1.0.0-rc.1`，本版附独立审核的运行ZIP；旧版本保留原范围。字体与依赖不是自动归入代码许可；AutoCAD/SDK不打包，未确定授权的 HZTXT 字体不随公开版分发。项目独立维护，未获 Autodesk 或上游作者背书。

欢迎人类及 AI 辅助贡献者提交可审查的改进和验证证据，合并与发布由维护者审核。见[贡献说明](CONTRIBUTING.md)。


## 2026-10-16 local font update

Local 1.2.0-local.3 adds guarded existing-style SHX font-pair replacement and anchor-preserving DBText metric scaling. See [scope and parameters](docs/FONT-REPLACEMENT-LOCAL3.zh-CN.md). Native evidence is scoped; prior public releases and global MCP registration are unchanged.

Local 1.2.0-local.4 unit closeout: [changes, native evidence and limits](docs/UNIT-CLOSEOUT-LOCAL4.zh-CN.md). This is a local deliverable; GitHub publication is deferred.
