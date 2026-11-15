# Choose and install components

[中文](COMPONENTS-AND-INSTALLATION.zh-CN.md) · [Foundation quick start](QUICKSTART.md) · [Compatibility](COMPATIBILITY.md)

Choose components by task. The two Skills are independent, optional companions, not global Foundation dependencies. Installing a Skill supplies instructions and resources, not missing CAD operations. No automatic Skill download or dependency installer is implemented.

| Task | Required components | Versions and limits |
| --- | --- | --- |
| Supported basic CAD operations | `cad-agent-foundation` only | Local baseline `v1.0.0-local`; public source preview `v1.1.0-rc.1`, with a manifest-bearing runtime ZIP. Bounded 2D operations; local.4 raw writes use original drawing values; unit metadata checks are optional; recorded Windows/AutoCAD 2026 host only. Neither Skill is required. |
| This project's callout style and placement | Foundation + `cad-callout-pair-layout` | Public callout Skill v0.3.0-rc.1 (existing rules v0.3). Metre-based road defaults; Native style/pose interfaces and a bounded synthetic unchanged-Skill integration are verified in v1.1; full real-project/background coverage remains per task. Check backend coverage before writing. |
| This project's distress/demolition workflow | Foundation + `cad-distress-annotation` + `cad-callout-pair-layout` | Public distress rules preview v0.1.0-rc.1. Requires engineering evidence, classifications and the callout rules; full workflow needs capabilities outside the current Foundation profile and is not certified on these three components alone. |

## Responsibilities and actual operation coverage

Foundation executes supported native operations with drawing identity, request ownership, bounded writes and readback. It currently supports creating/reusing a matching layer, new line/circle/2D polyline, DBText/MText, associative hatch on a boundary created earlier in the same plan, and an ordinary LEADER/MText/yellow filled-dot primitive. It also supports guarded text updates, moves and archival of supported entities or complete associated groups, inspection and whole-document save with specified-object disk readback. These are bounded operations, not unrestricted layer/entity editing. See [capabilities and limits](CAPABILITIES.md).

The callout Skill supplies text content conventions, dot/leader style, confirmed side and opening constraints, and finite placement/avoidance rules. Foundation's callout primitive does not supply that complete workflow. The distress Skill supplies evidence-based professional scope, classification and engineering rules; it uses the callout Skill for presentation. Foundation does not choose a treatment or infer quantities from a drawing without domain evidence.

Foundation is not restricted to roads or civil engineering: other professions may use these basic primitives within the same execution limits. Actual validation uses metre-based civil drawings. WCS is a coordinate reference, not a unit or projection. Local.4 recommends `units: raw_drawing_units` with optional `expected_insunits`; no conversion or INSUNITS change. Legacy `metres` remains compatible. The Skills' other-domain uses, including planting/species labels, also require unvalidated size/style/rule adaptations.

Four native dimension primitives and bounded closed-boundary/single-hatch updates are supplied. MLeader, general old-hatch repair, arbitrary polyline properties, Boolean/complex-hole engineering workflows, layout/plotting and external-reference editing remain outside the profile. Neither adding Skills nor installing another MCP server automatically extends Foundation's governed coverage.

## Repository and installation entries

Foundation source and both reviewed Skill previews are published below. Skill releases have manual-install ZIPs; Foundation now provides its own manifest-bearing runtime ZIP.

| Component | Repository / public preview | Installation entry |
| --- | --- | --- |
| Foundation | [cad-agent-foundation (source preview)](https://github.com/capskov/cad-agent-foundation) | [Foundation installation and CLI](QUICKSTART.md); [Runtime ZIP](https://github.com/capskov/cad-agent-foundation/releases/tag/v1.1.0-rc.1). |
| Callout Skill | [cad-callout-pair-layout v0.3.0-rc.1](https://github.com/capskov/cad-callout-pair-layout) | [Install](https://github.com/capskov/cad-callout-pair-layout/blob/main/docs/INSTALL.md) · [Release ZIP](https://github.com/capskov/cad-callout-pair-layout/releases/tag/v0.3.0-rc.1). |
| Distress Skill | [cad-distress-annotation v0.1.0-rc.1](https://github.com/capskov/cad-distress-annotation) | [Install](https://github.com/capskov/cad-distress-annotation/blob/main/docs/INSTALL.md) · [Release ZIP](https://github.com/capskov/cad-distress-annotation/releases/tag/v0.1.0-rc.1). Add callout rules for presentation. |

Use your Agent client's supported manual Skill installation procedure for the published Skill ZIPs. Keep the two folders as siblings with their exact names, each containing `SKILL.md` and its required resources, because the distress rules reference the callout rules. Client-specific discovery is not supplied by `foundation.cmd install`; that command installs Foundation only. Do not invent a `--skills` option.

## Before an Agent executes a task

1. Select the row above; check the actual installed component versions/resources, not merely repository names.
2. Discover the actual CAD tools and schemas. Check host, drawing units, ownership and each required operation, including style and layout semantics. No blanket `Foundation >= 1.0` full-workflow compatibility is claimed.
3. If a component is missing, report its name and the installation entry above; if publication is still pending, say so. If a tool capability is missing, report that gap separately. Continue only independent work that does not depend on it.
4. Bind the intended working copy and follow its write/readback contract. External tools may be used only through a verified, authorized route; they do not share Foundation transactions, request IDs or fences. Do not automatically switch to COM, arbitrary code or another provider after refusal or an uncertain write. Query the original request.

Reusable native primitives and execution constraints belong in Foundation. Professional decisions, classifications, style and placement rules belong in Skills. Future capability work must preserve that boundary; this documentation update adds no installer or CAD operation.

The Foundation-based rows describe one selected execution route, not a mandatory backend for independent scope review, offline geometry or BZYD. / 上表的底座组合描述选用底座的执行路线；范围分析、离线几何与BZYD不强制依赖底座。


## Optional satellite/aerial road tracing / 可选影像道路描绘

[Agentic Map Tracing](https://github.com/capskov/agentic-map-tracing), v0.1.0-beta.1, supplies the `satellite-road-edge-tracing` Agent Skill. Recommended native backend: Foundation v1.2.0-beta.3. Agent interprets markings, road composition, occlusion and human guidance; Foundation handles bounded native operations and feedback. Optional for this task, not a prerequisite for basic CAD or other Skills. Installing a Skill does not install Foundation or provide missing native operations.
