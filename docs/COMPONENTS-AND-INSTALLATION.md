# Choose and install components

[中文](COMPONENTS-AND-INSTALLATION.zh-CN.md) · [Foundation quick start](QUICKSTART.md) · [Compatibility](COMPATIBILITY.md)

Choose components by task. The two Skills are independent, optional companions, not global Foundation dependencies. Installing a Skill supplies instructions and resources, not missing CAD operations. No automatic Skill download or dependency installer is implemented.

| Task | Required components | Versions and limits |
| --- | --- | --- |
| Supported basic CAD operations | `cad-agent-foundation` only | Local baseline `v1.0.0-local`; public source preview `v1.0.0-rc.1`, without a runnable binary asset. Bounded 2D operations; current writes require metres; recorded Windows/AutoCAD 2026 host only. Neither Skill is required. |
| This project's callout style and placement | Foundation + `cad-callout-pair-layout` | Callout rules v0.3, public export pending. Metre-based road defaults; complete styles and layout have not been certified on Foundation v1 alone. Check backend coverage before writing. |
| This project's distress/demolition workflow | Foundation + `cad-distress-annotation` + `cad-callout-pair-layout` | Distress rules snapshot has no independent public release version. Requires engineering evidence, classifications and the callout rules; full workflow needs capabilities outside the current Foundation profile and is not certified on these three components alone. |

## Responsibilities and actual operation coverage

Foundation executes supported native operations with drawing identity, request ownership, bounded writes and readback. It currently supports creating/reusing a matching layer, new line/circle/2D polyline, DBText/MText, associative hatch on a boundary created earlier in the same plan, and an ordinary LEADER/MText/yellow filled-dot primitive. It also supports guarded text updates, moves and archival of supported entities or complete associated groups, inspection and whole-document save with specified-object disk readback. These are bounded operations, not unrestricted layer/entity editing. See [capabilities and limits](CAPABILITIES.md).

The callout Skill supplies text content conventions, dot/leader style, confirmed side and opening constraints, and finite placement/avoidance rules. Foundation's callout primitive does not supply that complete workflow. The distress Skill supplies evidence-based professional scope, classification and engineering rules; it uses the callout Skill for presentation. Foundation does not choose a treatment or infer quantities from a drawing without domain evidence.

Foundation is not restricted to roads or civil engineering: other professions may use these basic primitives within the same execution limits. Actual validation uses metre-based civil drawings. WCS is a coordinate reference, not a unit or projection. The present plan parser requires `units: metres`; millimetres and other units are not enabled or validated. The Skills' other-domain uses, including planting/species labels, also require unvalidated size/style/rule adaptations.

Native DIMENSION, MLeader, general old-hatch repair, arbitrary existing-polyline reshaping, Boolean/complex-hole engineering workflows, layout/plotting and external-reference editing are not supplied by the current profile. Neither adding Skills nor installing another MCP server automatically extends Foundation's governed coverage.

## Repository and installation entries

Foundation source is published at the link below; **both Skill links remain planned placeholders**. No runnable Foundation binary or reviewed Skill download is available; source is not an installed package.

| Component | Repository / planned Skill | Installation entry |
| --- | --- | --- |
| Foundation | [cad-agent-foundation (source preview)](https://github.com/capskov/cad-agent-foundation) | [Foundation installation and CLI](QUICKSTART.md); approved public package link pending. |
| Callout Skill | [cad-callout-pair-layout (planned)](https://github.com/capskov/cad-callout-pair-layout) | Planned repository `docs/INSTALL.md`; reviewed Skill archive/source link pending. |
| Distress Skill | [cad-distress-annotation (planned)](https://github.com/capskov/cad-distress-annotation) | Planned repository `docs/INSTALL.md`; reviewed Skill archive/source link pending. Install alongside the callout Skill. |

Once reviewed Skill exports are available, use your Agent client's supported manual Skill installation procedure. Keep the two folders as siblings with their exact names, each containing `SKILL.md` and its required resources, because the distress rules reference the callout rules. Client-specific discovery is not supplied by `foundation.cmd install`; that command installs Foundation only. Do not invent a `--skills` option.

## Before an Agent executes a task

1. Select the row above; check the actual installed component versions/resources, not merely repository names.
2. Discover the actual CAD tools and schemas. Check host, drawing units, ownership and each required operation, including style and layout semantics. No blanket `Foundation >= 1.0` full-workflow compatibility is claimed.
3. If a component is missing, report its name and the installation entry above; if publication is still pending, say so. If a tool capability is missing, report that gap separately. Continue only independent work that does not depend on it.
4. Bind the intended working copy and follow its write/readback contract. External tools may be used only through a verified, authorized route; they do not share Foundation transactions, request IDs or fences. Do not automatically switch to COM, arbitrary code or another provider after refusal or an uncertain write. Query the original request.

Reusable native primitives and execution constraints belong in Foundation. Professional decisions, classifications, style and placement rules belong in Skills. Future capability work must preserve that boundary; this documentation update adds no installer or CAD operation.
