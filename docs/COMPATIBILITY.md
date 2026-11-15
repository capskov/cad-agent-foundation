# Compatibility and independent Skills

| Item | Evidence/current target |
|---|---|
| OS/architecture | Local Windows x64 |
| AutoCAD | Recorded R25.1.74.0.0 installation and AutoCAD 2026 workflow reports |
| Managed target | Server net8.0; plugin net8.0-windows, x64 |
| AutoCAD 2022 (R24.1) | **Migrated local build**: plugin retargeted to `net48` (`plugin-acad22`), host CLR .NET Framework 4.8; see the ACAD2022 section below and `ACAD2022-MIGRATION-STATUS.md` |
| AutoCAD 2026.1.2 / .NET 10 | Not validated here; do not infer support from “2026” |
| Other AutoCAD years, LT, macOS, headless DXF | Not certified by this profile |
| Python entry | Python 3.11+ requirement; local stdlib-based Windows entry |
| Clients | CLI and a local stdio MCP test client checked; other clients need their own integration confirmation |
| Write units / coordinates | 2D WCS; metre plans only, with metre or explicitly metre-based unitless drawings; no unit or map-projection conversion |
| Autodesk Assistant's official MCP | Separate product/server; not used as this project's runtime |

The full AutoCAD patch/runtime baseline for the exact local release should be recorded again when producing the public clean build; its captured binding report records the year and module identity, not every host runtime field. This is environment identification, not a demand to rerun all completed CAD tests.

[Autodesk describes its 2025/2026 .NET 10 updates](https://blog.autodesk.io/autodesk-desktop-products-2025-2026-net-10-updates/). Its [official AutoCAD/Civil 3D MCP](https://help.autodesk.com/view/ADSKMCP/ENU/?guid=ADSKMCP_AutoCADCivil3DMcp_autodesk_autocad_civil_3d_mcp_html) currently documents Autodesk Assistant as the client, with AutoCAD read-only access and Civil 3D write tools. Product Help MCP supplies documentation rather than editing the DWG. These do not establish this project's native integration or patch compatibility.

## AutoCAD 2022 (R24.1 / .NET Framework 4.8) migration profile

This local migration runs the same Foundation architecture on AutoCAD 2022 without changing the 2026 product shape.

| Aspect | 2026 (upstream) | 2022 (migrated) | Notes |
|---|---|---|---|
| Host CLR | .NET 8 | .NET Framework 4.8 | `acdbmgd/acmgd` target `.NETFramework,Version=v4.7`; a net8 assembly cannot load in a 4.8 host |
| Plugin project | `plugin-acad26` (`net8.0-windows`) | `plugin-acad22` (`net48`, `ACAD2022;ACAD2026`, x64) | 60 BCL gaps bridged in `shared/Execution/NetFx.cs`; 0 Autodesk API gaps |
| Data transport | named pipe | TCP (loopback, token-authenticated) | discovery `transport: "tcp"`, `pipe_name: null` |
| Control channel | in-pipe transport | independent control pipe (`control_pipe_name`) with ACL: current user SID only, inheritance off | server routes control commands there when the data channel is TCP |
| SQLite native | resolved beside the assembly | preloaded by absolute path (`NativeLibraryPreloader`) | .NET Framework `DllImport` searches only the host directory and PATH |
| Managed shims | framework-provided | `NetFrameworkAssemblyResolver` binds `System.Memory` etc. from the plugin directory on demand | host `acad.exe.config` is never modified |
| Save before finalize | n/a (upstream forbids plugin-side save) | `BIMWRIGHT_FOUNDATION_SAVE_ENABLED=1` enables `save_drawing`; `finalize` requires a completed save of the same drawing | opt-in, off by default; 2026 semantics unchanged |
| Rejected submissions | journal keeps terminal rows | `write_fence_conflict` / `request_id_conflict` recorded as `failed` rows, never `ok:true` with no row | see `ACAD2022-MIGRATION-STATUS.md` §7.2 |
| Crash recovery | owner liveness (PID + start time + generation lock) | same provider, plus queued-owner-death branch (`owner_lost_before_start`) and `job_recovery_resolve` for `unknown` rows | auto-recovery only for confirmed-lost owners |

Not verified for 2022: AutoCAD 2025/2026 runtime regression (no such installation on this machine), server-side queue timeouts (not implemented in either year), running-phase cancellation (job completes too fast to intercept), and cross-user denial of the control pipe ACL (only wrong-token rejection was exercised).

## Skill compatibility is not a blanket version requirement

| Independent repository | Public version | Foundation v1.1 overlap | Remaining boundary |
|---|---|---|---|
| cad-callout-pair-layout | v0.3.0-rc.1 | Installed fonts/styles, text/leader colors, actual text sizes, pose and complete groups; bounded synthetic solver/native integration | Real-project road/background/layout correctness remains per task; no blanket migration certification. |
| cad-distress-annotation | v0.1.0-rc.1 | New and bounded existing closed-boundary/single-hatch operations, quantities/readback, native dimensions | Boolean/holes/general old-hatch repair and full engineering workflow remain outside the profile. |

Skill repositories should publish tested backend/operation combinations. Do not advertise `Foundation >=1.0` as proof that all Skill steps work. Preserve their current routing safeguards: inspect real tool schemas and identity, do not silently switch to legacy COM or replay unknown writes.

The annotation method can be adapted to other fields, including landscape plant classification. That potential is not evidence of a tested landscape workflow or support for millimetre drawings. Style sizes, scale, placement constraints and unit-dependent checks need explicit adaptation; this preparation does not change the existing Skills or write-unit gate.

## Task dependencies and installation

Foundation alone covers supported basic CAD tasks. Add the callout Skill for this project's style/layout; this project's distress callout presentation uses both Skills and a verified native backend; Foundation is the governed route for supported operations, with missing backend steps checked separately. Neither Skill is a global Foundation dependency. See the bilingual [component selection and installation table](COMPONENTS-AND-INSTALLATION.md). Both Skill previews and their manual-install ZIPs are public; no automatic Skill installer exists.
### AutoCAD 2022：写入后的落盘（2026 无此路径）

上游 2026 禁止插件自身保存，2022 因此单独定义了一条落盘路由：

| 项 | 2022 行为 |
|---|---|
| MCP 工具 | `dwg_foundation_save_drawing`（`request_json`：`output_path` 绝对 `.dwg`、`confirm`、`overwrite_existing`、`allow_repo_output`） |
| 插件侧 | `save_drawing`（读作业，不改图形），需 `BIMWRIGHT_FOUNDATION_SAVE_ENABLED=1` |
| 服务端开关 | `BIMWRIGHT_DWG_FOUNDATION_SAVE_TOOL_ENABLED=true` 时注册该工具 |
| 守卫 | `write_requires_completed_save`：写作业 finalize 前必须存在"同 journal、同图纸指纹、带 `saved_path`"的已完成保存行 |
| 客户端默认 | `apply` 只写不存，返回 `persisted: false` + `verified_not_persisted` 说明 |
| 客户端显式 | `apply --save-to <abs.dwg>` 连续完成写入→验证→保存→finalize 并释放 fence |
| 边界 | 显式一步式**不等于**崩溃后自动 finalize；保存失败或状态不明时 fence 保持，禁止重放 |
