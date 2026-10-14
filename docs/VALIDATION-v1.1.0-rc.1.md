# Native validation — v1.1.0-rc.1 / 原生验收

This is the scoped next-release acceptance, separate from earlier local-v1 and public-rc.1 evidence. The recorded host is Windows x64 AutoCAD 2026 R25.1.74.0.0 / .NET 8. Source inputs for these new tests are freshly created synthetic metre drawings; no formal engineering DWG is modified or published.

## Passed scoped workflows

| Evidence | Result / scope |
| --- | --- |
| Native creation | Installed-font text style and dimension style, Chinese DBText/MText, ordinary linked callout/yellow dot, closed boundary/hatch, Line, four native DIMENSION types. Linear/aligned/radius/diameter native measurements 8/5/3/6 checked independently. |
| Continuation | Closed polyline vertices and native hatch path/area; callout pose including separate dot, format/height/width/gap/colors, linked text content. |
| Editing | Line rotation/scale/copy, complete callout-group copy/scale with associations and source protection, closed surface archival, layer state and filtered query. |
| Save/reopen | Whole drawing saved; 17 tracked native objects independently read from disk and in a fresh read-only CAD copy, matching native digests. Owned CAD stopped normally. |
| MCP | Eight-tool write profile, exact drawing/plugin binding, 17 object reads, layer list, one new native aligned dimension, original-job queries, save and stop. |
| Native refusals | Stale expected digest, missing associated leader, locked source layer: effects none, fences released only after confirmed no-effect outcome; original tracked objects unchanged. Layer restored, saved and stopped. |
| Offline invariants | 19 focused extension geometry/style/tampering checks; 5 CLI identity/no-replay/parameter protection checks; 21 request-ledger scenarios; 67 plugin-journal scenarios. Counts are separate suites, not a summed reliability claim. |
| Package | Independent dependency notices (54 package entries), hashes/MVID, Autodesk/SHX/PDB exclusions and ZIP byte checks. Final archive extraction/install check is tied to its release manifest. |

The separate callout Skill's unchanged solver is used by a synthetic native integration check: actual native text dimensions feed its finite-budget proposals, which are applied through `set_callout` and independently checked against native vertices, text positions, text extents and leader/text conflicts. This is a bounded example, not complete migration of every road/background/layout rule. Final extracted-package outcome: two callouts/eight native objects, solver unchanged, exact proposal/native geometry at the stated civil tolerance, zero native text/leader conflicts, finite budget of three rounds/five extensions/5 m step, saved and stopped. Actual native paragraph extents are used after fitting width from actual rendered width.

## Corrections and retained evidence

Initial dimension validation incorrectly required obsolete native radial LeaderLength and an arbitrary point along the dimension line. AutoCAD reports that length as zero and can choose another representative point on the same line. Native measured values, explicit radial/diameter label positions and perpendicular dimension-line offset are now checked. Failed candidates rolled back and were preserved/closed normally; no unknown request was replayed.

Group scaling preserves the shared named style while scaling effective text height/gap and dot geometry. Floating text metrics/circumference are compared at the separately stated daily civil precision (0.001 m), not bit-exact arithmetic. Identity, old-state SHA256, handles, declared integer colors/flags, associations and request ownership remain exact. HATCH cached area and actual path remain separate evidence, followed by independent native quantity read.

A direct Python CLI used a GBK console and could fail printing squared-unit characters after an already successful save. The original saved request was queried with UTF-8 output, not resubmitted. CLI streams now explicitly emit UTF-8; the existing batch launcher already did so.

## Limits

This evidence does not cover every parameter/font/patch/client/entity. Zero-width/zero-thickness supported model-space entities only for rotate/scale/copy; no DIMENSION/block/proxy transforms or generic deletion. Geometry-only hatch transforms retain pattern parameters. Old shared style overwrite, complex holes/Boolean/general old-hatch repair, 2022/.NET10/millimetres and layout/plot/xref remain outside this release. Native acceptance does not imply whole engineering-drawing correctness, universal fault recovery or speed/token-cost advantage.

中文：本版以全新合成米制图验证，正式工程图不改。已完成四种原生尺寸、字体／尺寸样式、边界填充及引线续改、旋转缩放复制、闭合面归档、图层、查询、整图保存及17对象新进程重开；MCP与三种实机拒绝场景另验。旧失败记录保留，不改写为通过。误差按面积／位置分别判断；HATCH缓存面积独立读取。所有版本、适用对象和未验证范围按实际证据说明。
