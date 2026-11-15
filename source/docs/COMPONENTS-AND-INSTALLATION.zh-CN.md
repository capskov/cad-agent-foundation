# 按任务选择与安装组件

[English](COMPONENTS-AND-INSTALLATION.md) · [底座快速入门](QUICKSTART.zh-CN.md) · [兼容边界](COMPATIBILITY.md)

按任务选择组件。两个 Skill 独立维护、按需安装，不是底座的全局强制依赖。安装 Skill 获得的是规则与资源，不会补出缺少的 CAD 操作。目前没有自动下载 Skill 或自动安装依赖的机制。

| 任务 | 所需组件 | 版本与限制 |
| --- | --- | --- |
| 已支持的基础 CAD 操作 | 仅 `cad-agent-foundation` | 本地基线 `v1.0.0-local`；公开源码预览`v1.1.0-rc.1`，提供带清单运行ZIP。有界二维操作，local.4原坐标操作不以单位为前置条件，仅有记录的 Windows／AutoCAD 2026 宿主实测。无需两个 Skill。 |
| 本项目的引线标注样式与排布 | 底座 + `cad-callout-pair-layout` | 引线公开预览v0.3.0-rc.1（沿用v0.3规则）。默认与验证来自米制道路图；v1.1已验证样式／位置接口及有限合成Skill联动，完整实战与背景覆盖仍按任务验收，写入前须核对后端覆盖。 |
| 本项目的病害／拆除标注工作流 | 底座 + `cad-distress-annotation` + `cad-callout-pair-layout` | 病害规则公开预览v0.1.0-rc.1。需要工程依据、分类规则及标注规则；完整流程仍需首版配置之外的能力，尚未验证仅靠这三个组件即可全部复现。 |

## 职责与实际操作范围

底座负责受支持原生操作的执行与核验，包括图纸身份、请求归属、有界写入和读回。目前支持创建／复用颜色匹配的图层，新建直线、圆、二维多段线、DBText／MText、基于同一计划中先创建边界的关联填充，以及普通 LEADER／MText／黄色实心圆点基础组；支持旧值摘要保护下的文字更新、受支持对象或完整关联组的移动与归档、读取及整图保存后的指定对象磁盘核验。这不是任意图层管理或任意对象编辑。详见[能力与限制](CAPABILITIES.md)。

标注 Skill 提供文字内容约定、圆点和引线样式、已确认的道路侧／开口方向，以及有限预算的排布与避让规则。底座能创建基础标注组，不等于内置完整标注工作流。病害拆除 Skill 提供有工程依据的专业范围、分类和工程规则，并使用标注 Skill 表达成果。底座不独立决定病害处治或凭图纸猜工程量。

底座用途不限道路土建：其他专业可在相同执行限制下使用基础能力。实际验证来自米制土建图；WCS 是坐标参照，不规定单位或地图投影。local.4推荐 `units: raw_drawing_units`，`expected_insunits`可选；不换算、不修改INSUNITS，旧米制接口保留。园林植物／品种等标注只是可拓展用途，现有 Skill 的尺寸、样式与规则适配也尚未验证。

本版提供四种原生尺寸及有界闭合边界／单填充续改；MLEADER、通用旧填充修复、任意多段线属性、布尔／复杂孔洞、布局出图和外参仍不提供。增加 Skill 或另装 MCP 不会自动扩大底座治理范围。

## 仓库与安装入口

下方为实际公开入口；两个Skill已有手动安装ZIP，底座提供独立带清单运行ZIP。

| 组件 | 公开仓库／预览 | 安装入口 |
| --- | --- | --- |
| 底座 | [cad-agent-foundation（源码预览）](https://github.com/capskov/cad-agent-foundation) | [底座安装与调用](QUICKSTART.zh-CN.md)；[运行ZIP](https://github.com/capskov/cad-agent-foundation/releases/tag/v1.1.0-rc.1)。 |
| 标注 Skill | [cad-callout-pair-layout v0.3.0-rc.1](https://github.com/capskov/cad-callout-pair-layout) | [引线安装](https://github.com/capskov/cad-callout-pair-layout/blob/main/docs/INSTALL.zh-CN.md) · [Release ZIP](https://github.com/capskov/cad-callout-pair-layout/releases/tag/v0.3.0-rc.1)。 |
| 病害拆除 Skill | [cad-distress-annotation v0.1.0-rc.1](https://github.com/capskov/cad-distress-annotation) | [病害安装](https://github.com/capskov/cad-distress-annotation/blob/main/docs/INSTALL.zh-CN.md) · [Release ZIP](https://github.com/capskov/cad-distress-annotation/releases/tag/v0.1.0-rc.1)，需本项目引线表现时同级安装引线Skill。 |

按Agent客户端支持的手动安装流程放置已公开的Skill ZIP。两个 Skill 文件夹应同级，名称保持不变，各含 `SKILL.md` 和所需资源，以便病害拆除规则引用标注规则。`foundation.cmd install` 只安装底座，不负责 Skill 的安装或客户端发现；不要虚构 `--skills` 参数。

## Agent 执行前检查

1. 按任务表确认所需组件，检查实际安装的版本和资源，不能只看仓库名字。
2. 发现实际 CAD 工具及参数，核对宿主、图纸单位、身份和每项操作，包括样式与布局语义。没有“Foundation ≥ 1.0 就完全兼容两个 Skill”的承诺。
3. 缺组件时明确名称和上述安装入口；尚未公开时如实说明。缺工具能力时单独说明缺项，只推进不依赖它的工作。
4. 绑定正确工作副本并遵守写入／读回约束。外部工具仅能走经核实且有授权的路线，不共享底座事务、请求编号或未知写入栅栏；不能在工具拒绝或写入未知后自动转 COM、通用代码或换后端。应查询原请求。

后续开发边界：可复用原生操作和执行约束归底座，专业判断、分类、样式和排布规则归 Skill。本轮只完善说明，不新增自动安装或 CAD 操作。

The Foundation-based rows describe one selected execution route, not a mandatory backend for independent scope review, offline geometry or BZYD. / 上表的底座组合描述选用底座的执行路线；范围分析、离线几何与BZYD不强制依赖底座。


## Optional satellite/aerial road tracing / 可选影像道路描绘

[Agentic Map Tracing](https://github.com/capskov/agentic-map-tracing), v0.1.0-beta.1, supplies the `satellite-road-edge-tracing` Agent Skill. Recommended native backend: Foundation v1.2.0-beta.3. Agent interprets markings, road composition, occlusion and human guidance; Foundation handles bounded native operations and feedback. Optional for this task, not a prerequisite for basic CAD or other Skills. Installing a Skill does not install Foundation or provide missing native operations.
