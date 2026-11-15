# CAD Agent Foundation v1.2.0-beta.3

Scoped Windows x64 / AutoCAD 2026 / .NET 8 runtime preview. Compatible licensed AutoCAD, .NET 8 Desktop Runtime and Python 3.11+ remain prerequisites. Older releases and tags are retained.

## New since v1.2.0-local.4

- Read-only proxy/compound decomposition into WCS primitives, text and bounded geometry pages; proprietary pipe/node design attributes are not inferred from display shapes.
- Bound `dwg_view`: view readback, WCS window/center positioning, zoom/pan through explicit center/height, and native PNG capture with drawing/camera identity and hashes.
- Native raster-reference facts and guarded `set_raster_path`; changes the external file reference while preserving image geometry, not georeferencing or generic DWG Xref editing.
- Explicit pre-submission rejection is distinguished from unknown writes; unavailable digests are reported rather than silently omitted; transient DBText/MText can be read.
- Constant-width polyline creation facts include widths; fixes erroneous post-create verification rollback. Nonzero-width rotate/scale/copy remains rejected before effects.
- Redraw after preview capture and truthful post-capture feedback. Current-layer protection and original-request reconciliation remain unchanged.

## Evidence and limits

Existing local evidence: 101 offline contracts; packaged beta.3 MCP checks covering seven 0.08-width polylines, independent readback, guarded refusal, view movement/capture and save/disk readback. Earlier beta.2 synthetic raster-path, text and rejection checks are retained. Scoped field observations include subsequent actual-window viewing. This publication reuses those accepted server/plugin binaries; no new CAD acceptance is claimed from documentation or repackaging.

Plugin MVID: `b8287e47-02b2-4da4-ac0a-7952943278af`.
Plugin SHA256: `300EEF9A673F88DE0C331212380FB65D180B7D60BD35B09B40788AF0EC27B6F6`.

Native preview may use a limited-color palette; pixel dimensions alone do not ensure faithful pavement/sidewalk colors. Cross-check source imagery and actual windows. `display_settle_verified` remains false; post-capture redraw does not guarantee all external tiles or drivers are visually stable. New PNG basenames currently use ASCII and must not overwrite files. Views are restricted to the supported model-space top-view profile. Existing Roslyn/WebClient build warnings remain.

## Upgrade and use

Download the ZIP or matching MCPB from this release; verify SHA256SUMS.txt. Extract fully, install into a fresh owned workspace with `foundation.cmd install`, then explicitly start a working-copy drawing with the matching plugin. Preserve older sessions with their matching entry. Do not rebind an unresolved request to a new version or replay uncertain writes. Default startup remains read-only; supported writes require explicit mode selection.

## Optional interpretation Skill

[Agentic Map Tracing](https://github.com/capskov/agentic-map-tracing) v0.1.0-beta.1 packages agent-led road interpretation, markings/cross-section reasoning, human guidance and actual-overlay correction. Foundation supplies native operations and feedback; the Skill supplies domain judgment. Install/select it only for that task. Basic CAD does not require the Skill. [Components](COMPONENTS-AND-INSTALLATION.md).

## 中文速览

新版提供代理几何/文字读取、受控放缩平移与截图、光栅引用路径修复、明确拒绝分类及常宽多段线事实修正。复用已实测beta.3二进制，不重编已验运行文件；范围有限，不声称全部AutoCAD版本或专业属性兼容。

建议搭配Agentic Map Tracing道路Skill完成卫星影像判读；底座负责操作，Agent/Skill负责道路理解和看图纠错，均可独立选择。原生预览可能颜色量化，显示稳定性仍需实际查看。本次源码、ZIP及MCPB按哈希核对，打包不是新增CAD实机验收。
