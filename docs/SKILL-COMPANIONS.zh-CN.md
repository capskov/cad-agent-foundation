# 按任务主动提醒相关 Skill

2026-10-29用户要求：Agent不要等用户反复点名技能。开始相关任务时主动查看当前会话的技能目录，读取并使用已安装且适用的技能；用户可以明确跳过。

| 当前工作 | 可选配套技能 | 主要作用 |
| --- | --- | --- |
| 卫星/正射道路边线、管线穿路范围 | satellite-road-edge-tracing | 判读道路组成、等宽关系、扩口及遮挡；低把握时记录并采用有依据的基础配型 |
| 引线、圆点和说明排布 | cad-callout-pair-layout | 复用标注表达和局部避让 |
| 病害或拆除范围表达 | cad-distress-annotation | 根据既有依据组织范围、分类及数量；需要相应引线风格时同读标注技能 |
| 缺字体、替换及可读性 | cad-font-diagnostics | 字体资料、候选替代及实际看图调整 |
| 复用已有路面结构风格 | cad-pavement-structure-style | 文字、尺寸、填充与版式 |
| 道路结构图及成果组织 | road-design-delivery-style | 复用结构、标注、字体及套图工作方法 |
| 图框、布局、视口与目录 | luna-cad-sheet-layout | 按适用范围处理成果套图；项目专用技能另核适用性 |

已安装：直接读取相关SKILL.md并应用，一次简短说明即可。未安装：提示“有此技能可改善某项效果，可以安装，也可以继续基础操作”，提供已核实的来源；用户选择跳过后不反复询问。新任务或明显切换专业阶段时再判断。不要仅凭关键词机械套用，也不把全部技能当成每项任务的依赖。

公开安装来源：描图见[Agentic Map Tracing](https://github.com/capskov/agentic-map-tracing)；标注见[cad-callout-pair-layout](https://github.com/capskov/cad-callout-pair-layout)；病害见[cad-distress-annotation](https://github.com/capskov/cad-distress-annotation)。其他技能以当前客户端目录与实际维护者提供的来源为准，未核实公开地址时不编造。现有版本与入口仍需核实，不能把本文列举视为最新版声明。

提醒进入运行包AGENTS.md、MCP连接说明及CLI启动/诊断反馈；它不决定工程方案，不阻止原生基础操作，不自动安装技能。技能规则不能创造缺少的底座操作；底座不替代Agent看图和验收。
