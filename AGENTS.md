# Working with this project

Use the actual local tool catalog and schema. Current writes use a configured AutoCAD 2026 owned-copy profile; no tool name alone proves compatibility or authorization.

Select components by task before execution: Foundation alone for supported basic CAD; add cad-callout-pair-layout for this project's style/layout; prepare both that Skill and cad-distress-annotation for distress/demolition. Check actual versions, resources and tool schemas; installing a Skill does not add native operations. Report missing components using [installation entries](docs/COMPONENTS-AND-INSTALLATION.md), including actual release/installation status, and report missing backend capabilities separately. No automatic Skill installer exists. Reusable native operations and execution constraints belong in Foundation; domain decisions, styles and placement rules belong in Skills.

Bind explicitly and inspect drawing/process/session/module identity. Use fresh entity digests for existing objects and include all required associated members. Submit a write once with a stable request ID. After timeout or an ambiguous reply, query that original request; do not replay, change providers, delete journals or force-release a fence.

Effect state and verification are separate. A later verification failure does not roll back a committed write. Save affects the entire working drawing. Metadata is bind-time observation; do not infer continuing idle/writable state.

Keep source drawings and runtime credentials private. Use authorized working copies and synthetic public reproductions. Respect the documented profile limits and separate engineering Skills. Offline tests or documentation changes do not prove CAD execution; report the exact evidence and missing coverage.

The two companion Skills and future binary assets require separate maintainer review. These Agent instructions do not authorize external publication, configuration changes or tool fallback.
