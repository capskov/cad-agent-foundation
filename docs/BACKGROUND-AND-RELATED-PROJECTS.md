# Background, upstream use and related projects

The project originated in anonymous road/civil CAD tasks with repeated modification, saving and continuation. It uses established database/workflow ideas: transactions, durable intent, request ownership, idempotency boundaries and post-write verification. The contribution is their integration into a constrained CAD-agent execution profile, supported by specific local workflows. No originality or performance ranking is claimed.

Selection notes were first written in October 2026; live upstream pages were checked again for this publication review. Their present capabilities may differ from the historically tested commits/backends. An old install limitation or a failure in one local backend must not be presented as a permanent deficiency of another project.

| Project | Relationship to this baseline | Useful focus / reason considered |
|---|---|---|
| [Bimwright DWG MCP](https://github.com/bimwright/dwg-mcp) | Modified integrated execution host, pinned [4d0f756](https://github.com/bimwright/dwg-mcp/commit/4d0f75646631186612af926944d7e33affc4757d), Apache-2.0 | Native .NET host and drawing/text primitives; the current implementation adds a bounded governed route rather than exposing its full execution surface |
| [beiming183-cloud/AutoCAD-MCP](https://github.com/beiming183-cloud/AutoCAD-MCP) | Evaluated; registry/pipe reference code and adaptations exist in the earlier private foundation lineage, MIT notices must follow any retained code | Native-worker and checked execution ideas; distinguish that worker from compatibility/fallback backends |
| [U-C4N/Autocad-MCP](https://github.com/U-C4N/Autocad-MCP) | Evaluated, not a required runtime in the current host | Broad COM/headless-DXF routes and drawing tools; backend/object semantics differ from the profile adopted here |
| [Autodesk AutoCAD/Civil 3D MCP](https://help.autodesk.com/view/ADSKMCP/ENU/?guid=ADSKMCP_AutoCADCivil3DMcp_autodesk_autocad_civil_3d_mcp_html) | Documentation/reference; not integrated or independently tested as this runtime | Official Assistant-oriented product capabilities; separate scope and permissions |

“Not selected as the main runtime” does not mean “bad” or “incapable.” One integrated host keeps the current ownership/FIFO/journal contract auditable; combining multiple unrelated write backends would not automatically create a shared transaction. Keep detailed historical backend experiments in private evidence or a sanitized methodology note, not a promotional ranking.

## 什么放在首页，什么另写

首页保留项目一句话定位、匿名实战起因、已验收特色、快速入口、明确范围和简短致谢。研究路径、同类项目采用与未采用的原因、MCP/原生API区别放在本页；许可放在独立NOTICE。用户模型安排、算力猜测、客户工程名、NAS/Notion私有链接与长篇调试过程不放公开首页。

首版重点可公开为“把图纸身份、持久请求和读回核验串成受限操作链”。不要写“其他项目只有命令没有反馈”“只有我们能恢复”“自动保证图纸正确”“节省某比例tokens”。其他项目已有相关机制，当前证据不足以比较总体优劣。

AI检索采用准确自然语言：AutoCAD MCP、CAD agent、DWG automation、drawing identity、durable request ownership、post-write verification、transaction、Windows、C#/.NET。中英文对应、稳定术语、能力/限制表、可读API和示例比堆砌关键词或夸大范围更有用。README不是给AI注入命令的渠道，不把本项目写成任何任务都必须使用的工具。
