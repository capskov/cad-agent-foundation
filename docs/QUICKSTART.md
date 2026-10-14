# Local installation and first operation

This release provides source only, not a runnable binary download. Build prerequisites and current packaging gap: [BUILD](BUILD.md). The commands below describe the accepted local packaged interface and require a complete manifest-bearing package.

## Choose components first

| Task | Required components | Versions and limits |
| --- | --- | --- |
| Supported basic CAD operations | `cad-agent-foundation` only | Local baseline `v1.0.0-local`; public source preview `v1.0.0-rc.1`, without a runnable binary asset. Bounded 2D operations; current writes require metres; recorded Windows/AutoCAD 2026 host only. Neither Skill is required. |
| This project's callout style and placement | Foundation + `cad-callout-pair-layout` | Callout rules v0.3, public export pending. Metre-based road defaults; complete styles and layout have not been certified on Foundation v1 alone. Check backend coverage before writing. |
| This project's distress/demolition workflow | Foundation + `cad-distress-annotation` + `cad-callout-pair-layout` | Distress rules snapshot has no independent public release version. Requires engineering evidence, classifications and the callout rules; full workflow needs capabilities outside the current Foundation profile and is not certified on these three components alone. |

This page installs Foundation only. Public assets and reviewed Skill exports are pending; no automatic Skill installation is implemented. [Component and tool preflight](COMPONENTS-AND-INSTALLATION.md).

Publication draft. This documents the accepted local CLI; the public binary asset and its checksum will be added only after release review. A source checkout alone is not a ready-to-run installed package.

Requirements: Windows x64, a separately licensed AutoCAD installation matching the tested host profile, .NET 8 Desktop Runtime for the current baseline, and Python 3.11+. Updated .NET 10-based AutoCAD hosts are not validated by this baseline. See [compatibility](COMPATIBILITY.md).

```bat
foundation.cmd install --workspace "C:\CAD-work\demo01"
foundation.cmd start --workspace "C:\CAD-work\demo01" --source "C:\drawings\input.dwg" --write
foundation.cmd doctor --workspace "C:\CAD-work\demo01"
```

Installation requires write permission in the selected AutoCAD installation's plugin directory. A versioned installation is hash-checked; do not disable trusted loading to bypass a failed installation. The working drawing is `C:\CAD-work\demo01\drawings\work.dwg`. A new session needs a new workspace; the current entry does not resume a closed CAD process in-place.

Example `plan.json`, UTF-8, deliberately synthetic:

```json
{"schema":"foundation-drawing-plan-v1","source_role":"test","units":"metres","steps":[{"id":"layer","op":"layer","name":"CF_DEMO","color":2},{"id":"line","op":"line","start":[100,100],"end":[120,100],"layer":"CF_DEMO","color":256},{"id":"label","op":"text","text":"Demo length: 20 m","position":[100,103],"height":2.5,"rotation":0,"layer":"CF_DEMO","color":256}]}
```

```bat
foundation.cmd apply --workspace "C:\CAD-work\demo01" --plan "plan.json"
foundation.cmd read --workspace "C:\CAD-work\demo01" --handles RETURNED_HANDLES
foundation.cmd save --workspace "C:\CAD-work\demo01" --handles RETURNED_HANDLES
foundation.cmd stop --workspace "C:\CAD-work\demo01"
```

`RETURNED_HANDLES` must be replaced with the comma-separated real handles from the result. Check `effects`, `verification` and fence release; a transport exit or an accepted request alone does not prove the drawing operation succeeded.

The entry persists the request before submission. Repeating the same plan normally queries its existing request; after an uncertain reply use `status` with the original request ID. A fresh ID is for a deliberate new action, never for bypassing an unresolved write. Do not delete journals or switch providers to retry an uncertain write.

Save persists the entire working drawing, including other unsaved edits in that instance. Up to 64 selected objects are disk-verified; that does not certify every original entity or project requirement. Stop requires a saved, idle owned instance with no unresolved fences; it does not force-kill CAD.

For MCP, start generates `mcp-config.json` in the workspace. Add that fragment to a client supporting local stdio MCP if desired; the installer does not change global client configuration. Bind with `dwg_bind_active_document`, inspect identity/version, then pass the bound `context_id` and a stable `request_id`. CLI and MCP cannot run concurrently in the same workspace.

Keep the workspace private: connection tokens, journals, object facts and drawing paths may be present. Sharing the complete workspace is not a public bug report. For reproductions, create a minimal synthetic case and remove customer/project identifiers.
