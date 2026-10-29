# MCP discovery, registration and installation / 检索、注册与安装

CAD Agent Foundation is an **agent-first AutoCAD MCP and CLI execution foundation**, not just a command bridge. It combines drawing/request binding, execution feedback, write/save verification and safe continuation for supported CAD operations. Independent or third-party professional Skills can reuse those operations; the two companion Skills are separate repositories and do not replace server registration.

**中文速览：面向 AI Agent 的 CAD 执行底座，提供图纸绑定、执行反馈、结果核验与安全续作；支持与第三方专业 Skill 协同。**

## GitHub preview v1.2.0-beta.3

[Current release and upgrade notes](RELEASE-v1.2.0-beta.3.md). Download the [Windows runtime ZIP](https://github.com/capskov/cad-agent-foundation/releases/download/v1.2.0-beta.3/cad-agent-foundation-v1.2.0-beta.3-win-x64.zip) or [matching MCPB](https://github.com/capskov/cad-agent-foundation/releases/download/v1.2.0-beta.3/cad-agent-foundation-v1.2.0-beta.3-win-x64.mcpb), and verify SHA256SUMS.txt from that release. Upgrade into a fresh owned workspace with the matching plugin; close or preserve previous sessions through their original versioned entry. The source server.json describes the current GitHub bundle; the official MCP Registry also publishes version 1.2.0-beta.3. Earlier 1.1.0-rc.1 evidence below is retained as history.

中文：新版GitHub预览支持原坐标操作及可选单位核对，运行ZIP与MCPB包含相同运行代码。升级使用新工作目录；此次已发布Registry版本1.2.0-beta.3，不修改用户全局MCP。

## Official MCP Registry — current record and earlier evidence

Published name: `io.github.capskov/cad-agent-foundation`, current version `1.2.0-beta.3`. The 2026-10-23 record passed official publisher validation and publication. The earlier `1.1.0-rc.1` packaging/native connection evidence below describes that historical version, not a new beta.3 client test. The repository's [server.json](../server.json) describes a local stdio MCPB distributed through GitHub Releases. Public metadata can be queried through the [official Registry API](https://registry.modelcontextprotocol.io/v0.1/servers?search=io.github.capskov%2Fcad-agent-foundation). Registry metadata does not install software or grant permission to operate a drawing.

Download [the Windows x64 MCPB](https://github.com/capskov/cad-agent-foundation/releases/download/v1.1.0-rc.1/cad-agent-foundation-v1.1.0-rc.1-win-x64.mcpb) and [its checksum](https://github.com/capskov/cad-agent-foundation/releases/download/v1.1.0-rc.1/cad-agent-foundation-v1.1.0-rc.1-win-x64.mcpb.sha256). Clients implementing MCPB can read its manifest and request a Python executable and existing owned workspace. Generic stdio clients can use the existing generated `mcp-config.json` instead. No specific desktop-client one-click compatibility is claimed without testing that client.

Prerequisites remain separately installed licensed compatible AutoCAD 2026/.NET 8 on Windows x64, .NET 8 Desktop Runtime and Python 3.11+. First install the runtime and explicitly start an owned drawing copy as described in [English](QUICKSTART.md) / [中文](QUICKSTART.zh-CN.md). Then configure the same workspace in the extension. Loading the MCPB does not start AutoCAD or enable writes. Default startup is read-only; write mode requires explicit `--write`. Keep private workspace credentials and drawings outside the package. No remote endpoint or public CAD access is introduced.

## Distribution and verification

The MCPB is an outer standard manifest and installation instructions around the exact accepted runtime under `runtime/`. Its inner `manifest.json` remains the Foundation runtime hash/MVID manifest; its outer `manifest.json` is the MCPB specification. Do not confuse the two. Original ZIP, original checksum file, original release body and existing assets remain unchanged.

The official MCPB CLI 2.1.2 validated and unpacked the bundle; the official MCP publisher 1.8.1 validated the Registry record. A separate owned synthetic AutoCAD session used the command and variables resolved directly from the MCPB manifest: eight MCP tools, 25 plan operations, 17 native entity reads, layer listing, one new native aligned dimension, original-request completion, save and normal stop passed. Runtime code and all 212 original package files were byte-identical. This validates the resolved local stdio configuration, not every graphical client's installer.

To reproduce: use [package-mcpb.py](../scripts/package-mcpb.py) with the accepted runtime ZIP; validate/unpack with the official MCPB CLI; run [verify-native-mcp.py](../scripts/verify-native-mcp.py) with `--package` pointing to extracted `runtime/` and `--mcpb-manifest` pointing to the outer manifest. Maintainer fixtures must be synthetic owned drawings, never formal engineering inputs.

## Directory submissions — 2026-10-15

MCPServers confirmed submission of CAD Agent Foundation to its free review queue. This is pending review, not a live listing or an endorsement. The site states review can take up to two weeks.

SkillsLLM confirmed submission of the two independently maintained callout and distress Skills for review. It states a 100-star requirement on its form; submission acceptance does not guarantee final inclusion. Foundation is submitted independently as an MCP server as well, rather than being represented by the Skills.

The `server.json` description and MCPB packaging metadata are maintained together. Updating these source files does not automatically change the already-published Registry listing or an existing versioned MCPB asset; publication metadata requires a separate publishing step.

Official registration, directory review and GitHub/web search ranking are distinct. Accurate topics include `autocad`, `autocad-mcp`, `mcp-server`, `cad-automation`, `model-context-protocol`, `ai-agents` and `dwg`. The native .NET backend is not advertised as COM automation. No ranking guarantee, remote CAD service, or broad AutoCAD-version support is implied.

中文：底座已作为MCP服务器发布官方Registry元数据，并提供MCPB安装入口；两个Skill独立维护。目录“提交成功”仅指进入审核，不写成已收录。先安装运行环境、安装工作目录并启动自有图纸副本，再连接MCP；注册不会赋予智能体操作权限，也不会把本机CAD开放到公网。MCPB配置已按实际解析命令做原生连接／读写验证，尚未声称所有图形客户端的一键安装均通过。


Optional image-interpretation Skill: [Agentic Map Tracing](https://github.com/capskov/agentic-map-tracing). Its SKILL.md discovery and install process is separate from MCP server registration.
