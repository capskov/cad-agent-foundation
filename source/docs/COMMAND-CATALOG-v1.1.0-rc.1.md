# Foundation command catalog — v1.1.0-rc.1

[中文](COMMAND-CATALOG-v1.1.0-rc.1.zh-CN.md) · [previous catalog](COMMAND-CATALOG-v1.0.0-rc.1.md)

This version adds bounded native operations to the existing governed route. Current verification status and host are recorded in the accompanying acceptance document; implementation alone is not native proof. `document_info.foundation_catalog` at bind/doctor lists implemented operations, layer facts/digests and text/dimension styles. It is a bind-time snapshot, with `writes_enabled` separate from capability.

## CLI: ten actions

`foundation.cmd install/start/doctor/read/query/apply/save/status/stop/mcp --workspace W`. Existing invocation and request identity remain as described in the previous catalog. `install` needs a manifest-bearing runtime ZIP, compatible licensed AutoCAD 2026, .NET 8 Desktop Runtime and Python 3.11+. `start` owns a copy and defaults to read-only. `status` queries the original recorded request; `stop` requires one idle, saved owned drawing with no unresolved fences. No global MCP configuration is edited.

## MCP: nine distinct interfaces

Both modes expose `dwg_bind_active_document`, `dwg_list_layers`, `dwg_get_entity_properties`, `dwg_query_entities`, `dwg_job_get`, `dwg_job_get_historical`, `dwg_job_cancel`. Read-only mode adds `dwg_inspect_lisp`; write mode replaces that with `dwg_apply_plan`. No extra arbitrary upstream writer is opened. Cancellation remains queued-only; historical-query scope remains narrow. Discover actual tool schemas first.

## Plan: 25 operations

All use `foundation-drawing-plan-v1`, `source_role`, `units: metres` and `steps`. Every step requires `id/op`; the listed fields are in addition to them. Existing-object guards use freshly read uppercase native `expected_sha256`, explicit handles and complete associated members. Coordinates are `[x,y]` in 2D WCS drawing metres. Rotation is degrees; hatch angle is radians. Limits remain 16 steps/64 entity outcomes/semantic 128 KiB. A copy counts both protected sources and outcomes toward that limit.

| Operation | Step fields | Scope |
| --- | --- | --- |
| `layer` | `name,color` | Create or reuse a color-matching visible unlocked layer. |
| `set_layer` | `name,expected_sha256,color,is_off,is_frozen,is_locked,is_plottable` | Update a named existing non-xref layer from a fresh catalog digest; affects all objects using that layer. Current layer cannot be hidden; layer 0 cannot be hidden. No rename/delete/purge. |
| `text_style` | `name,font_file,big_font_file,width_factor` | Create or reuse an exactly matching style; fonts are installed basenames, not distributed or arbitrary paths. Empty big_font_file means none. No overwrite of an existing style. |
| `dimension_style` | `name,text_style,text_height,arrow_size,overall_scale,gap,decimal_places,dimension_color,extension_color,text_color` | Create/reuse a matching style; other variables inherit the current native style. Never overwrite a shared existing style. |
| `line` | `start,end,layer,color` | New Line. |
| `circle` | `center,radius,layer,color` | New Circle. |
| `polyline` | `vertices,closed,layer,color` | New 2D Polyline; `[x,y,bulge]` vertices supported, abs(bulge)≤1. |
| `text` | `text,position,height,rotation,layer,color`; optional `text_style` | New DBText. |
| `mtext` | text fields plus `width`; optional `text_style` | New MText; read native actual_width/actual_height to measure real formatted content. Width does not guarantee one visual line. |
| `callout` | `text,position,height,width,vertices,dot_radius,layer,color`; optional `text_style,dimension_style,text_color,gap,attachment` | Ordinary LEADER, associated MTEXT and yellow Circle/SOLID dot; caller chooses geometry, not MLeader. Nine named MTEXT attachments; BottomRight matches the current separate solver. |
| `hatch` | `boundary,pattern,scale,angle,layer,color` | One association to a new closed polyline/circle from an earlier plan step. |
| `linear_dimension` | `start,end,position,dimension_style,layer,color`; optional `rotation` | Native RotatedDimension; projected measurement and physical dimension-line offset are checked. |
| `aligned_dimension` | `start,end,position,dimension_style,layer,color` | Native AlignedDimension. |
| `radial_dimension` | `center,chord,text_offset,dimension_style,layer,color` | Native RadialDimension; supplied circle geometry and explicit text position, not live circle association. |
| `diameter_dimension` | same radial fields | Native DiametricDimension; opposite chord derived from center. text_offset sets the label beyond the supplied chord; obsolete native LeaderLength is not used as proof. |
| `set_text` | `handle,expected_sha256,text,companions[]` | Content-only DBText/MText update; formatting dimensions may change with content. |
| `set_polyline` | `handle,expected_sha256,vertices,companions[]` | Closed zero-width WCS boundary, 3–128 vertices, at most one associated single-boundary hatch; rebuild affected hatch path. No holes/general Boolean repair. |
| `set_callout` | `handle,expected_sha256,text_handle,text_expected_sha256,vertices,position`; optional `dot_companions[]` | Three-vertex ordinary LEADER and MTEXT pose. Moving a separate-dot anchor needs the circle/hatch pair; fixed-anchor tail movement preserves the dot. |
| `set_callout_format` | leader/text identity fields plus `text_style,dimension_style,height,width,gap,text_color,leader_color`; optional `attachment` | Format a linked ordinary LEADER/MTEXT pair while preserving geometry, content, association and arrow mode; attachment changes only when explicit. |
| `move` | `targets[],delta` | Existing accepted primitive/complete-callout move; not a general polyline editing route. |
| `rotate` | `targets[],base,angle` | Supported zero-thickness/zero-width 2D Line/Circle/DBText/MText/Leader/Polyline and complete associated hatch groups. |
| `scale` | `targets[],base,factor` | Positive uniform scale 1e-6..1e6; callout text, gap and dot geometry scale together via overrides; shared style records remain unchanged. |
| `copy` | `targets[],delta` | Same-drawing deep copy of those supported complete groups, remapping leader/text and hatch/boundary associations; originals protected. |
| `archive` | `targets[],layer` | Reversible off/frozen/non-plot layer archival, including complete closed-boundary/hatch groups. No generic delete or block/xref archival. |
| `save` | `expected[]` | Separate one-step whole-DWG save with specified-object independent disk readback plus model/layer/style protection. |

`targets/companions/expected` members are `{handle,expected_sha256}`. For `set_callout`, `handle` is the LEADER and `text_handle` its MTEXT. Layer digests are from a freshly bound `foundation_catalog.layer_digests`; entity digests come from `dwg_get_entity_properties`. Stale state or missing associations is rejected. A native write returning unknown is queried by its original request; it is never replayed or unlocked automatically.

## Geometry and feedback

Area and position/path are independent checks. Routine area tolerance is below 0.001 m²; supported position/size comparisons use 0.001 m. Identity, handles, old-state digests, colors, association names and journal ownership remain exact. AutoCAD may shift a dimension-line representative point along the same line: verify the perpendicular offset and endpoint geometry, not arbitrary along-line coordinates.

HATCH.Area can be stale in the same mutation transaction. Boundary/actual hatch path/association are verified there; modified-hatch receipts explicitly require independent area reading. A Skill must separately read current native area before engineering quantity acceptance. Equal area alone never proves a matching path.

MText GeometricExtents can include its full paragraph Width, while ActualWidth/ActualHeight describe the formatted text. For finite placement, first fit paragraph width to the measured content with a small margin, then independently read the resulting native extents as obstacles. Preserve remaining extension budgets after native checks; do not reset them when adjusting a remaining conflict.

## Not supported / later stages

No arbitrary code/Lisp execution, arbitrary property editing, generic deletion/purge, MLeader, block/proxy editing, DIMENSION rotate/scale/copy, complex-hole/Boolean geometry, cross-DWG transfer, layout/plot/xref editing, automatic uncertain-write recovery, millimetres or updated .NET 10/AutoCAD 2022 certification. Text/dimension style overwrite is deliberately outside this profile; create a new named style instead. English/Chinese BZYD remains independently maintained and is not changed by this release.
