# Why AutoCAD 2026 is the initial target

## 中文解释

首个验收平台选择 AutoCAD 2026，是因为它匹配实际工作环境及采用的 Bimwright 原生插件路线。当前本地基线使用 .NET 8、AutoCAD 托管 API、文档锁和事务，集中围绕一个已运行的宿主建立安装、执行及核验证据，再按实际需求适配其他版本。

2026 的理由可以归纳为“现有实战环境、匹配的现代插件路线、明确的首版验收范围”。较早版本也有外部自动化接口，2025 也使用 .NET 8；MCP 由桥接服务提供，不能写成只有 2026 才能接入 AI 或 MCP。目前没有依据宣称 2026 的外部接口独占地更可靠。

这解释当前技术目标，不重构没有记录的历史原话。选择现代插件路线便于后续适配属于工程判断，不是已测得的跨版本优势。

## English explanation

AutoCAD 2026 is the initial validated host because it matches the working environment and the native plugin route adopted from Bimwright DWG MCP. The local .NET 8 baseline uses managed document/database access, locking and transactions. Concentrating on one installed host keeps installation and execution evidence manageable.

MCP comes from the bridge server. Earlier AutoCAD releases already had automation APIs, and 2025 also supports .NET 8. Version-specific upstream shells do not establish Foundation acceptance on those years. Updated AutoCAD 2026/.NET 10 compatibility remains unvalidated here; future adaptation follows actual host compatibility and user needs.

## 需要避免的说法

- “只有2026支持MCP”“买2026就自动能接Codex”：协议/桥接服务、产品官方MCP和宿主年份是不同事情。
- “选择2026因为原生官方MCP替我们写图”：当前运行的是修改后的Bimwright宿主，官方MCP不在执行链中。
- “2026永远是.NET8，所有补丁都兼容”：官方已有2026.1.2/.NET10迁移，当前基线尚未验证。
- “.NET8只属于2026，2025不能用原生API”：不能把本项目首个验收目标写成API的排他条件。

Autodesk's [official AutoCAD/Civil 3D MCP page](https://help.autodesk.com/view/ADSKMCP/ENU/?guid=ADSKMCP_AutoCADCivil3DMcp_autodesk_autocad_civil_3d_mcp_html) describes a separate Assistant integration. Its [Product Help MCP](https://help.autodesk.com/cloudhelp/ENU/ADSKMCP-ProductHelpMcp/files/ADSKMCP_ProductHelpMcp_autodesk_product_help_mcp_server_html.html) retrieves documentation. [Runtime updates](https://blog.autodesk.io/autodesk-desktop-products-2025-2026-net-10-updates/) must be considered independently.

Autodesk's [managed compatibility table](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm) lists .NET Framework 4.8 for 2024 and earlier, .NET 8 for 2025 and early 2026, and .NET 10 for 2026 Update 1.2 onward. [Bimwright](https://github.com/bimwright/dwg-mcp) documents version-specific shells; live upstream content may change independently of the pinned local integration.
