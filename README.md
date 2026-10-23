# CAD Agent Foundation — AutoCAD MCP & CLI for AI Agents

**Public runtime preview v1.2.0-beta.3.** Proxy/compound geometry and text reads, bounded native views/captures, raster-path repair, rejection classification and polyline-width fixes. Reuses the accepted beta.3 binaries; [scope, known capture limitations and upgrade notes](docs/RELEASE-v1.2.0-beta.3.md).

**Optional road interpretation:** [Agentic Map Tracing](https://github.com/capskov/agentic-map-tracing) v0.1.0-beta.1. The Agent reasons about boundaries; Foundation supplies CAD execution. Install separately.

## First priority: reliable operations for increasingly capable agents

Foundation development supports the real task. Provide small, reliable, controlled CAD primitives and truthful feedback so the Agent can act and move forward. The Agent owns visual judgment, structure interpretation, example learning, pattern recognition and final review. Do not turn those judgments into programs, fitting algorithms or compulsory verification workflows. Do not add incidental features unless explicitly requested. When current primitives suffice and the Agent judges the result acceptable, continue the main task. Preserve drawing identity, source protection and no replay of unknown writes.

Local unit closeout: `1.2.0-local.4` carries prior bounded primitives, makes INSUNITS consistency optional for raw-coordinate plans and retains the legacy metres interface. This prerelease is published as `v1.2.0-local.4`; earlier releases remain available. See [units, precision and paper typography](docs/DRAWING-UNITS-AND-TYPOGRAPHY.zh-CN.md); this is not blanket A–P certification.

**Agent-first AutoCAD MCP — built for the execution loop, not just command calls.** CAD Agent Foundation helps AI agents bind the right drawing, track each request, receive execution feedback, verify what was written or saved, and continue multi-step CAD work with confidence. Independent or third-party CAD Skills can reuse this execution foundation for their own professional workflows.

**中文简介：面向 AI Agent 的 CAD 执行底座——不止调用命令，还关注图纸绑定、执行反馈、结果核验与安全续作，可与第三方专业 Skill 组合使用。**

While CAD integrations connect AI to drawing commands and data, Foundation focuses on the **agent workflow around each operation**: knowing what was requested, what actually happened, and whether the next step is safe.


**Public runtime preview v1.2.0-beta.3.** [Release and upgrade notes](docs/RELEASE-v1.2.0-beta.3.md). Source, documentation and a manifest-bearing Windows runtime ZIP are provided in this preview. [Build prerequisites and limits](docs/BUILD.md). The accepted local runtime v1.0.0-local is a separate baseline. Companion Skill previews are published independently: callout v0.3.0-rc.1 and distress v0.1.0-rc.1.

[简体中文](README.zh-CN.md) · [Quick start](docs/QUICKSTART.md) · [Capabilities](docs/CAPABILITIES.md) · [Compatibility](docs/COMPATIBILITY.md)

Registered MCP server: `io.github.capskov/cad-agent-foundation`. [MCPB installation and discovery](docs/MCP-DISCOVERY-AND-INSTALLATION.md) · [Registry metadata](server.json). Local Windows/AutoCAD prerequisites still apply; registration does not grant drawing access.

**AutoCAD automation bridge for AI agents via MCP and CLI, using a native .NET API.**

AI agents can inspect and operate a live AutoCAD drawing through structured native tools for bounded 2D geometry, text, hatches and callouts. The bridge uses MCP (Model Context Protocol) and CLI; drawing identity checks and write/save verification govern the supported operations.

CAD Agent Foundation gives CAD agents a bounded execution contract: explicitly bind a drawing, persist request ownership and execution state, inspect the original request after an uncertain reply, and verify tracked object facts after writes and saves. The current implementation integrates these mechanisms into a modified, pinned [Bimwright DWG MCP](https://github.com/bimwright/dwg-mcp) host.

This Windows/AutoCAD bridge is maintained by **capskov**. The current preview is **v1.2.0-beta.3**, with an independently built runtime ZIP and scoped native evidence. The accepted **v1.0.0-local** baseline is retained; compatibility with every AutoCAD update is not claimed.

## Why this project exists

The project grew out of road and civil-engineering CAD work: creating boundaries, fills, quantities and linked callouts, then revising and saving them over a long agent session. A useful drawing command is only part of that workflow. The agent also needs to know which drawing it owns, whether a modification has already happened, what remains unverified, and whether another attempt would duplicate it.

The design focuses on those decisions. Durable records, drawing identity and native readback add overhead; no comparative speed, token-cost or productivity advantage has been established. Engineering judgment and domain-specific layout rules remain in separate Skills.

## Native operations and installation

Download [v1.2.0-beta.3 runtime](https://github.com/capskov/cad-agent-foundation/releases/tag/v1.2.0-beta.3), verify SHA256SUMS.txt and extract the complete folder. Requires licensed compatible Windows x64 AutoCAD 2026, .NET 8 Desktop Runtime and Python 3.11+. No Autodesk assemblies or SHX fonts are redistributed. [Installation](docs/QUICKSTART.md) · [25-operation catalog](docs/COMMAND-CATALOG-v1.1.0-rc.1.md) · [New validation](docs/VALIDATION-v1.1.0-rc.1.md).

New in this preview: installed-font text styles, dimension styles, native linear/aligned/radius/diameter DIMENSION, layer state management, guarded closed-boundary/hatch continuation, linked callout geometry/format, and same-drawing rotate/scale/copy of supported complete groups. It preserves original-request reconciliation and drawing/object protection.

## Current capability overview

Maturity: the bounded local runtime has native acceptance evidence; The release provides a runtime ZIP with dependency licenses, file hashes and plugin MVID; prerequisites remain separately installed. Support follows the actually exposed profile.

| Category | Current capabilities | Status / limits |
| --- | --- | --- |
| Basic drawing | New layer, line, circle, 2D polyline and associative hatch on an earlier new boundary | Specified local workflows verified; not arbitrary editing/all hatch cases. |
| Text and callouts | Single-line DBText, multiline MText, ordinary LEADER/MText/yellow filled-dot group | Creation verified; complete domain styles/placement belong to Skill. |
| Edits / object management | Digest-guarded linked-text update and complete callout-group move/archive | Specified group workflow verified; not every entity branch individually tested. |
| Execution / delivery | Drawing binding, original-request queries, duplication guards and whole-document save/disk readback | Local workflows verified; no automatic unknown-write replay/unlock. |
| Reads / helpers | Entity properties/digests, layer list, filtered paging and static Lisp inspection | Entity readback verified; some other implemented routes lack current-version individual end-to-end proof. |
| Additional bounded operations | Four native DIMENSION types, named matching styles, layer states, closed-boundary/callout updates, rotate/scale/copy | See the 25-operation catalog for exact guards and scoped native evidence. |
| Not exposed | MLEADER, shared-style overwrite, arbitrary object editing, complex Boolean/holes, layout/plot/xref edits | No blanket coverage of every native object or upstream command. |

[Full interfaces, invocation and per-item verification status](docs/COMMAND-CATALOG-v1.1.0-rc.1.md). Foundation supplies reusable native operations, execution constraints, status and object management. Independently maintained Skills supply domain rules, style parameters, placement algorithms and workflows through actual supported Foundation interfaces. Missing native/backend compatibility is not supplied merely by installing a Skill.

## Execution contract

Bind the intended drawing and verify process/session/plugin identity. Persist a stable request before submission; after an uncertain reply, query that original request. Existing edits need fresh native digests and complete associations. Confirm effects, independent readback and fence release separately; save covers the whole working drawing. See [architecture](docs/ARCHITECTURE.md) and [versioned interface catalog](docs/COMMAND-CATALOG-v1.1.0-rc.1.md).

## Choose components by task

| Task | Required components | Versions and limits |
| --- | --- | --- |
| Supported basic CAD operations | `cad-agent-foundation` only | Local baseline `v1.0.0-local`; public source preview `v1.1.0-rc.1`, with a reviewed runtime ZIP; prerequisites are separate. Bounded 2D operations; local.4 raw writes use original drawing values; unit metadata checks are optional; recorded Windows/AutoCAD 2026 host only. Neither Skill is required. |
| This project's callout style and placement | Foundation + `cad-callout-pair-layout` | Public callout Skill v0.3.0-rc.1 (existing rules v0.3). Metre-based road defaults; Native style/pose interfaces and a bounded synthetic unchanged-Skill integration are verified in v1.1; full real-project/background coverage remains per task. Check backend coverage before writing. |
| This project's distress/demolition workflow | Foundation + `cad-distress-annotation` + `cad-callout-pair-layout` | Public distress rules preview v0.1.0-rc.1. Requires engineering evidence, classifications and the callout rules; full workflow needs capabilities outside the current Foundation profile and is not certified on these three components alone. |

The two Skills are optional for basic operations. Their rules are not built into Foundation; installing them does not add missing native operations. Foundation source and both independent Skill previews are public. [Installation entries, versions and Agent checks](docs/COMPONENTS-AND-INSTALLATION.md).

## Quick start

The commands below use the extracted runtime ZIP. Source checkout alone requires build/packaging first.

```bat
foundation.cmd install --workspace "C:\CAD-work\demo01"
foundation.cmd start --workspace "C:\CAD-work\demo01" --source "C:\drawings\input.dwg" --write
foundation.cmd doctor --workspace "C:\CAD-work\demo01"
foundation.cmd apply --workspace "C:\CAD-work\demo01" --plan "plan.json"
foundation.cmd save --workspace "C:\CAD-work\demo01" --handles RETURNED_HANDLES
foundation.cmd stop --workspace "C:\CAD-work\demo01"
```

Omit `--write` for a read-only session. `start` creates a working copy; it does not overwrite the source drawing. Save affects the **entire working drawing**, including other unsaved edits in that instance. Read the [quick start](docs/QUICKSTART.md) for request handling and a small synthetic plan.

## Verification and scope

The local release was checked on two different drawing sources, with different coordinates and handles. Each workflow created ten new objects, read them independently, updated linked text, moved the complete callout group, archived it, saved and closed the drawing. Fresh CAD sessions then read the saved objects with matching native digests. The CLI/MCP entry and local package extraction/installation were also checked.

These are bounded workflow results on a recorded Windows AutoCAD 2026 environment. They do not prove universal drawing correctness, production-wide reliability, compatibility with every patch, or a general CAD engineering agent. The [validation summary](docs/VALIDATION.md) separates native evidence from offline tests and records unresolved historical outcomes honestly.

## Current limits

Plans write bounded 2D model-space geometry. Coordinates, lengths and annotation sizes are expressed in drawing units. WCS is the CAD coordinate reference; it does not imply metres or a map projection. Foundation performs no projection or unit conversion.

**Local 1.2.0-local.4 uses raw drawing values:** prefer `units: raw_drawing_units`, with optional `expected_insunits` only for requested metadata consistency. Unknown or unitless metadata does not block ordinary raw operations. Legacy `units: metres` keeps its original 0/6 metadata contract. No coordinate conversion, automatic geometry scaling or INSUNITS changes occur. See [units, precision and paper typography](docs/DRAWING-UNITS-AND-TYPOGRAPHY.zh-CN.md); PDF/DXF output retains its separate current limits.

This release provides four native dimension primitives and bounded closed-boundary/single-hatch updates. It does not provide MLeader, general old-hatch repair/Boolean/holes, arbitrary polyline properties, cross-drawing copy, proxy-object semantics, layout/plotting, engineering decisions or automatic recovery/unlocking of uncertain writes. A committed operation cannot be assumed rolled back because a later verification failed.

The `.NET 8` local baseline was inspected on AutoCAD **R25.1.74.0.0**. AutoCAD 2026.1.2 introduced a `.NET 10` transition; compatibility with that updated host is **not yet validated here**. See [compatibility](docs/COMPATIBILITY.md). [Autodesk runtime update](https://blog.autodesk.io/autodesk-desktop-products-2025-2026-net-10-updates/)

## Separate Skills

See the [anonymous road demolition case](examples/road-demolition/README.md) for existing before/after drawings and quantity/station references. It illustrates companion-Skill output and does not certify a Foundation-only execution of that full workflow.

Independent Skill repositories and installation:

- [cad-callout-pair-layout](https://github.com/capskov/cad-callout-pair-layout): callout style, road-side constraints and finite placement budgets; includes optional bilingual BZYD manual Lisp. [Install](https://github.com/capskov/cad-callout-pair-layout/blob/main/docs/INSTALL.md) · [v0.3.0-rc.1](https://github.com/capskov/cad-callout-pair-layout/releases/tag/v0.3.0-rc.1).
- [cad-distress-annotation](https://github.com/capskov/cad-distress-annotation): evidence-based road distress/demolition boundaries and quantity rules. [Install](https://github.com/capskov/cad-distress-annotation/blob/main/docs/INSTALL.md) · [v0.1.0-rc.1](https://github.com/capskov/cad-distress-annotation/releases/tag/v0.1.0-rc.1).

Foundation supplies supported CAD primitives and their execution contract. Skills decide what to draw, how to lay it out, and which domain checks to apply. Foundation does not determine plant species, distress treatment or engineering quantities.

Callout techniques may also serve landscape planting and other classified drawing annotations. The existing Skill's tested rules and defaults come from metre-based road drawings; other disciplines or millimetre drawings need style, size and placement adaptation and are not certified here.

Both repositories provide versioned public Skill ZIPs and manual installation guides. Foundation v1.1 adds the native style/pose/measurement interfaces; a synthetic unchanged-Skill solver integration is recorded in the new validation, not a blanket real-project certification. A complete Foundation-only Skill workflow has not been certified. Callout v0.3.0-rc.1 and distress v0.1.0-rc.1 remain independently versioned. See [compatibility](docs/COMPATIBILITY.md).

## Working alongside other tools

Agents can use other MCP servers or CAD tools separately. Only supported operations submitted through Foundation enter its governed request, execution and verification flow. An unrelated backend does not automatically share its transactions, request identities or fences. Switching tools is not a way to bypass an unresolved write. Full coverage of an external backend would require an explicit integration; it is not provided by this baseline. See [Foundation, Skills and other tools](docs/FOUNDATION-SKILLS-AND-OTHER-TOOLS.md).

## Upstream and licensing

AutoCAD 2026 is the initial validated host because it matches the working environment and the adopted native plugin route. The local .NET 8 baseline uses document locking, transactions and native readback; 2025 also has a .NET 8 host, and older years have automation APIs. MCP comes from the bridge. See [why 2026](docs/WHY-AUTOCAD-2026.md).

The integrated host derives from Bimwright DWG MCP, pinned at [`4d0f756`](https://github.com/bimwright/dwg-mcp/commit/4d0f75646631186612af926944d7e33affc4757d). Preserve its Apache-2.0 licensing and attribution. Other reviewed projects are credited separately from code actually reused; see [background and related projects](docs/BACKGROUND-AND-RELATED-PROJECTS.md).

The development path was practical: examine existing CAD MCP backends against real tasks, integrate a bounded execution contract into the selected native host, then check writes, native readback and saved drawings. Historical evaluations are documented with their scope rather than presented as a ranking of today's projects.

This preview uses Apache-2.0, preserving upstream and MIT-origin notices. Earlier `v1.0.0-rc.1`/`rc.2` source/documentation releases are retained; `v1.1.0-rc.1` adds a reviewed runtime ZIP with dependency license texts. AutoCAD/SDK and unlicensed SHX assets are not redistributed; Open Sans retains OFL. See [license decision](LICENSING.md) and [notices](THIRD_PARTY_NOTICES.md).

Human and AI-assisted contributions are welcome when changes and verification evidence are reviewable. The maintainer reviews merges and releases. See [contributing](CONTRIBUTING.md).

AutoCAD and DWG are Autodesk trademarks. This independent project is not endorsed by Autodesk or the upstream maintainers.


## 2026-10-16 local font update

Local 1.2.0-local.3 adds guarded existing-style SHX font-pair replacement and anchor-preserving DBText metric scaling. See [scope and parameters](docs/FONT-REPLACEMENT-LOCAL3.zh-CN.md). Native evidence is scoped; prior public releases and global MCP registration are unchanged.

Local 1.2.0-local.4 unit closeout: [changes, native evidence and limits](docs/UNIT-CLOSEOUT-LOCAL4.zh-CN.md). The original local closeout has now been authorized for GitHub prerelease publication; see the current release notes.
