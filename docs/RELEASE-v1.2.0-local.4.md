# v1.2.0-local.4 — 原始单位执行与字体操作预览

这是从v1.1.0-rc.1升级的有界运行预览，保留版本号1.2.0-local.4，以对应已经CAD实测的二进制。新增功能分阶段开发，以下明确区分本轮与历史验证范围；不表示全部CAD操作、专业规则或客户端均已通过。

## 主要变化

- 普通操作推荐`units: raw_drawing_units`，DWG原始坐标及数值直接执行。`expected_insunits`改为可选；任务明确要求单位一致性时才填写。未知/无单位不阻止原坐标操作，底座不主动换算、缩放或修改INSUNITS。旧`metres`接口保留0/6约定。
- 保存入口使用原始单位模式。INSUNITS用于辅助Agent理解图纸、判断工程精度和制图方式；读回数值容差与工程允许误差分开，不增加单位规则引擎。
- 文字样式字体对替换`set_text_style_font`、保留锚点的DBText字高/字宽倍率修改`set_text_metrics`。字体选择和换后看图调整由Agent负责；目标字体对限CAD可搜索到的SHX，不打包或下载SHX，不清除MText内嵌字体。
- 延续1.2阶段的点/弧/椭圆/矩形、属性/线圆/开放多段线编辑、限定偏移与镜像复制、表格、简单命名块/属性、页面设置、单张PDF和指定对象读回DXF等有界原生操作。实际目录提供49项计划操作，不能据命令名推断未实现的对象范围。
- Agent说明随运行包提供。用户或Skill要求优先；未指定时按图框、纸张及打印比例反算，普通汉字纸面优先2.5～3.5mm，小于2mm检查可读性。保持工程尺寸和正式比例正确；审美、复杂判断与最终复查由Agent承担。

图纸身份、请求归属、旧值摘要、事务和未知写入不重放保护继续保留。读取effects、verification和write_fence_released，不能只看进程退出码。回应不明时查询原请求，不重放或绕过栅栏。

## 安装与升级

需Windows x64、合法且兼容的AutoCAD2026/.NET8、.NET8 Desktop Runtime及Python3.11+。下载本版本Windows运行ZIP并完整解压；MCPB用户可安装同版MCPB，或解压后进入其runtime目录。两者运行代码相同。

```bat
foundation.cmd install --workspace "C:\CAD-work\v124-demo"
foundation.cmd start --workspace "C:\CAD-work\v124-demo" --source "C:\drawings\input.dwg" --write
foundation.cmd doctor --workspace "C:\CAD-work\v124-demo"
```

升级请使用新工作目录，安装并加载同版本插件；不要把新入口指向旧版本活动会话。来源图只复制到自有工作副本。省略`--write`则只读；MCPB本身不会启动CAD或开启写入。工作目录产生的mcp-config.json可供普通stdio客户端使用。MCP与CLI不要同时占用同一工作目录，保存并正常停止后再切换。

[快速入门](QUICKSTART.zh-CN.md) · [单位与文字指导](DRAWING-UNITS-AND-TYPOGRAPHY.zh-CN.md) · [字体操作边界](FONT-REPLACEMENT-LOCAL3.zh-CN.md) · [1.2阶段操作说明](PHASE2-DEVELOPMENT-20261015.zh-CN.md)。GitHub新包与官方MCP Registry已发布记录属于不同渠道；此次GitHub更新不自动改动Registry线上记录。

## 验证范围

本轮实测插件MVID：`8a181360-77f8-434d-8274-13485d32c797`，SHA256：`F1A671366CD438C4D26B01F85A9622B24D5612A5C501C6D27505BB2E66D86AD0`。

当前版本：INSUNITS=6/4/0三份合成图，通过打包MCP完成原坐标创建、移动、复制、文字修改、原生标注、填充、CLI保存和独立磁盘数据库读回。17次写入提交验证通过，7次拒绝确认无写入；显式单位失配、旧摘要、毫米图旧米制调用拒绝均符合约定。来源图未改，新建测试CAD正常关闭，两侧执行栅栏均为0。未知/缺失元数据的处理另由契约检查覆盖。

现有契约76项、入口保护7项通过；插件/服务端构建成功。插件有原有3项警告（Roslyn引用版本冲突及旧WebClient）；不是新增CAD宿主兼容证明。发布包与本轮实测候选的插件、服务端、客户端、支持代码及依赖逐文件哈希相同，发布仅补齐文档、标准安装封装和验收元数据。

历史阶段：local.1对其当时二进制验证过表格、命名块/属性、页面设置、PDF/DXF及旧流程；local.3对其当时二进制验证过字体替换、DBText倍率、MText缓存刷新和保存读回。这些功能包含在当前源码中，本轮没有宣称在local.4上重新穷尽以上全部组合或字体图面核查。[历史1.2阶段验收](PHASE2-LOCAL-ACCEPTANCE-20261015.zh-CN.md)。

合成空白来源图的单位通过既有原生启动脚本（AutoLISP/COM）准备并单独记录；这是测试准备，不称作MCP验收。上述实际修改和保存通过打包MCP执行。私有图纸、原始回执、日志、凭据和机器路径不随发布分发。

## 仍有限制

- PDF/DXF仍有独立INSUNITS=0或6门槛；PDF页面范围仅layout/extents。本轮未扩展或重新验收输出分支。
- 二维WCS、有界实体/数量与现有数值范围保留；未认证极端数值、所有INSUNITS枚举或所有工程精度。工程允许误差由用户、Skill或Agent选择。
- 目标字体替换限安装字体搜索路径中的SHX对；不保证专业符号、任意字体组合、内嵌字体或所有字形兼容。没有自动字体排版。
- 不提供任意代码、通用删除、复杂布尔、任意动态块/外参编辑、跨图复制或未知写入自动解锁/重放。AutoCAD其他年份及.NET10宿主未验证。

## English summary

This prerelease publishes the exact native-tested 1.2.0-local.4 runtime. Ordinary plans use raw DWG values; INSUNITS is auxiliary metadata and expected_insunits is optional. Legacy metre calls remain compatible. Includes bounded SHX style replacement, explicit DBText metric scaling and the earlier 1.2 primitives. Agent instructions cover units, engineering-versus-readback precision and paper text guidance.

The current three-profile native run covers raw creation, moves, copies, text, dimensions, hatches, save and disk readback: 17 committed/passed writes and seven confirmed no-effect refusals. Broader font and phase-two evidence belongs to earlier binaries and is not relabelled as exhaustive current-version certification. PDF/DXF retain their separate unit gates. Install into a fresh owned workspace with matching runtime/plugin; prerequisites remain separate. Registry publication, all-client compatibility and automatic engineering/layout decisions are not implied.
