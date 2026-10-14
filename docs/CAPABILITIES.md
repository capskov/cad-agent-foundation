# Capability status

Baseline: v1.0.0-local. “Implemented” is scoped to the configured bounded profile; it is not a blanket guarantee for every input.

| Capability | Current state | Boundary |
|---|---|---|
| Drawing/process/document/database binding | Implemented, locally checked | Exact owned copy and session; bind-time observation |
| Durable ownership, original-request query, fences | Implemented; same-CAD-session service restart checked | No generic job takeover across CAD restarts |
| Layers, Line, Circle, Polyline, DBText, MText | Implemented, local workflow checked | New bounded 2D model-space plans; existing-layer state protected |
| Associative hatch | Implemented, local workflow checked | Boundary created earlier in the same plan; not a general old-hatch repair tool |
| Ordinary LEADER/MText/yellow filled dot | Implemented, local workflow checked | Distinct from MLeader; not full project-style adaptation or collision-free layout |
| Native DIMENSION / MLeader | Not supplied by this profile | Descriptive callouts are not native dimensions |
| Other drawing units / map projection conversion | Not supplied by this profile | Current write gate requires metres; no CRS conversion |
| Existing text update | Implemented, locally checked | Fresh digest and complete associated companions |
| Move/archive supported logical groups | Implemented, locally checked | Supported entity types and complete associations; no general polyline reshape |
| Whole-document save and selected-object disk readback | Implemented, locally checked | Includes all unsaved document edits; checks selected facts, model handles and layers |
| Owned-copy install/start/doctor/stop, CLI/MCP | Implemented, locally checked | Windows local environment; safe stop does not override unresolved fences |
| Filtered model-space query and cursor | Implemented in existing governed reads | Extents-overlap candidates, not exact geometry intersection; stale after changes |
| Local static Lisp inspection | Read-only catalog capability | Not Lisp execution; omitted from the write-profile catalog |
| H1/J10 fixed repair pilots | Historical restricted experiments, closed milestones | Not the public general drawing API; no new sample expansion |
| View/preview work from prior stages | Historical constrained work | Not exposed as a supported v1 public profile |
| Full Skill workflows, all other upstream tools | Not certified by this release | Tool discovery and per-operation validation required |
| Layout/plot/export, cross-DWG copy, proxy editing | Future candidate work | Not implemented in this profile; no release dates promised |
| Automatic unknown-write unlock/replay/recovery | Not provided | Preserve evidence and fence; absence of a reply does not authorize replay |

Plan bounds: 1–16 steps, up to 64 entity outcomes, semantic JSON up to 128 KiB; new polylines up to 128 vertices each and total plan vertices up to 1024. Existing move/archive target lists are bounded to 32 per step. Save is a separate single-step plan. Points are 2D WCS coordinates expressed in drawing units. WCS does not define a physical unit or a map projection. The current parser requires `units: metres`; native writes accept `INSUNITS=6`, or `INSUNITS=0` with the explicit metre declaration. Other units are not currently enabled or validated. Text rotation uses degrees and hatch angle radians.

The layer primitive creates a layer or reuses an existing layer with matching color; it is not a full layer-management API. New polyline creation is distinct from arbitrary editing of existing polylines. Supported callout height, width, vertices and dot radius are explicit plan parameters, not a complete project-style engine. See [Foundation, Skills and other tools](FOUNDATION-SKILLS-AND-OTHER-TOOLS.md) for domain and external-backend boundaries.

Object identity and old-value digests are exact guards. For the tested road-work geometry checks, area difference below 0.001 m² and position checks at 0.001 m were used independently. Those engineering checks are not manufacturing tolerances or a coordinate/unit-conversion service.

Reads may report missing, unsupported or truncated information. Do not infer a complete geometric or engineering proof from an area, bounding box, association flag or tool name alone.
