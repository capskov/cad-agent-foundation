# Release notes

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
