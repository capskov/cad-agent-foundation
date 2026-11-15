# CAD Agent Foundation v1.2.0-beta.3

## v1.2.0-beta.3.18 — field reliability and explicit PDF printing

Adds local CTB configuration and native transient window PDF output, plus save/leader/paper-space field fixes. 144 extension checks, 14 Python tests and 13 native A3 outputs passed within the recorded scope. [Evidence and limits](docs/RELEASE-v1.2.0-beta.3.18.md).

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

[Agentic Map Tracing](https://github.com/capskov/agentic-map-tracing) v0.1.0-beta.1 packages agent-led road interpretation, markings/cross-section reasoning, human guidance and actual-overlay correction. Foundation supplies native operations and feedback; the Skill supplies domain judgment. Install/select it only for that task. Basic CAD does not require the Skill. [Components](docs/COMPONENTS-AND-INSTALLATION.md).

## 中文速览

新版提供代理几何/文字读取、受控放缩平移与截图、光栅引用路径修复、明确拒绝分类及常宽多段线事实修正。复用已实测beta.3二进制，不重编已验运行文件；范围有限，不声称全部AutoCAD版本或专业属性兼容。

建议搭配Agentic Map Tracing道路Skill完成卫星影像判读；底座负责操作，Agent/Skill负责道路理解和看图纠错，均可独立选择。原生预览可能颜色量化，显示稳定性仍需实际查看。本次源码、ZIP及MCPB按哈希核对，打包不是新增CAD实机验收。


---

# Release notes

## v1.2.0-beta.2 — local field-feedback fixes

Adds `dwg_view` in the bounded AutoCAD2026 Foundation profile: current view context, WCS window/center navigation, native regen/readback and non-overwriting PNG capture. Reuses native capture APIs without clipboard or arbitrary commands; unsupported layout/rotated/tiled views are not silently changed. Adds fresh-digest single-reference `set_raster_path`, preserving image geometry and refusing shared definitions; native RasterImageDef path/loading is separate from image interpretation or geographic calibration. Entity digests no longer require geometry output. Transient DBText/MText content can be read from decomposed compound entities. Explicit parser/binder rejection is distinct from uncertain transport, and unknown writes retain original-request protection.

Evidence: 97 offline contract checks, four CLI classification checks, packaged MCP on a synthetic owned DWG, three native captures, visible restored raster, unchanged image placement and independently verified save. Native fixture preparation is separate. Prior failed integration receipt is retained; final scoped checks passed and both test sessions closed without fences. No blanket professional proxy/old-host certification, no satellite tracing algorithm, no automatic GitHub publication. See [details and remaining scope](docs/FEEDBACK-BETA2-20261023.zh-CN.md).

## v1.2.0-beta.1 — local proxy geometry research

Adds read-only transient native decomposition through the existing entity-properties MCP tool, WCS geometry, NURBS parameters and bounded geometry paging. Entity write digests remain unchanged; page snapshots include document session/revision. Real CRDAxisEntity and CCivilDb proxies were read on AutoCAD2026; representative road/water geometry was rebuilt and visually sampled, and an actual layout clip boundary was read. Missing frame xrefs remain unavailable. A legacy water drawing's save committed but failed full readback because font-file metadata changed; both protective fences remain held. See [scope](docs/PROXY-GEOMETRY-READ.zh-CN.md) and [field closeout and release limitations](docs/PROXY-GEOMETRY-FIELD-CLOSEOUT.zh-CN.md). Local preparation only; no GitHub publication or global MCP registration change.

## 后续版本命名约定（用户于2026-10-16确认）

- `v1.2.0-beta.1`：功能基本形成，继续实战测试。
- `v1.2.0-rc.1`：功能准备稳定，进入正式发布前验证。
- `v1.2.0`：当前声明支持范围内的功能达到正式发布要求。

保留已发布并验证的`v1.2.0-local.4`。下一轮正常升级时按成熟度选择版本名，不为改名重打包、重打标签或重新发布，也不据此自动安排发布。

## v1.2.0-local.4 — raw drawing units and bounded font operations

Ordinary CAD plans execute raw DWG values with optional INSUNITS consistency checks; legacy metre calls remain compatible. Adds bounded SHX style replacement and explicit DBText metric scaling, plus the earlier 1.2 primitive/curve/text/table/block/page/file operations. Ships a Windows runtime ZIP, matching MCPB, manifests, dependency notices and SHA256 checksums. See [changes, installation, exact native scope and limits](docs/RELEASE-v1.2.0-local.4.md). Prior releases remain available; Registry publication is a separate step.

## v1.1.0-rc.1 — native operations and Windows runtime preview

Adds four native DIMENSION types, text/dimension styles, layer management, closed boundary/hatch and callout continuation, guarded rotation/scaling/copying, and a manifest-bearing Windows runtime package. See [release details](docs/RELEASE-v1.1.0-rc.1.md), [commands](docs/COMMAND-CATALOG-v1.1.0-rc.1.md) and [native validation](docs/VALIDATION-v1.1.0-rc.1.md). Previous releases remain intact.

## v1.0.0-rc.1 — source and documentation preview

First public source preview of CAD Agent Foundation, based on accepted local runtime v1.0.0-local. This is a prerelease, not a turnkey binary distribution.

Includes bounded 2D Foundation plans, drawing/session identity, durable original-request reconciliation, guarded edits, native readback/save mechanisms, the existing CLI/MCP entry source, bilingual installation/compatibility guidance and an anonymous visual case with historical-backend provenance.

Public-export checks: server build passed (zero warnings); AutoCAD 2026 plugin build passed (three existing warnings: two Roslyn reference conflicts and obsolete WebClient). Five Python entry protection checks, 20 ledger scenarios and 67 plugin-journal scenarios passed in the exported tree. These are builds/offline contracts, not a new native CAD acceptance run.

Private history, original CAD fixtures/receipts, runtime state, SDK binaries and unlicensed SHX files are excluded. Historical closed pilot coordinates are illustrative local values; those pilots are not public-supported operations or replacement native evidence. Upstream attribution, Apache/MIT/OFL notices and a resolved source-dependency inventory are retained.

Limits: recorded Windows x64 AutoCAD 2026/.NET 8 baseline, bounded metre-based 2D model-space writes. No native DIMENSION/MLeader, general old-hatch repair, layout/plotting, external-reference editing, cross-DWG copy, arbitrary code or automatic unknown-write replay/unlock. Updated .NET 10 host is not validated. The case's full historical workflow used project Python/COM tools, not Foundation v1 alone.

No runnable binary ZIP is attached: its clean packaging, manifest identities and targeted install/load/native readback still need completion. Do not rename or redistribute the old local ZIP as this preview. The two optional Skill repositories are not yet published and require separate maintainer review.

Versioned capabilities: [command catalog for v1.0.0-rc.1](docs/COMMAND-CATALOG-v1.0.0-rc.1.md) · [中文清单](docs/COMMAND-CATALOG-v1.0.0-rc.1.zh-CN.md). This documentation addition describes the existing version; it introduces no command or renewed native acceptance.

## v1.0.0-rc.2 — documentation and companion publication update

Runtime source is unchanged from v1.0.0-rc.1; no new binary asset or CAD certification. Publishes the already-reviewed 10-action CLI / 9-distinct-interface MCP / 12-operation plan catalog, separates implemented/verified/unsupported branches, links actual callout v0.3.0-rc.1 and distress v0.1.0-rc.1 installations, and updates About/Topics/bilingual discovery wording. Three repositories remain independently maintained; Skills are optional for basic CAD. Existing v1.0.0-rc.1 tag/Release history is retained.

中文：rc.2只更新文档与实际Skill联动、能力清单和检索定位，不新增命令、不重跑CAD验收、不附底座二进制。原rc.1标签与Release保留。

## 本机补充：1.2.0-local.5-pavement（2026-10-18）

路面表达增加普通LEADER关联偏移/Dimtad、尺寸界线与小数分隔符、多段线实体宽度；归档数值校验采用既有图面容差，修复原生尺寸重算末位漂移导致误报。保留身份/关联/哈希检查，已有提交不重放。旧故障经完整原生只读证据与日志受控恢复。独立重开与尺寸归档回归通过；合并后契约91项通过。详见docs/PAVEMENT-LOCAL5-20261018.zh-CN.md。更新本机常用入口，旧包保留，未发布GitHub。
