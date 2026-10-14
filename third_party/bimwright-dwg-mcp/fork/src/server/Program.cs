// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bimwright.Dwg.Server.Execution;
using Bimwright.Dwg.Server.Tools;
using Bimwright.Dwg.Shared;
using Bimwright.Dwg.Shared.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            if (args != null && args.Any(a => a == "--help" || a == "-h"))
            {
                PrintHelp();
                return;
            }

            // Pin and validate the root before reading config, SQLite, discovery,
            // or creating the MCP host. Invalid overrides fail closed here.
            BimwrightAppRoot.Initialize();
            var config = DwgMcpConfig.Load(args);
            ValidateTarget(config.Target);
            if (config.EnableD1WritePilotOrDefault && !config.ReadOnlyOrDefault
                && !string.Equals(config.Target, "2026", StringComparison.Ordinal))
                throw new InvalidOperationException("The D1 write pilot requires --target 2026 and AutoCAD 2026 pipe transport.");
            D1PreflightPilotScope d1PreflightPilotScope = ResolveD1PreflightPilotScope(config);
            H1PreflightPilotScope h1PreflightPilotScope = ResolveH1PreflightPilotScope(config);
            H1WritePilotScope h1WritePilotScope = ResolveH1WritePilotScope(config);
            FoundationWriteScope foundationWriteScope = ResolveFoundationWriteScope(config);
            ServerState.Config = config;
            WarnIfUnwiredOptions(config);

            D1WritePilotScope d1WritePilotScope = null;
            if (config.EnableD1WritePilotOrDefault && !config.ReadOnlyOrDefault)
            {
                d1WritePilotScope = new D1WritePilotScope(config.D1WriteExpectedDrawingPath,
                    config.D1WriteExpectedJournalScopeId, config.D1WriteExpectedJournalPathDigest);
            }

            // Open, lock, migrate, and recover before building a host that can
            // serve any MCP request. No in-memory production fallback exists.
            string stateDirectory = BimwrightAppRoot.Current.Dwg;
            string ledgerPath = Path.Combine(stateDirectory, "request-ledger.sqlite");
            using var ledger = new RequestLedger(ledgerPath);
            ledger.RecoverUnfinished();

            string ownershipPath = Path.Combine(stateDirectory, "bound-plugin-ownership.sqlite");
            using var ownershipStore = SqliteBoundPluginOwnershipStore.Open(ownershipPath);
            BoundPluginContextStore contextStore = ReconcileRequestLedgerAtStartup(ledger, ownershipStore);
            var executionGateway = new BoundPluginExecutionGateway(
                () => AuthToken.Resolve(ServerState.Config?.Target), contextStore, ledger: ledger,
                d1WritePilotScope: d1WritePilotScope,
                d1PreflightPilotScope: d1PreflightPilotScope,
                h1PreflightPilotScope: h1PreflightPilotScope, h1WritePilotScope: h1WritePilotScope, foundationWriteScope: foundationWriteScope);

            var enabled = config.EnableFoundationWriteOrDefault || config.EnableD1PreflightPilotOrDefault || config.EnableH1PreflightPilotOrDefault || config.EnableH1WritePilotOrDefault
                ? new HashSet<string>(new[] { "query" }, StringComparer.OrdinalIgnoreCase)
                : ToolsetFilter.Resolve(config);
            var registeredTools = CreateGovernedToolInstances(enabled, config.ReadOnlyOrDefault,
                config, d1WritePilotScope, d1PreflightPilotScope, h1PreflightPilotScope, h1WritePilotScope, foundationWriteScope);

            var builder = Host.CreateApplicationBuilder(args);
            // MCP stdio transport owns stdout. Route logs to stderr.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            var mcp = builder.Services
                .AddMcpServer(ConfigureMcpServerOptions)
                .WithStdioServerTransport();
            mcp = mcp.WithTools(registeredTools);

            using var host = builder.Build();
            BoundPluginExecutionGateway.InstallShared(executionGateway);
            try
            {
                await host.RunAsync();
            }
            finally
            {
                BoundPluginExecutionGateway.RemoveShared(executionGateway);
            }
        }

        internal static BoundPluginContextStore ReconcileRequestLedgerAtStartup(
            RequestLedger ledger, SqliteBoundPluginOwnershipStore ownershipStore)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (ownershipStore == null) throw new ArgumentNullException(nameof(ownershipStore));

            var contextStore = new BoundPluginContextStore(ownershipStore);
            var ownersByRequestId = new Dictionary<string, BoundPluginRequestOwnership>(StringComparer.Ordinal);
            var ledgerJobsByRequestId = new Dictionary<string, LedgerJob>(StringComparer.Ordinal);
            foreach (LedgerJob existingJob in ledger.ListJobsForStartupReconciliation())
            {
                if (!ledgerJobsByRequestId.TryAdd(existingJob.RequestId, existingJob))
                    throw new InvalidOperationException("Request ledger contains a duplicate request ID during startup reconciliation.");
            }

            foreach (BoundPluginRequestOwnership owner in contextStore.GetRequestOwnersForStartup())
            {
                bool isD1Write = owner.IsWrite;
                string mcpToolName;
                bool requestValid;
                if (isD1Write)
                {
                    bool foundation = owner.ToolName == FoundationDrawingPlan.PluginToolName;
                    bool h1 = owner.ToolName == H1FixedHatchWriteContract.PluginToolName;
                    mcpToolName = foundation ? FoundationDrawingPlan.McpToolName : h1 ? H1FixedHatchWriteContract.McpToolName : Road08R01D1WriteContract.McpToolName;
                    requestValid = (foundation || h1 || owner.ToolName == Road08R01D1WriteContract.PluginToolName)
                        && (foundation ? FoundationDrawingPlan.IsCanonicalValid(owner.CanonicalParametersJson) : owner.CanonicalParametersJson == (h1 ? H1FixedHatchWriteContract.CanonicalParametersJson : Road08R01D1WriteContract.CanonicalParametersJson))
                        && owner.RiskKeys.Count == 1
                        && contextStore.TryGetContext(owner.ContextId, out BoundPluginContext writeContext)
                        && writeContext.TryGetJournalScopeIdentity(required: true, out string scopeId,
                            out string pathDigest, out _)
                        && scopeId == owner.JournalScopeId && pathDigest == owner.JournalPathDigest
                        && writeContext.TryGetDrawingPathRiskKey(out string pathRiskKey, out _)
                        && owner.RiskKeys[0] == pathRiskKey
                        && string.Equals(writeContext.Discovery.Transport, "pipe", StringComparison.OrdinalIgnoreCase)
                        && writeContext.Discovery.AcadYear == 2026;
                }
                else
                {
                    requestValid = BoundPluginExecutionGateway.TryValidateGovernedPluginRequest(owner.ToolName,
                        owner.CanonicalParametersJson, out mcpToolName, out string validatedPluginToolName, out _)
                        && string.Equals(validatedPluginToolName, owner.ToolName, StringComparison.Ordinal);
                }

                if (!requestValid
                    || !contextStore.TryGetContext(owner.ContextId, out BoundPluginContext context)
                    || !string.Equals(context.CompleteTargetJson, owner.CompleteTargetJson, StringComparison.Ordinal)
                    || !context.TryGetOwnerIdentity(out BoundPluginOwnerIdentity identity, out string identityError)
                    || !string.Equals(identity.OwnerId, owner.OwnerId, StringComparison.Ordinal)
                    || !string.Equals(identity.ProviderGeneration, owner.ProviderGeneration, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Stored plugin ownership cannot be reconciled to the allowlisted read-only tools.");
                }

                if (!ownersByRequestId.TryAdd(owner.RequestId, owner))
                    throw new InvalidOperationException("Stored plugin ownership contains a duplicate request ID.");

                if (owner.IsWrite)
                {
                    if (!ledgerJobsByRequestId.TryGetValue(owner.RequestId, out LedgerJob existingWriteJob)
                        || !MatchesRecoveredLedgerJob(existingWriteJob, owner))
                        throw new InvalidOperationException("Stored D1 write ownership has no matching pre-existing request ledger row; startup is fail-closed.");

                    // The ownership store is query-only after restart. Keep the
                    // existing server row and its fence intact; D1 writes may
                    // not be recreated through the read-only recovery API.
                    continue;
                }

                var recovered = ledger.ReserveRecovered(new ReserveRequest(
                    owner.RequestId,
                    owner.ContextId,
                    mcpToolName!,
                    owner.CanonicalParametersJson,
                    owner.CompleteTargetJson,
                    IsWrite: owner.IsWrite,
                    ProviderGeneration: owner.ProviderGeneration,
                    RiskKeys: owner.RiskKeys,
                    GlobalFence: false));

                if (recovered.Disposition is not (ReserveDisposition.Created or ReserveDisposition.Existing)
                    || recovered.Job == null
                    || !MatchesRecoveredLedgerJob(recovered.Job, owner))
                {
                    throw new InvalidOperationException("Request ledger conflicts with immutable plugin ownership; startup is fail-closed.");
                }
            }

            foreach (LedgerJob job in ledger.ListJobsForStartupReconciliation())
            {
                if (ownersByRequestId.TryGetValue(job.RequestId, out BoundPluginRequestOwnership owner))
                {
                    if (!MatchesRecoveredLedgerJob(job, owner))
                        throw new InvalidOperationException("Request ledger conflicts with immutable plugin ownership; startup is fail-closed.");
                    continue;
                }

                if (!ledger.MarkUnavailable(job.RequestId, job.ProviderGeneration, job.PluginSequence))
                    throw new InvalidOperationException("An ownerless request ledger row changed during startup reconciliation.");
            }

            ledger.CompleteStartupReconciliation();
            return contextStore;
        }

        private static bool MatchesRecoveredLedgerJob(LedgerJob job, BoundPluginRequestOwnership owner)
        {
            string mcpToolName;
            if (owner.IsWrite)
            {
                bool foundation = owner.ToolName == FoundationDrawingPlan.PluginToolName;
                    bool h1 = owner.ToolName == H1FixedHatchWriteContract.PluginToolName;
                if ((!foundation && !h1 && owner.ToolName != Road08R01D1WriteContract.PluginToolName)
                    || !(foundation ? FoundationDrawingPlan.IsCanonicalValid(owner.CanonicalParametersJson) : owner.CanonicalParametersJson == (h1 ? H1FixedHatchWriteContract.CanonicalParametersJson : Road08R01D1WriteContract.CanonicalParametersJson))
                    || owner.RiskKeys.Count != 1)
                    return false;
                mcpToolName = foundation ? FoundationDrawingPlan.McpToolName : h1 ? H1FixedHatchWriteContract.McpToolName : Road08R01D1WriteContract.McpToolName;
            }
            else
            {
                if (!BoundPluginExecutionGateway.TryValidateGovernedPluginRequest(owner.ToolName,
                        owner.CanonicalParametersJson, out mcpToolName, out string validatedPluginToolName, out _)
                    || !string.Equals(validatedPluginToolName, owner.ToolName, StringComparison.Ordinal))
                    return false;
            }
            string ownerParameters = RequestLedger.CanonicalizeJsonObject(owner.CanonicalParametersJson, "canonicalParametersJson");
            string ownerTarget = RequestLedger.CanonicalizeJsonObject(owner.CompleteTargetJson, "completeTargetJson");
            string expectedPayloadDigest;
            try
            {
                expectedPayloadDigest = RequestLedger.ComputeExpectedPayloadDigest(new ReserveRequest(
                    owner.RequestId, owner.ContextId, mcpToolName, owner.CanonicalParametersJson,
                    owner.CompleteTargetJson, owner.IsWrite, owner.ProviderGeneration,
                    RiskKeys: owner.RiskKeys, GlobalFence: false));
            }
            catch
            {
                return false;
            }

            return string.Equals(job.RequestId, owner.RequestId, StringComparison.Ordinal)
                && string.Equals(job.ContextId, owner.ContextId, StringComparison.Ordinal)
                && string.Equals(job.ToolName, mcpToolName, StringComparison.Ordinal)
                && string.Equals(job.CanonicalParametersJson, ownerParameters, StringComparison.Ordinal)
                && string.Equals(job.CompleteTargetJson, ownerTarget, StringComparison.Ordinal)
                && string.Equals(job.PayloadDigest, expectedPayloadDigest, StringComparison.Ordinal)
                && job.IsWrite == owner.IsWrite && !job.GlobalFence
                && job.HeldRiskKeys.SequenceEqual(job.WriteFenceReleased
                    ? Array.Empty<string>() : owner.RiskKeys, StringComparer.Ordinal)
                && string.Equals(job.ProviderGeneration, owner.ProviderGeneration, StringComparison.Ordinal)
                && (!owner.IsWrite || IsConsistentRecoveredD1Write(job, owner));
        }

        private static bool IsConsistentRecoveredD1Write(LedgerJob job, BoundPluginRequestOwnership owner)
        {
            if (job.PluginSequence.HasValue && !owner.PluginSequence.HasValue)
                return false;
            if (job.PluginSequence.HasValue && owner.PluginSequence.HasValue
                && owner.PluginSequence.Value < job.PluginSequence.Value)
                return false;
            if (owner.PluginSequence.HasValue && !job.PluginSequence.HasValue && !job.ServerUnavailable)
                return false;
            if (owner.PluginSequence.HasValue && job.PluginSequence.HasValue
                && owner.PluginSequence.Value > job.PluginSequence.Value && !job.ServerUnavailable)
                return false;

            bool hasReadbackState = job.ReadbackRevision.HasValue || job.ReadbackDigest != null
                || job.VerificationStagedPluginSequence.HasValue || job.VerificationResultJson != null
                || job.VerificationErrorCode != null || job.VerificationErrorMessage != null
                || job.Observation == ObservationState.IndependentReadback;
            if (!hasReadbackState)
            {
                if (job.Verification != VerificationState.NotChecked)
                    return false;
                return !job.WriteFenceReleased
                    || (job.Effects == EffectState.None && job.Phase is JobPhase.Failed or JobPhase.Cancelled);
            }

            if (job.Phase != JobPhase.Completed || job.Effects != EffectState.Committed
                || !job.ReadbackRevision.HasValue || job.ReadbackRevision.Value < 0
                || !RequestLedger.IsValidSha256Hex(job.ReadbackDigest)
                || job.VerificationResultJson == null
                || !job.VerificationStagedPluginSequence.HasValue
                || !job.PluginSequence.HasValue)
                return false;

            bool recoveredStage = job.Observation == ObservationState.Recovery && job.ServerUnavailable;
            bool currentStage = job.Observation == ObservationState.IndependentReadback && !job.ServerUnavailable;
            if (!recoveredStage && !currentStage)
                return false;

            long stagedSequence = job.VerificationStagedPluginSequence.Value;
            if (stagedSequence < 0 || job.ReadbackRevision.Value != stagedSequence)
                return false;

            string expectedReadbackDigest;
            JObject stageResult;
            try
            {
                expectedReadbackDigest = RequestLedger.ComputeExpectedVerificationDigest(new VerificationRequest(
                    job.RequestId, job.ContextId, job.CompleteTargetJson, job.ProviderGeneration,
                    job.ReadbackRevision.Value, job.Verification, ResultJson: job.VerificationResultJson,
                    ErrorCode: job.VerificationErrorCode, ErrorMessage: job.VerificationErrorMessage,
                    ExpectedPluginSequence: stagedSequence));
                stageResult = JObject.Parse(job.VerificationResultJson);
            }
            catch
            {
                return false;
            }
            bool foundation = owner.ToolName == FoundationDrawingPlan.PluginToolName;
            if (!string.Equals(job.ReadbackDigest, expectedReadbackDigest, StringComparison.Ordinal)
                || stageResult.Value<string>("validator_version") != (foundation ? FoundationDrawingPlan.Validator : Road08R01D1WriteContract.ReadbackValidatorVersion)
                || stageResult["passed"]?.Type != JTokenType.Boolean
                || stageResult.Value<bool>("passed") != (job.Verification == VerificationState.Passed)
                || stageResult["source_handles"] is not JArray sourceHandles
                || !sourceHandles.Values<string>().SequenceEqual(
                    foundation ? Array.Empty<string>() : Road08R01D1WriteContract.SourceHandles, StringComparer.Ordinal)
                || !RequestLedger.IsValidSha256Hex(stageResult.Value<string>("plugin_readback_stage_digest"))
                || !RequestLedger.IsValidSha256Hex(stageResult.Value<string>("readback_sha256")))
                return false;

            if (job.Verification == VerificationState.Failed)
                return !job.WriteFenceReleased && job.PluginSequence.Value == stagedSequence;
            if (job.Verification != VerificationState.Passed)
                return false;
            if (job.WriteFenceReleased)
                return stagedSequence < long.MaxValue && job.PluginSequence.Value == stagedSequence + 1;
            return job.PluginSequence.Value == stagedSequence;
        }

        private static void ConfigureMcpServerOptions(ModelContextProtocol.Server.McpServerOptions opts)
        {
            opts.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "dwg-mcp",
                Title = "DWG MCP",
                Version = "2.0.1",
                Description = "Model Context Protocol gateway for Autodesk AutoCAD DWG workflows",
                WebsiteUrl = "https://github.com/bimwright/dwg-mcp"
            };
            opts.ServerInstructions = ServerState.Config?.EnableFoundationWriteOrDefault == true
                ? "CAD foundation first release: bind the exact owned working drawing with dwg_bind_active_document; inspect binding_snapshot and document_info. Reads: dwg_list_layers, dwg_get_entity_properties (1..64 explicit handles, include_geometry=true returns native entity_digest), dwg_query_entities (filtered model-space candidates/paging). The only drawing write is dwg_apply_plan: foundation-drawing-plan-v1, source_role, units=metres, 1..16 bounded steps. Supports 25 bounded operations: layer state, create/reuse-matching installed-font and dimension styles, line/circle/polyline, styled text/callout, new-boundary hatch, four native DIMENSION types, guarded closed-boundary/callout/content updates and supported complete-group move/rotate/scale/copy/archive. Catalog/layer digests are in document_info.foundation_catalog. Native text dimensions and paragraph extents are different; fit width before finite layout. Modified hatch area needs independent native read. Separate whole-document save performs specified-object disk verification. Provide context_id and a stable request_id, submit once. A timeout or ambiguous reply means query dwg_job_get for the original context/request; do not repeat the write or bypass fences. Both committed effects and passed verification with write_fence_released are required. Source drawings must be protected; use owned working copies. No arbitrary code, cross-drawing copies, generic delete/old-hatch repair, layout/plotting or automatic engineering decisions. Coordinates are 2D WCS metres; text rotation is degrees, hatch angle radians. Save affects the entire current drawing. Layer/state and complete logical associations are checked. Existing-object edits require fresh native digests and all associated members. Task cancellation is queued-only; unknown writes remain fenced. Bind-time metadata is an observation, not live monitoring."
                : ServerInstructionsText;
        }

        private const string ServerInstructionsText =
@"dwg-mcp - MCP gateway for Autodesk AutoCAD DWG drawings. The governed pilot exposes three general drawing reads: dwg_list_layers, dwg_get_entity_properties (explicit handles only, 1-64 per request), and dwg_query_entities (filtered model-space reads). When the isolated Road08 D1 pilot is explicitly enabled, dwg_d1_preflight_r01 adds one fixed read-only inspection for its configured drawing; it cannot approve or perform a write. Other drawing reads, drawing changes, view changes, file writes, batch execution, code execution, and baked-tool execution are not available in this pilot.

Bind explicitly before reading: call dwg_bind_active_document. It returns an opaque context_id for the exact document and plugin endpoint selected at bind time. Pass that context_id and a stable caller-generated request_id to each read tool. Reuse the same request_id only to inspect or cancel that exact job. dwg_job_get performs one immediate snapshot and accepts only wait_ms 0; dwg_job_cancel can cancel queued work only. dwg_job_get_historical reads an already-persisted dwg_list_layers request through a different explicitly bound pipe observer; it never resubmits, cancels, or takes over the old request. A communication timeout leaves execution state unknown; use dwg_job_get with the same context_id and request_id.

Tool availability follows --toolsets and --read-only. query can expose dwg_list_layers, dwg_get_entity_properties, dwg_query_entities, and local static AutoLISP inspection. Entity property batches support 1-64 explicit handles; oversized results carry a truncated marker. dwg_query_entities reads model space only and requires at least one entity_type, layer, or color_index filter; limit defaults to 500 and accepts only integers 1-5000. Its count is the number of returned rows and it does not provide total_count; has_more and truncated report whether a further matching entity was found by one-object lookahead. meta can expose target discovery and selection. When toolbaker is enabled and selected, only its local listing reads are available. The explicit binding and job controls are registered when query or meta is selected.

Target selection is a future-bind setting. Changing targets does not change an existing context_id or reroute its job queries.";

        public static IReadOnlyList<McpServerTool> CreateGovernedToolInstances(HashSet<string> enabled,
            bool readOnly, DwgMcpConfig config = null, D1WritePilotScope d1WritePilotScope = null,
            D1PreflightPilotScope d1PreflightPilotScope = null,
            H1PreflightPilotScope h1PreflightPilotScope = null, H1WritePilotScope h1WritePilotScope = null, FoundationWriteScope foundationWriteScope = null)
        {
            IReadOnlyList<Type> pinnedTypes = GetPinnedUpstreamToolTypes();
            GovernedToolFactory.ValidateExactCatalog(pinnedTypes);
            bool d1WritePilotEnabled = !readOnly && config?.EnableD1WritePilotOrDefault == true;
            bool d1PreflightOnlyEnabled = readOnly && config?.EnableD1PreflightPilotOrDefault == true;
            bool d1PreflightEnabled = d1WritePilotEnabled || d1PreflightOnlyEnabled;
            bool h1ReadEnabled = config?.EnableH1PreflightPilotOrDefault == true;
            bool h1WriteEnabled = config?.EnableH1WritePilotOrDefault == true;
            bool h1Enabled = h1ReadEnabled || h1WriteEnabled;
            bool foundationEnabled = config?.EnableFoundationWriteOrDefault == true;
            if (foundationEnabled && (readOnly || config.Target != "2026" || d1PreflightEnabled || h1Enabled || foundationWriteScope == null)) throw new InvalidOperationException("Foundation plan writes require one exclusive AutoCAD 2026 scope.");
            if (h1WriteEnabled && (readOnly || h1ReadEnabled || config.EnableD1WritePilotOrDefault || config.EnableD1PreflightPilotOrDefault || config.Target != "2026" || h1WritePilotScope == null))
                throw new InvalidOperationException("H1 write requires an exclusive exact AutoCAD 2026 write scope.");
            if (h1ReadEnabled && (!readOnly || config.EnableD1WritePilotOrDefault || config.EnableD1PreflightPilotOrDefault
                || h1PreflightPilotScope == null || config.Target != "2026"))
                throw new InvalidOperationException("H1 preflight requires an exclusive read-only AutoCAD 2026 scope.");
            if (config?.EnableD1PreflightPilotOrDefault == true && !readOnly)
                throw new InvalidOperationException("The D1 preflight pilot requires --read-only.");
            if (d1PreflightOnlyEnabled && !string.Equals(config.Target, "2026", StringComparison.Ordinal))
                throw new InvalidOperationException("The D1 preflight pilot requires --target 2026 and AutoCAD 2026 pipe transport.");
            if (d1WritePilotEnabled && d1WritePilotScope == null)
                throw new InvalidOperationException("D1 write pilot is enabled without an exact drawing and journal scope.");
            if (d1PreflightOnlyEnabled && d1PreflightPilotScope == null)
                throw new InvalidOperationException("D1 preflight pilot is enabled without an exact read-only drawing and journal scope.");

            HashSet<string> selectedToolsets = foundationEnabled || d1PreflightOnlyEnabled || h1Enabled
                ? new HashSet<string>(new[] { "query" }, StringComparer.OrdinalIgnoreCase)
                : enabled ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The write pilot may add its single fixed tool beside normal
            // reads. Neither D1 mode enables generic upstream write tools.
            var upstreamTools = GovernedToolFactory.Create(pinnedTypes, selectedToolsets,
                readOnly || d1WritePilotEnabled || h1WriteEnabled || foundationEnabled);
            if (foundationEnabled || d1PreflightOnlyEnabled || h1Enabled)
            {
                upstreamTools = upstreamTools
                    .Where(tool => !string.Equals(tool.ProtocolTool?.Name, "dwg_inspect_lisp", StringComparison.Ordinal))
                    .ToArray();
            }
            var registered = upstreamTools.ToList();

            if (selectedToolsets.Contains("query") || selectedToolsets.Contains("meta") || d1PreflightEnabled)
                registered.AddRange(FoundationExecutionTools.CreateToolInstances());
            if (d1PreflightEnabled)
                registered.AddRange(D1Road08PreflightTools.CreateToolInstances());
            if (h1Enabled && h1WritePilotScope?.J10Plan == null) registered.AddRange(H1FixedHatchPreflightTools.CreateToolInstances());
            if (h1WriteEnabled) registered.AddRange(H1FixedHatchWriteTools.CreateToolInstances());
            if (foundationEnabled) registered.AddRange(FoundationDrawingPlanTools.CreateToolInstances());
            if (d1WritePilotEnabled)
            {
                registered.AddRange(D1Road08WriteTools.CreateToolInstances());
            }

            ServerState.RegisteredTools = registered;
            ServerState.PluginCallableDrawingTools = upstreamTools
                .Where(tool =>
                {
                    var effect = ToolPolicyManifest.Require(tool.ProtocolTool.Name).Effect;
                    return effect == ToolEffectClass.DrawingRead || effect == ToolEffectClass.DrawingWrite;
                })
                .ToArray();
            return registered;
        }

        internal static D1PreflightPilotScope ResolveD1PreflightPilotScope(DwgMcpConfig config)
        {
            if (config?.EnableD1PreflightPilotOrDefault != true)
                return null;
            if (!config.ReadOnlyOrDefault)
                throw new InvalidOperationException("The D1 preflight pilot requires --read-only.");
            if (!string.Equals(config.Target, "2026", StringComparison.Ordinal))
                throw new InvalidOperationException("The D1 preflight pilot requires --target 2026 and AutoCAD 2026 pipe transport.");

            return new D1PreflightPilotScope(
                config.D1PreflightExpectedDrawingPath,
                config.D1PreflightExpectedJournalScopeId,
                config.D1PreflightExpectedJournalPathDigest);
        }

        internal static H1PreflightPilotScope ResolveH1PreflightPilotScope(DwgMcpConfig config)
        {
            if (config?.EnableH1PreflightPilotOrDefault != true) return null;
            if (!config.ReadOnlyOrDefault || config.Target != "2026"
                || config.EnableD1WritePilotOrDefault || config.EnableD1PreflightPilotOrDefault || config.EnableH1WritePilotOrDefault)
                throw new InvalidOperationException("H1 preflight requires an exclusive read-only AutoCAD 2026 scope.");
            return new H1PreflightPilotScope(config.H1ExpectedDrawingPath,
                config.H1ExpectedJournalScopeId, config.H1ExpectedJournalPathDigest);
        }

        internal static FoundationWriteScope ResolveFoundationWriteScope(DwgMcpConfig config)
        {
            if(config?.EnableFoundationWriteOrDefault!=true)return null;
            if(config.ReadOnlyOrDefault||config.Target!="2026"||config.EnableH1WritePilotOrDefault||config.EnableH1PreflightPilotOrDefault||config.EnableD1WritePilotOrDefault||config.EnableD1PreflightPilotOrDefault)throw new InvalidOperationException("Foundation write scope must be exclusive.");
            return new FoundationWriteScope(config.FoundationExpectedDrawingPath,config.FoundationExpectedJournalScopeId,config.FoundationExpectedJournalPathDigest);
        }

        internal static H1WritePilotScope ResolveH1WritePilotScope(DwgMcpConfig config)
        {
            if (config?.EnableH1WritePilotOrDefault != true)
            { if (!string.IsNullOrWhiteSpace(config?.H1J10PlanFile)) throw new InvalidOperationException("J10 plan requires explicit H1 write enablement."); return null; }
            if (config.ReadOnlyOrDefault || config.Target != "2026" || config.EnableH1PreflightPilotOrDefault || config.EnableD1PreflightPilotOrDefault || config.EnableD1WritePilotOrDefault)
                throw new InvalidOperationException("H1 write requires an exclusive AutoCAD 2026 scope and write mode.");
            return new H1WritePilotScope(config.H1ExpectedDrawingPath, config.H1ExpectedJournalScopeId, config.H1ExpectedJournalPathDigest,
                string.IsNullOrWhiteSpace(config.H1J10PlanFile) ? null : J10WritePilotPlan.Load(config.H1J10PlanFile));
        }

        public static IReadOnlyList<Type> GetPinnedUpstreamToolTypes()
        {
            return new Type[]
            {
                typeof(QueryTools), typeof(ModifyTools), typeof(MetaTools), typeof(BatchTools),
                typeof(ToolBakerTools), typeof(ToolBakerWriteTools), typeof(CodeTools),
                typeof(AnnotationTools), typeof(BlockTools), typeof(BlockWriteTools),
                typeof(DimensionTools), typeof(ViewTools), typeof(ExportTools),
                typeof(DrawingTools), typeof(DrawingWriteTools)
            };
        }

        // Retained as a safe projection for callers that inspect toolset groups.
        // MCP registration itself always goes through CreateGovernedToolInstances.
        private static IEnumerable<Type> ResolveToolTypesForRegistration(HashSet<string> enabled, bool readOnly)
        {
            enabled = enabled ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected = new List<Type>();
            if (enabled.Contains("query")) selected.Add(typeof(QueryTools));
            if (enabled.Contains("meta")) selected.Add(typeof(MetaTools));
            if (enabled.Contains("toolbaker")) selected.Add(typeof(ToolBakerTools));
            return selected;
        }

        private static void ValidateTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            AuthToken.NormalizeTarget(target);
        }

        public static string UnwiredOptionWarning(DwgMcpConfig config)
        {
            if (config != null && config.AllowLanBindOrDefault)
            {
                return "warning: --allow-lan-bind / BIMWRIGHT_DWG_ALLOW_LAN_BIND is parsed but plugin-side LAN binding is not yet implemented in v1.0; the AutoCAD plugin still listens on loopback only.";
            }

            return null;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(
                "dwg-mcp - MCP stdio gateway for Autodesk AutoCAD 2022-2027.\n" +
                "\n" +
                "Usage: dwg-mcp [options]\n" +
                "\n" +
                "Options:\n" +
                "  --target <year>        Pin to AutoCAD 2022|2023|2024|2025|2026|2027.\n" +
                "  --toolsets <csv>       Toolsets to register (default: query,create,view,meta).\n" +
                "                         'all' enables the full surface.\n" +
                "  --read-only            Strip every write/execute tool.\n" +
                "  --enable-d1-preflight-pilot / --disable-d1-preflight-pilot\n" +
                "  --enable-toolbaker / --disable-toolbaker\n" +
                "  --allow-lan-bind       Parsed but not yet implemented (loopback only).\n" +
                "  --log-level <level>    trace|debug|information|warning|error.\n" +
                "  --config <path>        JSON config file. Default:\n" +
                "                         %LOCALAPPDATA%\\Bimwright\\Dwg\\dwgmcp.config.json by default.\n" +
                "\n" +
                "Environment overrides: BIMWRIGHT_DWG_TARGET, BIMWRIGHT_DWG_TOOLSETS,\n" +
                "BIMWRIGHT_DWG_READ_ONLY, BIMWRIGHT_DWG_ENABLE_TOOLBAKER,\n" +
                "BIMWRIGHT_DWG_ENABLE_D1_PREFLIGHT_PILOT and BIMWRIGHT_DWG_D1_PREFLIGHT_EXPECTED_* ,\n" +
                "BIMWRIGHT_DWG_ALLOW_LAN_BIND, BIMWRIGHT_DWG_LOG_LEVEL,\n" +
                "BIMWRIGHT_DWG_ROOT (absolute writable Bimwright root; contains Dwg and Fonts).\n" +
                "\n" +
                "  -h, --help             Show this help and exit.");
        }

        private static void WarnIfUnwiredOptions(DwgMcpConfig config)
        {
            var warning = UnwiredOptionWarning(config);
            if (!string.IsNullOrEmpty(warning))
            {
                Console.Error.WriteLine(warning);
            }
        }
    }
}
