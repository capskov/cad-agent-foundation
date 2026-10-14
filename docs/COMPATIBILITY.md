# Compatibility and independent Skills

| Item | Evidence/current target |
|---|---|
| OS/architecture | Local Windows x64 |
| AutoCAD | Recorded R25.1.74.0.0 installation and AutoCAD 2026 workflow reports |
| Managed target | Server net8.0; plugin net8.0-windows, x64 |
| AutoCAD 2026.1.2 / .NET 10 | Not validated here; do not infer support from “2026” |
| Other AutoCAD years, LT, macOS, headless DXF | Not certified by this profile |
| Python entry | Python 3.11+ requirement; local stdlib-based Windows entry |
| Clients | CLI and a local stdio MCP test client checked; other clients need their own integration confirmation |
| Write units / coordinates | 2D WCS; metre plans only, with metre or explicitly metre-based unitless drawings; no unit or map-projection conversion |
| Autodesk Assistant's official MCP | Separate product/server; not used as this project's runtime |

The full AutoCAD patch/runtime baseline for the exact local release should be recorded again when producing the public clean build; its captured binding report records the year and module identity, not every host runtime field. This is environment identification, not a demand to rerun all completed CAD tests.

[Autodesk describes its 2025/2026 .NET 10 updates](https://blog.autodesk.io/autodesk-desktop-products-2025-2026-net-10-updates/). Its [official AutoCAD/Civil 3D MCP](https://help.autodesk.com/view/ADSKMCP/ENU/?guid=ADSKMCP_AutoCADCivil3DMcp_autodesk_autocad_civil_3d_mcp_html) currently documents Autodesk Assistant as the client, with AutoCAD read-only access and Civil 3D write tools. Product Help MCP supplies documentation rather than editing the DWG. These do not establish this project's native integration or patch compatibility.

## Skill compatibility is not a blanket version requirement

| Independent repository | Public version | Foundation v1.1 overlap | Remaining boundary |
|---|---|---|---|
| cad-callout-pair-layout | v0.3.0-rc.1 | Installed fonts/styles, text/leader colors, actual text sizes, pose and complete groups; bounded synthetic solver/native integration | Real-project road/background/layout correctness remains per task; no blanket migration certification. |
| cad-distress-annotation | v0.1.0-rc.1 | New and bounded existing closed-boundary/single-hatch operations, quantities/readback, native dimensions | Boolean/holes/general old-hatch repair and full engineering workflow remain outside the profile. |

Skill repositories should publish tested backend/operation combinations. Do not advertise `Foundation >=1.0` as proof that all Skill steps work. Preserve their current routing safeguards: inspect real tool schemas and identity, do not silently switch to legacy COM or replay unknown writes.

The annotation method can be adapted to other fields, including landscape plant classification. That potential is not evidence of a tested landscape workflow or support for millimetre drawings. Style sizes, scale, placement constraints and unit-dependent checks need explicit adaptation; this preparation does not change the existing Skills or write-unit gate.

## Task dependencies and installation

Foundation alone covers supported basic CAD tasks. Add the callout Skill for this project's style/layout; this project's distress callout presentation uses both Skills and a verified native backend; Foundation is the governed route for supported operations, with missing backend steps checked separately. Neither Skill is a global Foundation dependency. See the bilingual [component selection and installation table](COMPONENTS-AND-INSTALLATION.md). Both Skill previews and their manual-install ZIPs are public; no automatic Skill installer exists.
