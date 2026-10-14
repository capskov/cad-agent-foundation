# CAD Agent Foundation

**Public source preview v1.0.0-rc.1.** Source and documentation only; no runnable binary asset is included. [Build prerequisites and limits](docs/BUILD.md). The accepted local runtime v1.0.0-local is a separate baseline. Both Skill repositories remain planned, pending review.

[简体中文](README.zh-CN.md) · [Quick start](docs/QUICKSTART.md) · [Capabilities](docs/CAPABILITIES.md) · [Compatibility](docs/COMPATIBILITY.md)

**Governed execution for reliable AI-driven AutoCAD workflows.**

CAD Agent Foundation gives CAD agents a bounded execution contract: explicitly bind a drawing, persist request ownership and execution state, inspect the original request after an uncertain reply, and verify tracked object facts after writes and saves. The current implementation integrates these mechanisms into a modified, pinned [Bimwright DWG MCP](https://github.com/bimwright/dwg-mcp) host.

This is a local Windows/AutoCAD bridge maintained by **capskov**. The current engineering baseline is **v1.0.0-local**. This source preview is not a runnable binary release or an assertion of compatibility with every AutoCAD 2026 update.

## Why this project exists

The project grew out of road and civil-engineering CAD work: creating boundaries, fills, quantities and linked callouts, then revising and saving them over a long agent session. A useful drawing command is only part of that workflow. The agent also needs to know which drawing it owns, whether a modification has already happened, what remains unverified, and whether another attempt would duplicate it.

The design focuses on those decisions. Durable records, drawing identity and native readback add overhead; no comparative speed, token-cost or productivity advantage has been established. Engineering judgment and domain-specific layout rules remain in separate Skills.

## What the local baseline provides

- **Explicit drawing identity:** process birth, document/database session, drawing path and loaded plugin version are checked. Bind-time metadata is an observation, not continuous health monitoring.
- **Durable request ownership:** server and plugin records support querying a known request after a service restart in the same CAD session. An uncertain write is not automatically replayed.
- **Bounded native operations:** layers, lines, circles, polylines, text/MText, associative hatch from a newly created boundary, and ordinary LEADER/MText callouts with a yellow filled dot.
- **Guarded edits:** fresh native entity digests protect supported text changes, complete associated-group moves and archival to an off/frozen/non-plotting layer.
- **Write and save verification:** tracked properties and geometry are read back; whole-document save includes separate disk readback of specified objects and model-handle/layer checks.
- **A consistent local entry:** CLI and MCP use the same configured gateway and journals. Installation is versioned, starts an owned working copy, and leaves global MCP configuration unchanged.

The tested write profile exposes `dwg_apply_plan`; it does not expose the upstream host's complete command or arbitrary-code surface. Ordinary LEADER is distinct from MLeader. Independent tools and CAD instances do not automatically share this project's transactions, request IDs or fences.

## Choose components by task

| Task | Required components | Versions and limits |
| --- | --- | --- |
| Supported basic CAD operations | `cad-agent-foundation` only | Local baseline `v1.0.0-local`; public source preview `v1.0.0-rc.1`, without a runnable binary asset. Bounded 2D operations; current writes require metres; recorded Windows/AutoCAD 2026 host only. Neither Skill is required. |
| This project's callout style and placement | Foundation + `cad-callout-pair-layout` | Callout rules v0.3, public export pending. Metre-based road defaults; complete styles and layout have not been certified on Foundation v1 alone. Check backend coverage before writing. |
| This project's distress/demolition workflow | Foundation + `cad-distress-annotation` + `cad-callout-pair-layout` | Distress rules snapshot has no independent public release version. Requires engineering evidence, classifications and the callout rules; full workflow needs capabilities outside the current Foundation profile and is not certified on these three components alone. |

The two Skills are optional for basic operations. Their rules are not built into Foundation; installing them does not add missing native operations. Foundation source is public; both Skill repositories remain planned, not published. [Installation entries, versions and Agent checks](docs/COMPONENTS-AND-INSTALLATION.md).

## Quick start

The commands below describe the existing local interface. A public download is intentionally not linked until asset, privacy and licensing review is complete.

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

**The current write interface is restricted to metres:** plans require `units: metres`, and the drawing must use metres or explicitly metre-based unitless coordinates. Millimetres and other units are not enabled or validated. This implementation boundary does not limit the intended foundation to road work.

This release does not provide native DIMENSION objects, MLeader, general old-hatch repair, arbitrary polyline editing, cross-drawing copy, proxy-object semantics, layout/plotting, engineering decisions, or automatic recovery/unlocking of uncertain writes. A committed operation cannot be assumed rolled back because a later verification failed.

The `.NET 8` local baseline was inspected on AutoCAD **R25.1.74.0.0**. AutoCAD 2026.1.2 introduced a `.NET 10` transition; compatibility with that updated host is **not yet validated here**. See [compatibility](docs/COMPATIBILITY.md). [Autodesk runtime update](https://blog.autodesk.io/autodesk-desktop-products-2025-2026-net-10-updates/)

## Separate Skills

See the [anonymous road demolition case](examples/road-demolition/README.md) for existing before/after drawings and quantity/station references. It illustrates companion-Skill output and does not certify a Foundation-only execution of that full workflow.

Planned repositories:

- [cad-callout-pair-layout](https://github.com/capskov/cad-callout-pair-layout): callout style, road-side constraints and finite placement budgets.
- [cad-distress-annotation](https://github.com/capskov/cad-distress-annotation): evidence-based road distress/demolition boundaries and quantity rules.

Foundation supplies supported CAD primitives and their execution contract. Skills decide what to draw, how to lay it out, and which domain checks to apply. Foundation does not determine plant species, distress treatment or engineering quantities.

Callout techniques may also serve landscape planting and other classified drawing annotations. The existing Skill's tested rules and defaults come from metre-based road drawings; other disciplines or millimetre drawings need style, size and placement adaptation and are not certified here.

These repository links are planned, not live publication claims. A complete Foundation-only Skill workflow has not been certified. The Skills remain independently versioned. See [compatibility](docs/COMPATIBILITY.md).

## Working alongside other tools

Agents can use other MCP servers or CAD tools separately. Only supported operations submitted through Foundation enter its governed request, execution and verification flow. An unrelated backend does not automatically share its transactions, request identities or fences. Switching tools is not a way to bypass an unresolved write. Full coverage of an external backend would require an explicit integration; it is not provided by this baseline. See [Foundation, Skills and other tools](docs/FOUNDATION-SKILLS-AND-OTHER-TOOLS.md).

## Upstream and licensing

AutoCAD 2026 is the initial validated host because it matches the working environment and the adopted native plugin route. The local .NET 8 baseline uses document locking, transactions and native readback; 2025 also has a .NET 8 host, and older years have automation APIs. MCP comes from the bridge. See [why 2026](docs/WHY-AUTOCAD-2026.md).

The integrated host derives from Bimwright DWG MCP, pinned at [`4d0f756`](https://github.com/bimwright/dwg-mcp/commit/4d0f75646631186612af926944d7e33affc4757d). Preserve its Apache-2.0 licensing and attribution. Other reviewed projects are credited separately from code actually reused; see [background and related projects](docs/BACKGROUND-AND-RELATED-PROJECTS.md).

The development path was practical: examine existing CAD MCP backends against real tasks, integrate a bounded execution contract into the selected native host, then check writes, native readback and saved drawings. Historical evaluations are documented with their scope rather than presented as a ranking of today's projects.

This public source preview uses Apache-2.0, preserving applicable upstream and MIT-origin notices. Version `v1.0.0-rc.1` includes source and documentation; binary assets remain pending their own review and checks. Dependencies keep their own licenses. AutoCAD and its SDK are not redistributed. HZTXT assets without established redistribution permission are excluded; the bundled Open Sans font retains its own OFL notice. See [license decision](LICENSING.md) and [draft notices](THIRD_PARTY_NOTICES.md).

Human and AI-assisted contributions are welcome when changes and verification evidence are reviewable. The maintainer reviews merges and releases. See [contributing](CONTRIBUTING.md).

AutoCAD and DWG are Autodesk trademarks. This independent project is not endorsed by Autodesk or the upstream maintainers.
