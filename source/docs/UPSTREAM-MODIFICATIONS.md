# Modified upstream files

Based on pinned upstream `4d0f75646631186612af926944d7e33affc4757d`. This list covers selected derived files retained in this export; additions are identified by their source provenance and repository history. Per-file modification notices accompany the derived sources.

- `src/plugin-acad24/App.cs`
- `src/plugin-acad26/Bimwright.Dwg.Plugin.Acad26.csproj`
- `src/server/AuthToken.cs`
- `src/server/Bake/BakePaths.cs`
- `src/server/Bimwright.Dwg.Server.csproj`
- `src/server/DwgMcpConfig.cs`
- `src/server/PluginClient.cs`
- `src/server/Program.cs`
- `src/server/Properties/AssemblyInfo.cs`
- `src/server/ServerLogger.cs`
- `src/server/ServerState.cs`
- `src/server/ToolCatalogBuilder.cs`
- `src/server/Tools.cs`
- `src/server/Tools/QueryTools.cs`
- `src/server/Tools/ViewTools.cs`
- `src/shared/Cad/CadEntityProperties.cs`
- `src/shared/Cad/CadQueryService.cs`
- `src/shared/Config/PluginSettings.cs`
- `src/shared/Export/ExportPathPolicy.cs`
- `src/shared/Handlers/GetEntityPropertiesHandler.cs`
- `src/shared/Handlers/QueryEntitiesHandler.cs`
- `src/shared/Infrastructure/CommandDispatcher.cs`
- `src/shared/Infrastructure/PluginLog.cs`
- `src/shared/Infrastructure/ResponseSizeGuard.cs`
- `src/shared/Infrastructure/SchemaValidator.cs`
- `src/shared/Security/McpResponsePrivacy.cs`
- `src/shared/Security/PathAllowlist.cs`
- `src/shared/Transport/PipeTransportServer.cs`
- `src/shared/Transport/TcpTransportServer.cs`
- `src/shared/Unicode/UnicodeStyleService.cs`
- `src/shared/View/CaptureViewService.cs`
- `src/shared/assets/fonts/README.md`

Public metadata, font attribution and illustrative closed-pilot coordinate changes are further described in PUBLICATION-SCOPE.md. Historical pilot/test receipts are not exported.


## Local phase two, 2026-10-15

Added guarded primitive/curve/text/dimension/table/block/page/file contracts and native handlers in FoundationPhase2, FoundationCurve, FoundationAngular, FoundationBlock, FoundationPrint and FoundationDxf files. Existing attribute services, ledger, native transaction, verifier and stdio MCP routes are reused. CadEntityProperties now reports explicit cell/attribute/style coverage and primitives; catalog reports actual runtime, resources and bounded plot settings. Save checks layout settings and named definitions. CLI retries original-request reads only; no submission replay or fence bypass. Versions target 1.2.0-local.1; professional Skills remain independent. See PHASE2-DEVELOPMENT-20261015.zh-CN.md for exact limits and evidence distinctions.

## Local unit closeout, 1.2.0-local.4 (2026-10-16)

FoundationDrawingPlan validates optional raw-mode expected_insunits and shares the unit consistency predicate with FoundationPlanWriteHandler. Existing identity, transaction, ownership and unknown-write mechanisms are retained. CLI save uses raw coordinates without attaching a unit guard. MCP instructions/catalog, Agent entry instructions, package metadata and unit/typography documentation describe INSUNITS as auxiliary metadata. Existing numerical tolerances were inspected and retained; engineering acceptance is Agent/Skill/user work. Native regression reuses the existing packaged MCP harness for metre, millimetre and unitless synthetic drawings. PDF/DXF code is unchanged.
