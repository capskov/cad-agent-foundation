# Foundation interface and capability catalog — v1.0.0-rc.1

[中文](COMMAND-CATALOG-v1.0.0-rc.1.zh-CN.md) · [README](../README.md) · [Validation](VALIDATION.md)

Checked against source tag `v1.0.0-rc.1` (commit `31795b860103a60a431d1575a3f24cee76ea8324`) and existing native evidence for local `v1.0.0-local`. No CAD rerun occurred for this catalog. The public release is source-only; invocation examples describe the packaged interface and require a complete manifest-bearing package and valid working session.

**V: implemented and verified in the stated local workflow. I: implemented without separately identified current-version native/end-to-end proof; offline and historical evidence are distinct. N: not supported by the current Foundation profile.** V does not certify every parameter/drawing/client. Source code or native AutoCAD support alone does not justify V.

## CLI entry: 10 actions

Each row means `foundation.cmd <action> <arguments>`; W is the workspace. `start --write` explicitly enables bounded writes. The `--headless` source flag does not certify a separate headless CAD host or unattended execution.

| action | Arguments | Purpose | Status |
| --- | --- | --- | --- |
| `install` | `--workspace W [--cad CAD_DIR]` | Versioned trusted native installation; verify package files. | V |
| `start` | `--workspace W --source INPUT.dwg [--write]` | Start owned working copy; default read-only. | V |
| `doctor` | `--workspace W` | Bind and inspect identity/module metadata. | V |
| `read` | `--workspace W --handles H1,H2` | Entity read and original-job polling. | V |
| `query` | `--workspace W --arguments query.json` | Submit filtered entity query. | I |
| `apply` | `--workspace W --plan plan.json [--request-id ID]` | Submit once / query the same plan request. | V |
| `save` | `--workspace W --handles H1,H2 [--request-id ID]` | Fresh digests then standalone save plan. | V |
| `status` | `--workspace W --request-id ID` | Inspect a recorded request in W/requests. | I |
| `stop` | `--workspace W` | Close only the owned idle saved drawing without unresolved fences. | V |
| `mcp` | `--workspace W` | Stdio MCP route through the same configured host. | V |

The standalone `query` and `status` CLI branches are implemented but not individually identified in current acceptance; successful original-request queries do not certify every CLI branch. `doctor` is not monitoring; `stop` is not force-kill.

## MCP: eight per profile, nine distinct interfaces

RO means read-only; W means explicitly write-enabled. W replaces RO's `dwg_inspect_lisp` with `dwg_apply_plan`. Discover actual schemas through `tools/list`, then call `tools/call` with `{name, arguments}`. Dynamic wrapping adds `context_id`/`request_id`; C# method signatures alone are not sufficient. Poll the original job after read submission; verify effects, readback and fence status for writes.

| Interface | Profile | arguments | Purpose | Status / evidence boundary |
| --- | --- | --- | --- | --- |
| `dwg_bind_active_document` | RO/W | `{}` | Bind the selected native endpoint and active drawing; return context and captured identity. | **V** — Local CLI/MCP exact binding checked; snapshot, not health monitoring. |
| `dwg_list_layers` | RO/W | `context_id, request_id` | Read layer scalar properties. | **I** — Implemented/exposed; no individually identified current-v1 native invocation in the reviewed acceptance summary. |
| `dwg_get_entity_properties` | RO/W | `context_id, request_id, handles: JSON-array string, include_geometry?` | Read 1–64 handles, supported properties/geometry and native digests. | **V** — Creation/edit/save/reopen reads checked; unsupported or truncated facts remain explicit. |
| `dwg_query_entities` | RO/W | `context_id, request_id; entity_type?, layer?, color_index?, min_x?, min_y?, max_x?, max_y?, limit?, page_token?, include_geometry?` | Filtered top-level model-space candidates and cursor. | **I** — Implemented/exposed; prior constrained query evidence exists, but current-v1 full filter/cursor matrix is not separately certified. Bounds overlap extents; 1–5000 limit; changed revision invalidates cursor. |
| `dwg_job_get` | RO/W | `context_id, request_id, wait_ms: 0` | Inspect the original durable job; never resubmit it. | **V** — Original requests after service restart/repeated plan checked; wait_ms must be zero. |
| `dwg_job_get_historical` | RO/W | `context_id, request_id, observer_context_id` | Inspect one persisted dwg_list_layers job through a different bound observer. | **I** — Narrow historical route; not arbitrary request takeover. No new v1 native acceptance identified; prior/offline evidence is separate. |
| `dwg_job_cancel` | RO/W | `context_id, request_id` | Request cancellation only for owned queued work. | **I** — Cancellation/journal semantics have offline contract coverage; no separate current-v1 CAD cancellation proof. Does not undo running/committed writes. |
| `dwg_inspect_lisp` | RO only | `file? (absolute local path), code?; at least one` | Static local Lisp pattern inspection without execution. | **I** — No CAD round-trip; not antivirus or execution authorization. Excluded from W catalog. |
| `dwg_apply_plan` | W only | `context_id, request_id, plan_json: JSON string` | Submit the bounded plan operations below. | **V** — Two local native workflows and save/reopen checked; does not expose arbitrary upstream commands. |

## Plan operations: 12, not 12 standalone MCP tools

Use `foundation.cmd apply --workspace W --plan plan.json`, or the `plan_json` string of `dwg_apply_plan`. Every step requires a unique `id`, `op` and all fields below; missing/extra fields are rejected. Top-level fields: `schema: foundation-drawing-plan-v1`, `source_role` (annotation/existing/demolition/new_design/test), `units: metres`, `steps`. Points are `[x,y]`; polyline vertices may be `[x,y,bulge]`. Rotation is degrees; hatch angle is radians.

| op | Step fields (plus id/op) | Purpose | Status / limits |
| --- | --- | --- | --- |
| `layer` | `name, color` | Create/reuse a color-matching layer. | **V** — New layer verified; not arbitrary layer-state/style management. |
| `line` | `start, end, layer, color` | New Line. | **V** — Native creation/read/save/reopen checked. |
| `circle` | `center, radius, layer, color` | New Circle. | **V** — Standalone and callout-dot creation observed. |
| `polyline` | `vertices, closed, layer, color` | New 2D Polyline; optional per-vertex bulge. | **V** — Closed boundary verified; open/bulged combinations not separately v1-native certified. Does not reshape existing polylines. |
| `text` | `text, position, height, rotation, layer, color` | New single-line DBText. | **V** — Content/position/default-style construction verified; no explicit style/font field. |
| `mtext` | `text, position, height, rotation, width, layer, color` | New multiline MText. | **V** — Creation verified; not every inline format/font or paragraph feature. |
| `callout` | `text, position, height, width, vertices, dot_radius, layer, color` | Ordinary Leader + MText + yellow Circle + SOLID Hatch. | **V** — Four-member association checked. Caller supplies geometry; no collision solver/project style workflow/MLeader. |
| `hatch` | `boundary (earlier step id), pattern, scale, angle, layer, color` | Associative Hatch on an earlier new boundary. | **V** — ANSI31 and SOLID local workflows checked; not all patterns/holes/old-hatch repairs. |
| `set_text` | `handle, expected_sha256, text, companions[]` | Guarded DBText/MText content update. | **V** — Linked MText update verified. DBText update branch exists but lacks separate v1-native proof; preserve complete associations. |
| `move` | `targets[{handle,expected_sha256}], delta` | Move supported entities / complete logical group. | **V** — Full four-member callout group verified. Standalone Line/DBText and other branches not all individually checked; no existing Polyline move support claimed. |
| `archive` | `targets[{handle,expected_sha256}], layer` | Archive supported entities/group to an off/frozen/non-plot layer. | **V** — Linked group verified; not deletion or arbitrary management of old hatch/polyline groups. |
| `save` | `expected[{handle,expected_sha256}]` | Whole-document save and selected-object independent disk readback. | **V** — Standalone one-step plan, 1–64 expected objects. Includes all unsaved drawing edits; not full geometric proof of every object. |

Bounds: 1–16 steps, at most 64 entity outcomes, semantic JSON 128 KiB. Polyline ≤128 vertices, abs(bulge)≤1; LEADER 2–16 vertices; total plan vertices ≤1024. move/archive 1–32 targets per step. ACI 1–256; nonempty text ≤2048 characters. Metre or explicitly metre-based unitless drawing writes only; no unit/projection conversion.

## Focused audit and unsupported capabilities

| Capability | Status | Actual boundary |
| --- | --- | --- |
| Ordinary linked callout | V | Four-native-object callout; not MLeader. Full styles/side/avoidance rules belong to Skill. |
| Single-line / multiline text | V (creation) | DBText/MText creation verified; linked MText content update verified. Standalone DBText update lacks separate native proof (I). |
| Height/width/rotation/layer/ACI parameters | V (sample values) | Only declared plan fields; not complete style management or every combination verified. |
| List/create/edit text styles, fonts, dimension styles | N | No independent style-management API in this profile. Text constructors use database defaults. Skill rules do not create a missing backend interface. |
| Native linear/aligned/radial/diameter DIMENSION | N | Upstream DimensionTools code exists but is not exposed or current-version native-certified. |
| MLeader / multileader styles | N | Ordinary LEADER is not MLEADER. |
| Arbitrary existing-object rotate/scale/copy/delete | N | move/archive are not a general editing suite; archival is not deletion. |
| Layout/plot/xref edits, cross-DWG, arbitrary code/Lisp execution | N | Historical case or upstream capability does not imply current-profile support. |
| General old-hatch repair, Boolean/complex-hole workflow | N | Closed H1/J10 pilots are not generic public commands. |

Unexposed examples: `dwg_create_leader`, `dwg_create_linear_dimension`, `dwg_create_aligned_dimension`, `dwg_create_radial_dimension`, `dwg_create_diameter_dimension`, `dwg_apply_unicode_style`. Their presence in retained source does not make them callable through the Foundation catalog; do not switch to a broader host to bypass restrictions.

## Evidence and version maintenance

[Local native summary](VALIDATION.md) and [public export builds/offline checks](PUBLIC-EXPORT-VALIDATION.md). The former is historical accepted local-runtime evidence; the latter is not a new binary install/CAD certification. Each future release should link its versioned catalog, not use later main-branch capabilities as historical claims. See [BUILD](BUILD.md) for source-only installation limits.

Reviewed sources:

- [foundation.py](../client/foundation.py)
- [readonly-entry.py](../scripts/readonly-entry.py)
- [Program.cs](../third_party/bimwright-dwg-mcp/fork/src/server/Program.cs)
- [QueryTools.cs](../third_party/bimwright-dwg-mcp/fork/src/server/Tools/QueryTools.cs)
- [FoundationExecutionTools.cs](../third_party/bimwright-dwg-mcp/fork/src/server/Tools/FoundationExecutionTools.cs)
- [FoundationDrawingPlanTools.cs](../third_party/bimwright-dwg-mcp/fork/src/server/Tools/FoundationDrawingPlanTools.cs)
- [FoundationDrawingPlan.cs](../third_party/bimwright-dwg-mcp/fork/src/shared/Execution/FoundationDrawingPlan.cs)
- [FoundationPlanWriteHandler.cs](../third_party/bimwright-dwg-mcp/fork/src/plugin-acad26/FoundationPlanWriteHandler.cs)
