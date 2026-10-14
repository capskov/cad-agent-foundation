using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Dwg.Plugin.Cad;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server.Execution
{
    /// <summary>
    /// Exact saved drawing and plugin journal identity for the isolated
    /// read-only Road08 D1 preflight. This scope carries no write risk key.
    /// </summary>
    public sealed class D1PreflightPilotScope
    {
        public D1PreflightPilotScope(string expectedDrawingPath, string journalScopeId,
            string journalPathDigest)
        {
            if (!DrawingRiskKey.TryNormalizePath(expectedDrawingPath, out string drawingPathRiskKey)
                || !IsLocalDrivePath(drawingPathRiskKey))
                throw new ArgumentException("D1 preflight requires one explicit local saved-DWG path.", nameof(expectedDrawingPath));
            if (!Guid.TryParseExact(journalScopeId, "N", out Guid scopeGuid)
                || scopeGuid == Guid.Empty
                || !string.Equals(journalScopeId, scopeGuid.ToString("N"), StringComparison.Ordinal))
                throw new ArgumentException("D1 preflight requires the exact configured plugin journal scope ID.", nameof(journalScopeId));
            if (journalPathDigest == null || journalPathDigest.Length != 64
                || journalPathDigest.Any(character => !(character >= '0' && character <= '9')
                    && !(character >= 'A' && character <= 'F')))
                throw new ArgumentException("D1 preflight requires the exact uppercase SHA-256 journal path digest.", nameof(journalPathDigest));

            NormalizedDrawingPath = drawingPathRiskKey.Substring(5);
            JournalScopeId = journalScopeId;
            JournalPathDigest = journalPathDigest;
        }

        public string NormalizedDrawingPath { get; }
        public string JournalScopeId { get; }
        public string JournalPathDigest { get; }

        public bool Matches(BoundPluginContext context)
        {
            if (context == null
                || !string.Equals(context.Discovery.Transport, "pipe", StringComparison.OrdinalIgnoreCase)
                || context.Discovery.AcadYear != 2026
                || !string.Equals(context.Discovery.Target, "2026", StringComparison.Ordinal)
                || !context.TryGetJournalScopeIdentity(required: true, out string scopeId,
                    out string pathDigest, out _)
                || !string.Equals(scopeId, JournalScopeId, StringComparison.Ordinal)
                || !string.Equals(pathDigest, JournalPathDigest, StringComparison.Ordinal)
                || !context.TryGetDrawingPathRiskKey(out string drawingPathRiskKey, out _))
                return false;

            return string.Equals(drawingPathRiskKey.Substring(5), NormalizedDrawingPath, StringComparison.Ordinal);
        }

        private static bool IsLocalDrivePath(string normalizedRiskKey)
        {
            if (normalizedRiskKey == null || !normalizedRiskKey.StartsWith("PATH:", StringComparison.Ordinal))
                return false;
            string path = normalizedRiskKey.Substring(5);
            return path.Length >= 3
                && path[0] is >= 'A' and <= 'Z'
                && path[1] == ':' && path[2] == '\\';
        }
    }

    /// <summary>
    /// Executes the explicitly bound read-only drawing queries and their
    /// snapshot/cancel controls against the endpoint captured by bind.
    /// </summary>
    public sealed partial class BoundPluginExecutionGateway
    {
        public const string PilotMcpToolName = "dwg_list_layers";
        public const string PilotPluginToolName = "list_layers";
        public const string PilotCanonicalParameters = "{}";
        public const string EntityPropertiesMcpToolName = "dwg_get_entity_properties";
        public const string EntityPropertiesPluginToolName = "get_entity_properties";
        public const string QueryEntitiesMcpToolName = "dwg_query_entities";
        public const string QueryEntitiesPluginToolName = "query_entities";
        public const string D1PreflightMcpToolName = Road08R01D1WriteContract.PreflightMcpToolName;
        public const string D1PreflightPluginToolName = Road08R01D1WriteContract.PreflightPluginToolName;
        public const string D1PreflightCanonicalParameters = Road08R01D1WriteContract.PreflightCanonicalParametersJson;
        private const string QueueHaltedError = "plugin_execution_queue_halted";

        private readonly Func<DiscoveryInfo> _discoveryProvider;
        private readonly BoundPluginContextStore _contexts;
        private readonly RequestLedger _ledger;
        private readonly D1WritePilotScope _d1WritePilotScope;
        private readonly D1PreflightPilotScope _d1PreflightPilotScope;
        private readonly H1PreflightPilotScope _h1PreflightPilotScope;
        private readonly H1WritePilotScope _h1WritePilotScope;
        private readonly FoundationWriteScope _foundationWriteScope;
        private readonly Func<DiscoveryInfo, string, object, Task<McpResponse>> _send;
        private readonly object _catalogGate = new object();
        private readonly HashSet<string> _catalogPushedForEndpoints = new HashSet<string>(StringComparer.Ordinal);
        private static BoundPluginExecutionGateway _shared;

        public BoundPluginExecutionGateway(
            Func<DiscoveryInfo> discoveryProvider,
            BoundPluginContextStore contexts,
            Func<DiscoveryInfo, string, object, Task<McpResponse>> send = null,
            RequestLedger ledger = null,
            D1WritePilotScope d1WritePilotScope = null,
            D1PreflightPilotScope d1PreflightPilotScope = null,
            H1PreflightPilotScope h1PreflightPilotScope = null, H1WritePilotScope h1WritePilotScope = null, FoundationWriteScope foundationWriteScope = null)
        {
            _discoveryProvider = discoveryProvider ?? throw new ArgumentNullException(nameof(discoveryProvider));
            _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
            _ledger = ledger;
            _d1WritePilotScope = d1WritePilotScope;
            _d1PreflightPilotScope = d1PreflightPilotScope;
            _h1PreflightPilotScope = h1PreflightPilotScope;
            _h1WritePilotScope = h1WritePilotScope;
            _foundationWriteScope = foundationWriteScope;
            if (foundationWriteScope != null && (d1WritePilotScope != null || h1WritePilotScope != null || d1PreflightPilotScope != null || h1PreflightPilotScope != null)) throw new ArgumentException("Foundation writes require an exclusive scope.");
            if (h1WritePilotScope != null && (d1WritePilotScope != null || d1PreflightPilotScope != null || h1PreflightPilotScope != null))
                throw new ArgumentException("H1 write requires an exclusive pilot scope.");
            _send = send ?? ((discovery, cmd, parameters) => new PluginClient(() => discovery)
                .SendAsync(discovery, cmd, parameters));
        }

        public static BoundPluginExecutionGateway Shared => Volatile.Read(ref _shared)
            ?? throw new InvalidOperationException("Durable bound-plugin ownership has not been initialized.");

        public static void InstallShared(BoundPluginExecutionGateway gateway)
        {
            if (gateway == null) throw new ArgumentNullException(nameof(gateway));
            if (Interlocked.CompareExchange(ref _shared, gateway, null) != null)
                throw new InvalidOperationException("The bound-plugin execution gateway is already initialized.");
        }

        public static void RemoveShared(BoundPluginExecutionGateway gateway)
        {
            if (gateway != null)
                Interlocked.CompareExchange(ref _shared, null, gateway);
        }

        public async Task<string> BindActiveDocumentAsync()
        {
            DiscoveryInfo discovery;
            try
            {
                discovery = _discoveryProvider()?.Snapshot();
            }
            catch (Exception exception)
            {
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "plugin discovery error: " + exception.Message);
            }

            if (discovery == null)
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "plugin discovery is unavailable");

            var response = await _send(discovery, "bind_active_document", new { }).ConfigureAwait(false);
            if (response == null || !response.Ok)
                return Envelope(response?.Id, false, null, response?.Error ?? "plugin returned an empty bind response");

            JObject result = response.Result as JObject;
            TryGetString(result, "context_id", out string contextId);
            TryGetString(result, "complete_target_json", out string completeTargetJson);
            TryGetString(result, "journal_scope_id", out string journalScopeId);
            TryGetString(result, "journal_path_digest", out string journalPathDigest);
            if (!IsValidIdentifier(contextId) || string.IsNullOrWhiteSpace(completeTargetJson) || !IsJsonObject(completeTargetJson))
                return Envelope(response.Id, false, null, "plugin bind response did not contain a valid context_id and complete_target_json");

            var bound = new BoundPluginContext(contextId, completeTargetJson, discovery,
                journalScopeId, journalPathDigest);
            try
            {
                if (!_contexts.TryAdd(bound, out var storeError))
                    return Envelope(response.Id, false, null, storeError);
            }
            catch
            {
                return Envelope(response.Id, false, null, "bound context could not be durably recorded");
            }

            return Envelope(response.Id, true, new
            {
                context_id = bound.ContextId,
                binding_snapshot = CreateBindingSnapshot(bound),
                document_info = result["document_info"] is JObject documentInfo && documentInfo.Value<string>("schema")=="foundation-document-info-v1" ? documentInfo.DeepClone() : null
            }, null);
        }

        // This is an allowlisted projection of the captured binding, not a
        // fresh CAD observation. Never expose the endpoint token or journal.
        private static JObject CreateBindingSnapshot(BoundPluginContext bound)
        {
            var snapshot = new JObject
            {
                ["schema"] = "bimwright-binding-snapshot-v1",
                ["observation"] = "bind_time",
                ["reported_at_utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["identity_status"] = "unknown",
                ["owner_pid"] = JValue.CreateNull(),
                ["owner_started_at_utc"] = JValue.CreateNull(),
                ["owner_generation"] = JValue.CreateNull(),
                ["document_session_id"] = JValue.CreateNull(),
                ["database_session_id"] = JValue.CreateNull(),
                ["database_fingerprint"] = JValue.CreateNull(),
                ["drawing_path"] = JValue.CreateNull(),
                ["current_layout"] = JValue.CreateNull(),
                ["current_space"] = JValue.CreateNull(),
                ["document_read_only"] = JValue.CreateNull(),
                ["plugin_build"] = JValue.CreateNull()
            };

            // Legacy test/observer bindings remain usable, but their arbitrary
            // target JSON is not a source of public identity or private paths.
            using var text = new System.IO.StringReader(bound.CompleteTargetJson);
            using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
            JObject target = JObject.Load(reader);
            if (TryGetString(target, "schema", out string targetSchema)
                && targetSchema == "bimwright-acad26-document-target-v1"
                && bound.TryGetOwnerIdentity(out BoundPluginOwnerIdentity owner, out _))
            {
                snapshot["owner_pid"] = owner.ProcessId;
                snapshot["owner_started_at_utc"] = owner.ProcessStartedAtUtc.ToString("O", CultureInfo.InvariantCulture);
                snapshot["owner_generation"] = owner.ProviderGeneration;
                foreach (string name in new[] { "document_session_id", "database_session_id", "database_fingerprint" })
                {
                    if (TryGetString(target, name, out string value)
                        && Guid.TryParse(value, out Guid identifier) && identifier != Guid.Empty)
                        snapshot[name] = value;
                }
                if (TryGetString(target, "drawing_path", out string drawingPath)
                    && DrawingRiskKey.TryNormalizePath(drawingPath, out _))
                    snapshot["drawing_path"] = drawingPath;

                if (new[] { "document_session_id", "database_session_id", "database_fingerprint" }
                    .All(name => snapshot[name].Type == JTokenType.String))
                    snapshot["identity_status"] = "known";
            }
            snapshot["unknown_fields"] = new JArray(snapshot.Properties()
                .Where(property => property.Value.Type == JTokenType.Null)
                .Select(property => property.Name));
            return snapshot;
        }

        public Task<string> SubmitListLayersAsync(ToolInvocationEnvelope invocation, object inputParams, object pluginParams) =>
            SubmitNamedGovernedReadAsync(invocation, PilotMcpToolName, inputParams, pluginParams);

        public Task<string> SubmitEntityPropertiesAsync(ToolInvocationEnvelope invocation, object inputParams, object pluginParams) =>
            SubmitNamedGovernedReadAsync(invocation, EntityPropertiesMcpToolName, inputParams, pluginParams);

        public Task<string> SubmitQueryEntitiesAsync(ToolInvocationEnvelope invocation, object inputParams, object pluginParams) =>
            SubmitNamedGovernedReadAsync(invocation, QueryEntitiesMcpToolName, inputParams, pluginParams);

        private Task<string> SubmitNamedGovernedReadAsync(ToolInvocationEnvelope invocation,
            string expectedToolName, object inputParams, object pluginParams)
        {
            if (invocation == null || !string.Equals(invocation.ToolName, expectedToolName, StringComparison.Ordinal))
                return Task.FromResult(Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "the governed read invocation does not match its dedicated tool route"));
            return SubmitGovernedReadAsync(invocation, inputParams, pluginParams);
        }

        public static bool IsGovernedReadToolName(string toolName) =>
            string.Equals(toolName, PilotMcpToolName, StringComparison.Ordinal)
            || string.Equals(toolName, EntityPropertiesMcpToolName, StringComparison.Ordinal)
            || string.Equals(toolName, QueryEntitiesMcpToolName, StringComparison.Ordinal)
            || string.Equals(toolName, D1PreflightMcpToolName, StringComparison.Ordinal)
            || string.Equals(toolName, H1FixedHatchPreflightContract.McpToolName, StringComparison.Ordinal);

        public static bool TryValidateGovernedReadRequest(string toolName, string canonicalParametersJson,
            out string pluginToolName, out string error)
        {
            pluginToolName = null;
            error = "read tool or parameters are not allowlisted";
            if (string.Equals(toolName, H1FixedHatchPreflightContract.McpToolName, StringComparison.Ordinal))
            {
                if (canonicalParametersJson != H1FixedHatchPreflightContract.CanonicalParametersJson) return false;
                pluginToolName = H1FixedHatchPreflightContract.PluginToolName;
                error = null;
                return true;
            }
            if (string.Equals(toolName, PilotMcpToolName, StringComparison.Ordinal))
            {
                if (!string.Equals(canonicalParametersJson, PilotCanonicalParameters, StringComparison.Ordinal))
                    return false;
                pluginToolName = PilotPluginToolName;
                error = null;
                return true;
            }

            if (string.Equals(toolName, D1PreflightMcpToolName, StringComparison.Ordinal))
            {
                if (!string.Equals(canonicalParametersJson, D1PreflightCanonicalParameters, StringComparison.Ordinal))
                    return false;
                pluginToolName = D1PreflightPluginToolName;
                error = null;
                return true;
            }

            if (!string.Equals(toolName, EntityPropertiesMcpToolName, StringComparison.Ordinal))
            {
                if (!string.Equals(toolName, QueryEntitiesMcpToolName, StringComparison.Ordinal))
                    return false;

                return TryValidateGovernedPluginRequest(QueryEntitiesPluginToolName,
                    canonicalParametersJson, out _, out pluginToolName, out error);
            }

            return TryValidateGovernedPluginRequest(EntityPropertiesPluginToolName,
                canonicalParametersJson, out _, out pluginToolName, out error);
        }

        public static bool TryValidateGovernedPluginRequest(string pluginToolName,
            string canonicalParametersJson, out string mcpToolName, out string validatedPluginToolName,
            out string error)
        {
            mcpToolName = null;
            validatedPluginToolName = null;
            error = "read tool or parameters are not allowlisted";

            if (pluginToolName == H1FixedHatchPreflightContract.PluginToolName)
            {
                if (canonicalParametersJson != H1FixedHatchPreflightContract.CanonicalParametersJson) return false;
                mcpToolName = H1FixedHatchPreflightContract.McpToolName;
                validatedPluginToolName = H1FixedHatchPreflightContract.PluginToolName;
                error = null;
                return true;
            }

            if (string.Equals(pluginToolName, PilotPluginToolName, StringComparison.Ordinal))
            {
                if (!string.Equals(canonicalParametersJson, PilotCanonicalParameters, StringComparison.Ordinal))
                    return false;
                mcpToolName = PilotMcpToolName;
                validatedPluginToolName = PilotPluginToolName;
                error = null;
                return true;
            }

            if (string.Equals(pluginToolName, D1PreflightPluginToolName, StringComparison.Ordinal))
            {
                if (!string.Equals(canonicalParametersJson, D1PreflightCanonicalParameters, StringComparison.Ordinal))
                    return false;
                mcpToolName = D1PreflightMcpToolName;
                validatedPluginToolName = D1PreflightPluginToolName;
                error = null;
                return true;
            }

            if (string.Equals(pluginToolName, QueryEntitiesPluginToolName, StringComparison.Ordinal))
            {
                if (!TryValidateQueryEntitiesParameters(canonicalParametersJson, out _, out error))
                    return false;

                mcpToolName = QueryEntitiesMcpToolName;
                validatedPluginToolName = QueryEntitiesPluginToolName;
                error = null;
                return true;
            }

            if (!string.Equals(pluginToolName, EntityPropertiesPluginToolName, StringComparison.Ordinal)
                || !HandleBatchRequest.TryParseCanonicalJson(canonicalParametersJson, out _, out error))
                return false;

            try
            {
                string canonical = RequestLedger.CanonicalizeJsonObject(canonicalParametersJson, "canonicalParametersJson");
                if (!string.Equals(canonical, canonicalParametersJson, StringComparison.Ordinal))
                {
                    error = "entity property parameters are not canonical";
                    return false;
                }
            }
            catch
            {
                error = "entity property parameters are invalid";
                return false;
            }

            mcpToolName = EntityPropertiesMcpToolName;
            validatedPluginToolName = EntityPropertiesPluginToolName;
            error = null;
            return true;
        }

        private static bool TryCreateReadRequest(ToolInvocationEnvelope invocation, object inputParams,
            object pluginParams, out GovernedReadRequest request, out string error)
        {
            request = null;
            error = "read tool or parameters are not allowlisted";

            if (string.Equals(invocation.ToolName, PilotMcpToolName, StringComparison.Ordinal))
            {
                if (!IsEmptyObject(inputParams) || !IsEmptyObject(pluginParams)
                    || invocation.BusinessArguments == null || invocation.BusinessArguments.Count != 0)
                {
                    error = "list_layers accepts no business parameters";
                    return false;
                }

                request = new GovernedReadRequest(PilotMcpToolName, PilotPluginToolName, PilotCanonicalParameters);
                error = null;
                return true;
            }

            if (string.Equals(invocation.ToolName, H1FixedHatchPreflightContract.McpToolName, StringComparison.Ordinal))
            {
                if (!IsEmptyObject(inputParams) || !IsEmptyObject(pluginParams)
                    || invocation.BusinessArguments == null || invocation.BusinessArguments.Count != 0)
                { error = "h1_preflight_fixed accepts no business parameters"; return false; }
                request = new GovernedReadRequest(H1FixedHatchPreflightContract.McpToolName,
                    H1FixedHatchPreflightContract.PluginToolName, H1FixedHatchPreflightContract.CanonicalParametersJson);
                error = null;
                return true;
            }

            if (string.Equals(invocation.ToolName, D1PreflightMcpToolName, StringComparison.Ordinal))
            {
                if (!IsEmptyObject(inputParams) || !IsEmptyObject(pluginParams)
                    || invocation.BusinessArguments == null || invocation.BusinessArguments.Count != 0)
                {
                    error = "d1_preflight_r01 accepts no business parameters";
                    return false;
                }

                request = new GovernedReadRequest(D1PreflightMcpToolName,
                    D1PreflightPluginToolName, D1PreflightCanonicalParameters);
                error = null;
                return true;
            }

            if (string.Equals(invocation.ToolName, QueryEntitiesMcpToolName, StringComparison.Ordinal))
                return TryCreateQueryEntitiesReadRequest(invocation, inputParams, pluginParams, out request, out error);

            if (!string.Equals(invocation.ToolName, EntityPropertiesMcpToolName, StringComparison.Ordinal)
                || invocation.BusinessArguments == null
                || !invocation.BusinessArguments.TryGetValue("handles", out JsonElement handlesArgument)
                || handlesArgument.ValueKind != JsonValueKind.String)
                return false;

            foreach (string argumentName in invocation.BusinessArguments.Keys)
            {
                if (argumentName != "handles" && argumentName != "include_geometry")
                {
                    error = "get_entity_properties contains an unsupported business argument";
                    return false;
                }
            }

            bool includeGeometry = false;
            if (invocation.BusinessArguments.TryGetValue("include_geometry", out JsonElement geometryArgument))
            {
                if (geometryArgument.ValueKind != JsonValueKind.True
                    && geometryArgument.ValueKind != JsonValueKind.False)
                {
                    error = "include_geometry must be a boolean";
                    return false;
                }
                includeGeometry = geometryArgument.GetBoolean();
            }

            JArray handles;
            try { handles = JArray.Parse(handlesArgument.GetString()); }
            catch
            {
                error = "handles must be a JSON array of hexadecimal handles";
                return false;
            }

            var parameters = new JObject
            {
                ["handles"] = handles,
                ["include_geometry"] = includeGeometry
            };
            if (!HandleBatchRequest.TryCreate(parameters, out _, out error))
                return false;

            if (!TryConvertToObject(inputParams, out JObject inputObject)
                || !TryConvertToObject(pluginParams, out JObject pluginObject)
                || !JToken.DeepEquals(parameters, inputObject)
                || !JToken.DeepEquals(parameters, pluginObject))
            {
                error = "get_entity_properties arguments do not match the validated plugin parameters";
                return false;
            }

            string canonicalParameters;
            try
            {
                canonicalParameters = RequestLedger.CanonicalizeJsonObject(
                    parameters.ToString(Formatting.None), "canonicalParametersJson");
            }
            catch
            {
                error = "get_entity_properties parameters could not be canonicalized";
                return false;
            }

            if (!TryValidateGovernedReadRequest(EntityPropertiesMcpToolName, canonicalParameters,
                out string validatedPluginTool, out error))
                return false;

            request = new GovernedReadRequest(EntityPropertiesMcpToolName, validatedPluginTool, canonicalParameters);
            error = null;
            return true;
        }

        private static bool TryCreateQueryEntitiesReadRequest(ToolInvocationEnvelope invocation, object inputParams,
            object pluginParams, out GovernedReadRequest request, out string error)
        {
            request = null;
            error = "query_entities arguments are invalid";
            if (invocation?.BusinessArguments == null)
            {
                error = "query_entities arguments are missing";
                return false;
            }

            foreach (string argumentName in invocation.BusinessArguments.Keys)
            {
                if (argumentName != "entity_type" && argumentName != "layer" && argumentName != "color_index"
                    && argumentName != "min_x" && argumentName != "min_y" && argumentName != "max_x" && argumentName != "max_y"
                    && argumentName != "limit" && argumentName != "page_token" && argumentName != "include_geometry")
                {
                    error = "query_entities contains an unsupported business argument";
                    return false;
                }
            }

            var parameters = new JObject();
            if (!TryReadOptionalQueryString(invocation.BusinessArguments, "entity_type", parameters, out error)
                || !TryReadOptionalQueryString(invocation.BusinessArguments, "layer", parameters, out error)
                || !TryReadOptionalQueryInteger(invocation.BusinessArguments, "color_index", 1, 256,
                    "color_index must be an integer from 1 through 256", parameters, out error)
                || !TryReadOptionalQueryDouble(invocation.BusinessArguments, "min_x", parameters, out error)
                || !TryReadOptionalQueryDouble(invocation.BusinessArguments, "min_y", parameters, out error)
                || !TryReadOptionalQueryDouble(invocation.BusinessArguments, "max_x", parameters, out error)
                || !TryReadOptionalQueryDouble(invocation.BusinessArguments, "max_y", parameters, out error)
                || !TryReadOptionalQueryInteger(invocation.BusinessArguments, "limit", 1, 5000,
                    "limit must be an integer from 1 through 5000", parameters, out error))
                return false;

            int boundCount = CountProperties(parameters, "min_x", "min_y", "max_x", "max_y");
            if (boundCount != 0 && boundCount != 4)
            {
                error = "min_x, min_y, max_x, and max_y must be supplied together";
                return false;
            }
            if (boundCount == 4 && (!TryReadDouble(parameters["min_x"], out double minX)
                || !TryReadDouble(parameters["min_y"], out double minY)
                || !TryReadDouble(parameters["max_x"], out double maxX)
                || !TryReadDouble(parameters["max_y"], out double maxY)
                || minX >= maxX || minY >= maxY))
            {
                error = "rectangle bounds must be finite and min_x/min_y must be less than max_x/max_y";
                return false;
            }

            if (invocation.BusinessArguments.TryGetValue("page_token", out JsonElement pageTokenArgument)
                && pageTokenArgument.ValueKind != JsonValueKind.Null)
            {
                if (pageTokenArgument.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(pageTokenArgument.GetString())
                    || pageTokenArgument.GetString().Length > 4096)
                {
                    error = "page_token must be a non-empty string no longer than 4096 characters";
                    return false;
                }
                parameters["page_token"] = pageTokenArgument.GetString();
            }

            bool includeGeometry = false;
            if (invocation.BusinessArguments.TryGetValue("include_geometry", out JsonElement geometryArgument))
            {
                if (geometryArgument.ValueKind != JsonValueKind.True && geometryArgument.ValueKind != JsonValueKind.False)
                {
                    error = "include_geometry must be a boolean";
                    return false;
                }
                includeGeometry = geometryArgument.GetBoolean();
            }
            parameters["include_geometry"] = includeGeometry;

            if (parameters.Property("entity_type", StringComparison.Ordinal) == null
                && parameters.Property("layer", StringComparison.Ordinal) == null
                && parameters.Property("color_index", StringComparison.Ordinal) == null
                && boundCount == 0)
            {
                error = "query_entities requires at least one filter: entity_type, layer, color_index, or spatial bounds";
                return false;
            }

            if (!TryConvertToObject(inputParams, out JObject inputObject)
                || !TryConvertToObject(pluginParams, out JObject pluginObject)
                || !JToken.DeepEquals(parameters, inputObject)
                || !JToken.DeepEquals(parameters, pluginObject))
            {
                error = "query_entities arguments do not match the validated plugin parameters";
                return false;
            }

            string canonicalParameters;
            try
            {
                canonicalParameters = RequestLedger.CanonicalizeJsonObject(
                    parameters.ToString(Formatting.None), "canonicalParametersJson");
            }
            catch
            {
                error = "query_entities parameters could not be canonicalized";
                return false;
            }

            if (!TryValidateGovernedReadRequest(QueryEntitiesMcpToolName, canonicalParameters,
                out string validatedPluginTool, out error))
                return false;

            request = new GovernedReadRequest(QueryEntitiesMcpToolName, validatedPluginTool, canonicalParameters);
            error = null;
            return true;
        }

        private static bool TryReadOptionalQueryString(IReadOnlyDictionary<string, JsonElement> arguments,
            string propertyName, JObject parameters, out string error)
        {
            error = null;
            if (!arguments.TryGetValue(propertyName, out JsonElement argument) || argument.ValueKind == JsonValueKind.Null)
                return true;
            if (argument.ValueKind != JsonValueKind.String)
            {
                error = propertyName + " must be a string";
                return false;
            }

            string value = argument.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                parameters[propertyName] = value;
            return true;
        }

        private static bool TryReadOptionalQueryInteger(IReadOnlyDictionary<string, JsonElement> arguments,
            string propertyName, int minimum, int maximum, string rangeError, JObject parameters, out string error)
        {
            error = null;
            if (!arguments.TryGetValue(propertyName, out JsonElement argument) || argument.ValueKind == JsonValueKind.Null)
                return true;
            if (argument.ValueKind != JsonValueKind.Number || !argument.TryGetInt32(out int value)
                || value < minimum || value > maximum)
            {
                error = rangeError;
                return false;
            }

            parameters[propertyName] = value;
            return true;
        }

        private static bool TryReadOptionalQueryDouble(IReadOnlyDictionary<string, JsonElement> arguments,
            string propertyName, JObject parameters, out string error)
        {
            error = null;
            if (!arguments.TryGetValue(propertyName, out JsonElement argument) || argument.ValueKind == JsonValueKind.Null)
                return true;
            if (argument.ValueKind != JsonValueKind.Number || !argument.TryGetDouble(out double value)
                || double.IsNaN(value) || double.IsInfinity(value))
            {
                error = propertyName + " must be a finite number";
                return false;
            }

            parameters[propertyName] = value;
            return true;
        }

        private static bool TryValidateQueryEntitiesParameters(string canonicalParametersJson,
            out JObject parameters, out string error)
        {
            parameters = null;
            error = "query_entities parameters are invalid";
            try
            {
                parameters = JObject.Parse(canonicalParametersJson ?? string.Empty);
                string canonical = RequestLedger.CanonicalizeJsonObject(canonicalParametersJson, "canonicalParametersJson");
                if (!string.Equals(canonical, canonicalParametersJson, StringComparison.Ordinal))
                {
                    error = "query_entities parameters are not canonical";
                    return false;
                }
            }
            catch
            {
                error = "query_entities parameters are invalid";
                return false;
            }

            foreach (JProperty property in parameters.Properties())
            {
                if (property.Name != "entity_type" && property.Name != "layer" && property.Name != "color_index"
                    && property.Name != "min_x" && property.Name != "min_y" && property.Name != "max_x" && property.Name != "max_y"
                    && property.Name != "limit" && property.Name != "page_token" && property.Name != "include_geometry")
                {
                    error = "query_entities contains an unsupported parameter";
                    return false;
                }
            }

            foreach (string propertyName in new[] { "entity_type", "layer" })
            {
                JToken value = parameters[propertyName];
                if (value != null && (value.Type != JTokenType.String || string.IsNullOrWhiteSpace(value.Value<string>())))
                {
                    error = propertyName + " must be a non-empty string when supplied";
                    return false;
                }
            }

            if (parameters["color_index"] != null
                && (!TryReadInteger(parameters["color_index"], out int colorIndex) || colorIndex < 1 || colorIndex > 256))
            {
                error = "color_index must be an integer from 1 through 256";
                return false;
            }

            if (parameters["limit"] != null
                && (!TryReadInteger(parameters["limit"], out int limit) || limit < 1 || limit > 5000))
            {
                error = "limit must be an integer from 1 through 5000";
                return false;
            }

            int boundCount = CountProperties(parameters, "min_x", "min_y", "max_x", "max_y");
            if (boundCount != 0 && boundCount != 4)
            {
                error = "min_x, min_y, max_x, and max_y must be supplied together";
                return false;
            }
            if (boundCount == 4 && (!TryReadDouble(parameters["min_x"], out double minX)
                || !TryReadDouble(parameters["min_y"], out double minY)
                || !TryReadDouble(parameters["max_x"], out double maxX)
                || !TryReadDouble(parameters["max_y"], out double maxY)
                || minX >= maxX || minY >= maxY))
            {
                error = "rectangle bounds must be finite and min_x/min_y must be less than max_x/max_y";
                return false;
            }

            if (parameters["page_token"] != null
                && (parameters["page_token"].Type != JTokenType.String
                    || string.IsNullOrWhiteSpace(parameters.Value<string>("page_token"))
                    || parameters.Value<string>("page_token").Length > 4096))
            {
                error = "page_token must be a non-empty string no longer than 4096 characters";
                return false;
            }

            if (parameters["include_geometry"]?.Type != JTokenType.Boolean)
            {
                error = "include_geometry must be a boolean";
                return false;
            }

            if (parameters.Property("entity_type", StringComparison.Ordinal) == null
                && parameters.Property("layer", StringComparison.Ordinal) == null
                && parameters.Property("color_index", StringComparison.Ordinal) == null
                && boundCount == 0)
            {
                error = "query_entities requires at least one filter: entity_type, layer, color_index, or spatial bounds";
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryReadInteger(JToken token, out int value)
        {
            value = 0;
            if (token?.Type != JTokenType.Integer)
                return false;
            try
            {
                value = token.Value<int>();
                return true;
            }
            catch (Exception exception) when (exception is FormatException || exception is InvalidCastException || exception is OverflowException)
            {
                return false;
            }
        }

        private static bool TryReadDouble(JToken token, out double value)
        {
            value = 0d;
            if (token?.Type != JTokenType.Integer && token?.Type != JTokenType.Float)
                return false;
            try
            {
                value = token.Value<double>();
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch (Exception exception) when (exception is FormatException || exception is InvalidCastException || exception is OverflowException)
            {
                return false;
            }
        }

        private static int CountProperties(JObject value, params string[] names)
        {
            int count = 0;
            foreach (string name in names)
            {
                if (value.Property(name, StringComparison.Ordinal) != null) count++;
            }
            return count;
        }

        private static bool TryConvertToObject(object value, out JObject result)
        {
            result = null;
            try
            {
                JToken token = value as JToken ?? JToken.FromObject(value);
                if (token is not JObject obj)
                    return false;
                result = (JObject)obj.DeepClone();
                return true;
            }
            catch { return false; }
        }

        private async Task<string> SubmitGovernedReadAsync(ToolInvocationEnvelope invocation, object inputParams, object pluginParams)
        {
            if (invocation == null)
            {
                return Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "governed read invocation scope is missing or invalid");
            }
            if (!TryCreateReadRequest(invocation, inputParams, pluginParams,
                out GovernedReadRequest request, out string requestError))
            {
                return Envelope(Guid.NewGuid().ToString("N"), false, null, requestError);
            }
            if (!ServerState.IsToolRegistered(invocation.ToolName))
            {
                return Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "governed read tool is not registered");
            }

            BoundPluginContext bound = null;
            BoundPluginContext resolvedBound = null;
            RequestSubmissionLease reservation = null;
            BoundPluginOwnerIdentity identity = null;
            bool ledgerCreated = false;
            long? expectedLedgerSequence = null;
            try
            {
                if (!_contexts.TryGetContext(invocation.ContextId, out bound))
                    return Envelope(Guid.NewGuid().ToString("N"), false, null, "unknown context_id; bind the active document explicitly first");
                resolvedBound = bound;

                if (invocation.ToolName == H1FixedHatchPreflightContract.McpToolName && !MatchesH1PreflightScope(bound))
                    return Envelope(Guid.NewGuid().ToString("N"), false, null,
                        "the fixed H1 preflight requires its exact configured drawing and journal scope");

                if (string.Equals(invocation.ToolName, D1PreflightMcpToolName, StringComparison.Ordinal))
                {
                    string d1ScopeError = null;
                    if (!TryValidateD1PreflightBoundContext(bound, out _, out d1ScopeError))
                        return Envelope(Guid.NewGuid().ToString("N"), false, null,
                            d1ScopeError ?? "the fixed D1 preflight requires its exact configured drawing scope");
                }

                if (_ledger != null)
                {
                    if (!bound.TryGetOwnerIdentity(out identity, out _))
                        return Envelope(Guid.NewGuid().ToString("N"), false, null, "bound context owner identity is invalid");

                    ReserveResult ledgerReservation = _ledger.Reserve(new ReserveRequest(
                        invocation.RequestId, invocation.ContextId, request.McpToolName, request.CanonicalParametersJson,
                        bound.CompleteTargetJson, IsWrite: false, ProviderGeneration: identity.ProviderGeneration,
                        RiskKeys: Array.Empty<string>(), GlobalFence: false));
                    if (ledgerReservation.Disposition is not (ReserveDisposition.Created or ReserveDisposition.Existing)
                        || ledgerReservation.Job == null
                        || !MatchesLedgerJob(ledgerReservation.Job, bound, invocation.RequestId,
                            request.McpToolName, request.CanonicalParametersJson))
                        return Envelope(Guid.NewGuid().ToString("N"), false, null, "request reservation conflicts with an existing immutable request");
                    ledgerCreated = ledgerReservation.Disposition == ReserveDisposition.Created;
                    expectedLedgerSequence = ledgerReservation.Job.PluginSequence;
                }

                if (!_contexts.TryReserveRequest(invocation.RequestId, invocation.ContextId,
                    request.PluginToolName, request.CanonicalParametersJson, out BoundPluginContext reservedBound,
                    out reservation, out string ownerError,
                    allowCreate: _ledger == null || ledgerCreated))
                {
                    if (ledgerCreated && identity != null)
                        TryMarkReservationLedgerUnavailable(invocation.RequestId, identity.ProviderGeneration,
                            expectedLedgerSequence, resolvedBound, request.McpToolName, request.CanonicalParametersJson);
                    return Envelope(Guid.NewGuid().ToString("N"), false, null, ownerError);
                }
                bound = reservedBound;
            }
            catch
            {
                if (ledgerCreated && identity != null)
                    TryMarkReservationLedgerUnavailable(invocation.RequestId, identity.ProviderGeneration,
                        expectedLedgerSequence, resolvedBound, request.McpToolName, request.CanonicalParametersJson);
                // A storage failure must happen before catalog or job_submit I/O.
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "durable request reservation failed; no plugin request was sent");
            }

            await EnsurePluginCatalogAsync(bound.Discovery).ConfigureAwait(false);

            var submitPayload = new
            {
                request_id = invocation.RequestId,
                context_id = invocation.ContextId,
                tool_name = request.PluginToolName,
                canonical_parameters_json = request.CanonicalParametersJson
            };
            McpResponse submitted;
            try
            {
                submitted = await _send(bound.Discovery, "job_submit", submitPayload).ConfigureAwait(false);
            }
            catch
            {
                TryMarkLedgerUnavailable(invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                TryCompleteReservation(reservation, RequestSubmissionOutcome.OutcomeUnknown,
                    invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                return Envelope(Guid.NewGuid().ToString("N"), true, new
                {
                    request_id = invocation.RequestId,
                    context_id = invocation.ContextId,
                    status = "unknown",
                    pending = (bool?)null,
                    message = "The submit call failed before its outcome could be confirmed. The job may have been accepted; use job_get with this context_id and request_id."
                }, "plugin communication state is unknown");
            }

            if (submitted == null || !submitted.Ok)
            {
                TryMarkLedgerUnavailable(invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                if (IsTransportUncertain(submitted?.Error))
                {
                    TryCompleteReservation(reservation, RequestSubmissionOutcome.OutcomeUnknown,
                        invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                    return Envelope(submitted?.Id, true, new
                    {
                        request_id = invocation.RequestId,
                        context_id = invocation.ContextId,
                        status = "unknown",
                        pending = (bool?)null,
                        message = string.Equals(submitted?.Error, QueueHaltedError, StringComparison.Ordinal)
                            ? "The plugin reported a halted queue while acknowledging the submission. The job may have been accepted; use job_get with this context_id and request_id."
                            : "The submit response was lost. The job may have been accepted; use job_get with this context_id and request_id."
                    }, "plugin communication state is unknown");
                }

                TryCompleteReservation(reservation, RequestSubmissionOutcome.OutcomeUnknown,
                    invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                return Envelope(submitted?.Id, false, null, submitted?.Error ?? "plugin returned an empty job_submit response");
            }

            JObject submitResult = submitted.Result as JObject;
            TryGetString(submitResult, "disposition", out string disposition);
            JObject submittedJob = submitResult?["job"] as JObject;
            if (string.Equals(disposition, "request_id_conflict", StringComparison.Ordinal)
                || string.Equals(disposition, "fence_conflict", StringComparison.Ordinal))
            {
                TryMarkLedgerUnavailable(invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                TryCompleteReservation(reservation, RequestSubmissionOutcome.OutcomeUnknown,
                    invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                return Envelope(submitted.Id, true, new
                {
                    request_id = invocation.RequestId,
                    context_id = invocation.ContextId,
                    disposition,
                    job = (JObject)null
                }, null);
            }

            if (!string.Equals(disposition, "created", StringComparison.Ordinal)
                && !string.Equals(disposition, "existing", StringComparison.Ordinal))
            {
                TryMarkLedgerUnavailable(invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                TryCompleteReservation(reservation, RequestSubmissionOutcome.OutcomeUnknown,
                    invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                return Envelope(submitted.Id, true, new
                {
                    request_id = invocation.RequestId,
                    context_id = invocation.ContextId,
                    disposition = disposition ?? "unknown",
                    status = "unknown",
                    pending = (bool?)null,
                    message = "The plugin returned an unrecognized submission disposition. The job may have been accepted; use job_get with this context_id and request_id."
                }, "plugin execution state is unknown");
            }

            if (!TryAcceptPluginSnapshot(invocation.RequestId, bound, submittedJob,
                expectedLedgerSequence, out string snapshotError))
            {
                TryCompleteReservation(reservation, RequestSubmissionOutcome.OutcomeUnknown,
                    invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                return Envelope(submitted.Id, true, new
                {
                    request_id = invocation.RequestId,
                    context_id = invocation.ContextId,
                    status = "unknown",
                    pending = (bool?)null,
                    message = "The plugin returned a snapshot that did not match this request. The job may have been accepted; use job_get with this context_id and request_id."
                }, snapshotError ?? "plugin job snapshot could not be accepted");
            }

            if (TryGetInt64(submittedJob, "plugin_sequence", out long acceptedSubmitSequence))
                expectedLedgerSequence = acceptedSubmitSequence;
            if (!TryCompleteReservation(reservation, RequestSubmissionOutcome.Confirmed,
                invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence))
            {
                return Envelope(submitted.Id, true, BuildJobView(invocation.RequestId, invocation.ContextId,
                    disposition, submittedJob, stateUnavailable: true), null);
            }

            expectedLedgerSequence = CaptureLedgerSequence(invocation.RequestId, bound);

            var getPayload = new { request_id = invocation.RequestId, wait_ms = 0 };
            McpResponse snapshotResponse;
            try { snapshotResponse = await _send(bound.Discovery, "job_get", getPayload).ConfigureAwait(false); }
            catch
            {
                TryMarkLedgerUnavailable(invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                return Envelope(submitted.Id, true, BuildJobView(invocation.RequestId, invocation.ContextId,
                    disposition, submittedJob, stateUnavailable: true), null);
            }
            if (snapshotResponse == null || !snapshotResponse.Ok)
            {
                TryMarkLedgerUnavailable(invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                return Envelope(submitted.Id, true, BuildJobView(
                    invocation.RequestId,
                    invocation.ContextId,
                    disposition,
                    submittedJob,
                    stateUnavailable: true), null);
            }

            JObject getResult = snapshotResponse.Result as JObject;
            JObject job = getResult?["job"] as JObject;
            if (!TryGetBoolean(getResult, "found", out bool found) || !found
                || !TryAcceptPluginSnapshot(invocation.RequestId, bound, job, expectedLedgerSequence, out _))
            {
                TryMarkLedgerUnavailable(invocation.RequestId, identity?.ProviderGeneration, expectedLedgerSequence);
                return Envelope(submitted.Id, true, BuildJobView(
                    invocation.RequestId,
                    invocation.ContextId,
                    disposition,
                    submittedJob,
                    stateUnavailable: true), null);
            }

            return Envelope(submitted.Id, true, BuildJobView(
                invocation.RequestId,
                invocation.ContextId,
                disposition,
                job,
                stateUnavailable: false), null);
        }

        public async Task<string> GetJobAsync(string contextId, string requestId, int waitMs)
        {
            if (_contexts.TryGetOwnedRequest(requestId, contextId, requireConfirmed: false,
                    out _, out BoundPluginRequestOwnership routedOwner)
                && routedOwner.IsWrite)
                return routedOwner.ToolName == H1FixedHatchWriteContract.PluginToolName
                    ? await GetH1WriteJobAsync(contextId, requestId, waitMs).ConfigureAwait(false)
                    : await GetD1WriteJobAsync(contextId, requestId, waitMs).ConfigureAwait(false);

            if (waitMs != 0)
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "job_get supports only wait_ms 0 in the governed read surface");
            if (!_contexts.TryGetOwnedRequest(requestId, contextId, requireConfirmed: false,
                out var bound, out var ownership))
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "request_id is not owned by this context_id");
            long? expectedLedgerSequence = CaptureLedgerSequence(requestId, bound);

            McpResponse response;
            try { response = await _send(bound.Discovery, "job_get", new { request_id = requestId, wait_ms = 0 }).ConfigureAwait(false); }
            catch
            {
                TryMarkLedgerUnavailable(requestId, ownership.ProviderGeneration, expectedLedgerSequence);
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "plugin communication state is unknown");
            }
            if (response == null || !response.Ok)
            {
                TryMarkLedgerUnavailable(requestId, ownership.ProviderGeneration, expectedLedgerSequence);
                return Envelope(response?.Id, false, null, response?.Error ?? "plugin returned an empty job_get response");
            }

            JObject result = response.Result as JObject;
            JObject job = result?["job"] as JObject;
            if (!TryGetBoolean(result, "found", out bool found))
            {
                TryMarkLedgerUnavailable(requestId, ownership.ProviderGeneration, expectedLedgerSequence);
                return Envelope(response.Id, false, null, "plugin returned an invalid job_get found flag");
            }
            if (!found)
            {
                TryMarkLedgerUnavailable(requestId, ownership.ProviderGeneration, expectedLedgerSequence);
                return Envelope(response.Id, true, new { found = false, job = (JObject)null }, null);
            }
            if (!TryAcceptPluginSnapshot(requestId, bound, job, expectedLedgerSequence, out _))
                return Envelope(response.Id, false, null, "plugin job snapshot could not be accepted for this request");
            if (found)
                _contexts.MarkRequestOutcome(requestId, contextId, confirmed: true);

            return Envelope(response.Id, true, new
            {
                found,
                job = found ? ProjectJobSnapshot(job) : null
            }, null);
        }

        /// <summary>
        /// Reads an old request from the journal through a separately bound
        /// observer. The source owner and ledger generation remain immutable;
        /// only the observer endpoint is used for this single control query.
        /// </summary>
        public async Task<string> GetHistoricalJobAsync(string contextId, string requestId, string observerContextId)
        {
            if (_ledger == null
                || !IsValidIdentifier(contextId)
                || !IsValidIdentifier(requestId)
                || !IsValidIdentifier(observerContextId)
                || string.Equals(contextId, observerContextId, StringComparison.Ordinal)
                || !_contexts.TryGetHistoricalQueryOwner(requestId, contextId,
                    out BoundPluginContext source, out BoundPluginRequestOwnership ownership))
                return Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "historical query requires a persisted request and a distinct explicitly bound observer context");

            if (ownership.State == BoundPluginRequestState.Reserved
                || !source.TryGetOwnerIdentity(out BoundPluginOwnerIdentity sourceIdentity, out _)
                || !string.Equals(ownership.ToolName, PilotPluginToolName, StringComparison.Ordinal)
                || !string.Equals(ownership.CanonicalParametersJson, PilotCanonicalParameters, StringComparison.Ordinal)
                || !string.Equals(ownership.CompleteTargetJson, source.CompleteTargetJson, StringComparison.Ordinal)
                || !string.Equals(ownership.OwnerId, sourceIdentity.OwnerId, StringComparison.Ordinal)
                || !string.Equals(ownership.ProviderGeneration, sourceIdentity.ProviderGeneration, StringComparison.Ordinal)
                || !string.Equals(ownership.PayloadDigest, SqliteBoundPluginOwnershipStore.CreatePayloadDigest(ownership), StringComparison.Ordinal)
                || !TryGetMatchingLedgerJob(requestId, source, out LedgerJob sourceLedger))
                return Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "persisted historical request identity is invalid");

            long? expectedSequence = sourceLedger.PluginSequence;
            if (!_contexts.TryGetContext(observerContextId, out BoundPluginContext observer)
                || observer == null
                || !observer.TryGetOwnerIdentity(out BoundPluginOwnerIdentity observerIdentity, out _)
                || !string.Equals(observer.Discovery.Transport, "pipe", StringComparison.OrdinalIgnoreCase))
            {
                TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration, expectedSequence, source);
                return HistoricalUnknown(requestId, contextId, found: null, "observer_endpoint_unavailable");
            }

            McpResponse response;
            try
            {
                response = await _send(observer.Discovery, "job_get_historical", new { request_id = requestId })
                    .ConfigureAwait(false);
            }
            catch
            {
                TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration, expectedSequence, source);
                return HistoricalUnknown(requestId, contextId, found: null, "historical_endpoint_unavailable");
            }

            if (response == null || !response.Ok || response.Result is not JObject result
                || !TryGetBoolean(result, "found", out bool found)
                || !MatchesObserverIdentity(result["observer_identity"] as JObject, observerIdentity)
                || !TryGetHistoricalLiveness(result, out string liveness))
            {
                TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration, expectedSequence, source);
                return HistoricalUnknown(requestId, contextId, found: null, "historical_response_unavailable");
            }

            JToken jobToken = result["job"];
            if (!found)
            {
                if (jobToken != null && jobToken.Type != JTokenType.Null)
                {
                    TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration, expectedSequence, source);
                    return HistoricalUnknown(requestId, contextId, found: null, "historical_response_invalid");
                }
                TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration, expectedSequence, source);
                return HistoricalUnknown(requestId, contextId, found: false, "historical_snapshot_not_found");
            }

            if (jobToken is not JObject snapshot
                || !MatchesHistoricalSnapshot(snapshot, requestId, ownership, source, sourceIdentity)
                || !TryMapGovernedReadSnapshot(snapshot, out AdvanceRequest advance, out _))
            {
                TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration, expectedSequence, source);
                return HistoricalUnknown(requestId, contextId, found: null, "historical_snapshot_identity_mismatch");
            }

            if (!_contexts.TryRecordPluginSnapshot(requestId, snapshot, out _))
            {
                TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration, expectedSequence, source);
                return HistoricalUnknown(requestId, contextId, found: null, "historical_snapshot_sequence_rejected");
            }

            AdvanceResult mirrored;
            try
            {
                mirrored = _ledger.AdvanceIfCurrentSequence(advance with
                {
                    RequestId = requestId,
                    ProviderGeneration = sourceIdentity.ProviderGeneration
                }, expectedSequence);
            }
            catch
            {
                TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration, expectedSequence, source);
                return HistoricalUnknown(requestId, contextId, found: null, "historical_ledger_mirror_failed");
            }

            if (mirrored.Disposition is not (AdvanceDisposition.Applied or AdvanceDisposition.Duplicate)
                || mirrored.Job == null)
            {
                TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration, expectedSequence, source);
                return HistoricalUnknown(requestId, contextId, found: null, "historical_ledger_mirror_rejected");
            }

            bool terminal = IsTerminal(advance.Phase);
            bool uncertainLiveness = !terminal && liveness is "indeterminate" or "confirmed_lost"
                || advance.Phase == JobPhase.Unknown;
            LedgerJob visibleJob = mirrored.Job;
            if (uncertainLiveness)
            {
                TryMarkLedgerUnavailable(requestId, sourceIdentity.ProviderGeneration,
                    visibleJob.PluginSequence, source);
                return HistoricalUnknown(requestId, contextId, found: true, "old_owner_state_unavailable", liveness,
                    visibleJob.PluginSequence);
            }

            return Envelope(response.Id, true, new
            {
                request_id = requestId,
                context_id = contextId,
                found = true,
                available = true,
                status = ToPublicStatus(advance.Phase),
                phase = ToPublicPhase(advance.Phase),
                pending = IsPending(advance.Phase),
                old_owner_liveness = liveness,
                liveness_scope = "query_time_only",
                job = ProjectHistoricalSnapshot(snapshot)
            }, null);
        }

        public async Task<string> CancelJobAsync(string contextId, string requestId)
        {
            if (_contexts.TryGetOwnedRequest(requestId, contextId, requireConfirmed: false,
                    out _, out BoundPluginRequestOwnership routedOwner)
                && routedOwner.IsWrite)
                return routedOwner.ToolName == H1FixedHatchWriteContract.PluginToolName
                    ? Envelope(Guid.NewGuid().ToString("N"), false, null, "H1 cancellation is disabled; query the original request.")
                    : await CancelD1WriteJobAsync(contextId, requestId).ConfigureAwait(false);

            if (!_contexts.TryGetOwnedRequest(requestId, contextId, requireConfirmed: true,
                out var bound, out var ownership) || ownership.RecoveryQueryOnly)
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "request_id is not owned by this context_id");
            long? expectedLedgerSequence = CaptureLedgerSequence(requestId, bound);

            McpResponse response;
            try { response = await _send(bound.Discovery, "job_cancel", new { request_id = requestId }).ConfigureAwait(false); }
            catch
            {
                TryMarkLedgerUnavailable(requestId, ownership.ProviderGeneration, expectedLedgerSequence);
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "plugin communication state is unknown");
            }
            if (response == null || !response.Ok)
            {
                TryMarkLedgerUnavailable(requestId, ownership.ProviderGeneration, expectedLedgerSequence);
                return Envelope(response?.Id, false, null, response?.Error ?? "plugin returned an empty job_cancel response");
            }

            JObject result = response.Result as JObject;
            JObject job = result?["job"] as JObject;
            if (!TryGetString(result, "disposition", out string disposition)
                || !TryGetOptionalString(result, "reason", out string reason))
            {
                TryMarkLedgerUnavailable(requestId, ownership.ProviderGeneration, expectedLedgerSequence);
                return Envelope(response.Id, false, null, "plugin returned an invalid job_cancel response");
            }

            JToken jobToken = result?["job"];
            if (jobToken != null && jobToken.Type != JTokenType.Null && job == null)
            {
                TryMarkLedgerUnavailable(requestId, ownership.ProviderGeneration, expectedLedgerSequence);
                return Envelope(response.Id, false, null, "plugin returned an invalid job_cancel snapshot");
            }
            if (job == null)
                TryMarkLedgerUnavailable(requestId, ownership.ProviderGeneration, expectedLedgerSequence);
            else if (!TryAcceptPluginSnapshot(requestId, bound, job, expectedLedgerSequence, out _))
                return Envelope(response.Id, false, null, "plugin job snapshot could not be accepted for this request");

            // The plugin is authoritative for whether queued-only cancellation
            // succeeded. Running and terminal jobs retain their actual state.
            return Envelope(response.Id, true, new
            {
                disposition,
                reason,
                job = ProjectJobSnapshot(job)
            }, null);
        }

        private async Task<bool> EnsurePluginCatalogAsync(DiscoveryInfo discovery)
        {
            var key = EndpointKey(discovery);
            lock (_catalogGate)
            {
                if (_catalogPushedForEndpoints.Contains(key))
                    return true;
            }

            try
            {
                JObject payload = ToolCatalogBuilder.Build(ServerState.PluginCallableDrawingTools);
                var response = await _send(discovery, "set_tool_catalog", payload).ConfigureAwait(false);
                if (response != null && response.Ok)
                {
                    lock (_catalogGate)
                        _catalogPushedForEndpoints.Add(key);
                    return true;
                }
                ServerLogger.LogFinish("catalog", "set_tool_catalog", false, 0, response?.Error);
                return false;
            }
            catch (Exception exception)
            {
                ServerLogger.LogFinish("catalog", "set_tool_catalog", false, 0, exception.Message);
                return false;
            }
        }

        private static object BuildJobView(string requestId, string contextId, string disposition, JObject job, bool stateUnavailable)
        {
            if (stateUnavailable)
            {
                return new
                {
                    request_id = requestId,
                    context_id = contextId,
                    disposition,
                    status = "unknown",
                    pending = (bool?)null,
                    job = ProjectJobSnapshot(job),
                    message = "The job was accepted, but an immediate snapshot was unavailable. Use dwg_job_get with the same context_id and request_id."
                };
            }

            TryGetString(job, "phase", out string phase);
            phase = phase ?? "unknown";
            bool? pending = phase == "queued" || phase == "running" || phase == "cancellation_requested"
                ? true
                : (bool?)false;
            string status = pending == true ? "pending" : phase;
            JToken result = null;
            string error = null;
            if (string.Equals(phase, "completed", StringComparison.Ordinal))
            {
                TryGetString(job, "result_json", out string resultJson);
                if (!string.IsNullOrWhiteSpace(resultJson))
                {
                    try { result = JToken.Parse(resultJson); }
                    catch { result = resultJson; }
                }
            }
            else if (string.Equals(phase, "failed", StringComparison.Ordinal))
            {
                if (!TryGetString(job, "error_message", out error))
                    TryGetString(job, "error_code", out error);
            }

            return new
            {
                request_id = requestId,
                context_id = contextId,
                disposition,
                status,
                phase,
                pending,
                result,
                error,
                job = ProjectJobSnapshot(job)
            };
        }

        private static JObject ProjectJobSnapshot(JObject job)
        {
            if (job == null)
                return null;

            var projected = new JObject();
            foreach (string field in new[]
            {
                "request_id", "context_id", "tool_name", "phase", "effects", "verification", "observation",
                "created_at_utc", "updated_at_utc", "queue_deadline_utc", "result_json", "error_code", "error_message",
                "verification_result_json", "verification_error_code", "verification_error_message"
            })
            {
                CopyOptionalString(job, projected, field);
            }

            if (TryGetInt64(job, "plugin_sequence", out long sequence))
                projected["plugin_sequence"] = sequence;

            return projected;
        }

        private static JObject ProjectHistoricalSnapshot(JObject job)
        {
            // Reuse the explicit public job allowlist. Ownership, endpoint,
            // target, and digest fields are intentionally never projected.
            return ProjectJobSnapshot(job);
        }

        private static bool MatchesHistoricalSnapshot(JObject job, string requestId,
            BoundPluginRequestOwnership ownership, BoundPluginContext source, BoundPluginOwnerIdentity identity)
        {
            if (!MatchesPilotJob(job, source, requestId)
                || !TryGetString(job, "payload_digest", out string payloadDigest)
                || !TryGetString(job, "owner_id", out string ownerId)
                || !TryGetString(job, "provider_generation", out string generation)
                || !OwnerGuidEquals(ownerId, identity.OwnerId)
                || !string.Equals(generation, identity.ProviderGeneration, StringComparison.Ordinal)
                || !string.Equals(payloadDigest, ComputePluginSubmissionDigest(requestId, source, identity), StringComparison.Ordinal)
                || job["is_write"]?.Type != JTokenType.Boolean || job.Value<bool>("is_write")
                || job["global_fence"]?.Type != JTokenType.Boolean || job.Value<bool>("global_fence")
                || job["write_fence_released"]?.Type != JTokenType.Boolean || job.Value<bool>("write_fence_released")
                || job["held_risk_keys"] is not JArray riskKeys || riskKeys.Count != 0
                || job["queue_deadline_utc"] == null || job["queue_deadline_utc"].Type != JTokenType.Null
                || job["readback_revision"] == null || job["readback_revision"].Type != JTokenType.Null
                || job["readback_digest"] == null || job["readback_digest"].Type != JTokenType.Null
                || job["verification_result_json"] == null || job["verification_result_json"].Type != JTokenType.Null
                || job["verification_error_code"] == null || job["verification_error_code"].Type != JTokenType.Null
                || job["verification_error_message"] == null || job["verification_error_message"].Type != JTokenType.Null
                || !TryGetString(job, "created_at_utc", out string created)
                || !TryParseRoundTripTimestamp(created, out _)
                || !TryGetString(job, "updated_at_utc", out string updated)
                || !TryParseRoundTripTimestamp(updated, out _))
                return false;

            return string.Equals(ownership.ContextId, source.ContextId, StringComparison.Ordinal)
                && string.Equals(ownership.CompleteTargetJson, source.CompleteTargetJson, StringComparison.Ordinal)
                && string.Equals(ownership.OwnerId, identity.OwnerId, StringComparison.Ordinal)
                && string.Equals(ownership.ProviderGeneration, identity.ProviderGeneration, StringComparison.Ordinal);
        }

        private static string ComputePluginSubmissionDigest(string requestId, BoundPluginContext source,
            BoundPluginOwnerIdentity identity)
        {
            byte[] bytes;
            string parameters = RequestLedger.CanonicalizeJsonObject(PilotCanonicalParameters, "canonicalParametersJson");
            string target = RequestLedger.CanonicalizeJsonObject(source.CompleteTargetJson, "completeTargetJson");
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new Utf8JsonWriter(stream))
            using (JsonDocument parameterDocument = JsonDocument.Parse(parameters))
            using (JsonDocument targetDocument = JsonDocument.Parse(target))
            {
                writer.WriteStartObject();
                writer.WriteNumber("digest_version", 1);
                writer.WriteString("request_id", requestId);
                writer.WriteString("context_id", source.ContextId);
                writer.WriteString("tool_name", PilotPluginToolName);
                // PluginOwnerLockIdentity persists and reports canonical GUID-N
                // values even though the bound target uses GUID-D formatting.
                writer.WriteString("owner_id", Guid.Parse(identity.OwnerId).ToString("N"));
                writer.WriteNumber("owner_pid", identity.ProcessId);
                writer.WriteString("owner_process_started_at_utc",
                    identity.ProcessStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("provider_generation", identity.ProviderGeneration);
                writer.WritePropertyName("parameters");
                parameterDocument.RootElement.WriteTo(writer);
                writer.WritePropertyName("complete_target");
                targetDocument.RootElement.WriteTo(writer);
                writer.WriteBoolean("is_write", false);
                writer.WriteBoolean("global_fence", false);
                writer.WritePropertyName("risk_keys");
                writer.WriteStartArray();
                writer.WriteEndArray();
                writer.WriteNull("queue_deadline_utc");
                writer.WriteEndObject();
                writer.Flush();
                bytes = stream.ToArray();
            }
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private static bool MatchesObserverIdentity(JObject value, BoundPluginOwnerIdentity expected)
        {
            return value != null
                && value.Count == 4
                && TryGetString(value, "owner_id", out string ownerId)
                && value["process_id"]?.Type == JTokenType.Integer
                && TryGetString(value, "process_started_at_utc", out string startedText)
                && TryGetString(value, "provider_generation", out string generation)
                && TryParseRoundTripTimestamp(startedText, out DateTimeOffset started)
                && OwnerGuidEquals(ownerId, expected.OwnerId)
                && value.Value<int>("process_id") == expected.ProcessId
                && started.ToUniversalTime().Ticks == expected.ProcessStartedAtUtc.ToUniversalTime().Ticks
                && string.Equals(generation, expected.ProviderGeneration, StringComparison.Ordinal);
        }

        private static bool TryGetHistoricalLiveness(JObject value, out string liveness)
        {
            liveness = null;
            if (!TryGetString(value, "old_owner_liveness", out string token)) return false;
            if (token is not ("alive" or "confirmed_lost" or "indeterminate")) return false;
            liveness = token;
            return true;
        }

        private static bool OwnerGuidEquals(string actual, string expected)
        {
            return Guid.TryParse(actual, out Guid actualGuid)
                && Guid.TryParse(expected, out Guid expectedGuid)
                && actualGuid == expectedGuid;
        }

        private static bool TryParseRoundTripTimestamp(string value, out DateTimeOffset timestamp)
        {
            return DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out timestamp);
        }

        private static bool IsTerminal(JobPhase phase) =>
            phase is JobPhase.Completed or JobPhase.Failed or JobPhase.Cancelled;

        private static bool IsPending(JobPhase phase) =>
            phase is JobPhase.Queued or JobPhase.Running or JobPhase.CancellationRequested;

        private static string ToPublicPhase(JobPhase phase) => phase switch
        {
            JobPhase.CancellationRequested => "cancellation_requested",
            _ => phase.ToString().ToLowerInvariant()
        };

        private static string ToPublicStatus(JobPhase phase) =>
            IsPending(phase) ? "pending" : ToPublicPhase(phase);

        private static string HistoricalUnknown(string requestId, string contextId, bool? found, string reason,
            string liveness = null, long? sequence = null)
        {
            var result = new JObject
            {
                ["request_id"] = requestId,
                ["context_id"] = contextId,
                ["found"] = found.HasValue ? new JValue(found.Value) : JValue.CreateNull(),
                ["available"] = false,
                ["status"] = "unknown",
                ["phase"] = "unknown",
                ["pending"] = JValue.CreateNull(),
                ["reason"] = reason
            };
            if (liveness != null) result["old_owner_liveness"] = liveness;
            if (liveness != null) result["liveness_scope"] = "query_time_only";
            if (sequence.HasValue) result["plugin_sequence"] = sequence.Value;
            return Envelope(Guid.NewGuid().ToString("N"), true, result, null);
        }

        private static bool MatchesPilotJob(JObject job, BoundPluginContext bound, string requestId)
        {
            string actualCompleteTarget = null;
            bool matches = TryGetString(job, "request_id", out string actualRequestId)
                && TryGetString(job, "context_id", out string actualContextId)
                && TryGetString(job, "tool_name", out string actualToolName)
                && TryGetString(job, "canonical_parameters_json", out string actualCanonicalParameters)
                && TryGetString(job, "complete_target_json", out actualCompleteTarget)
                && string.Equals(actualRequestId, requestId, StringComparison.Ordinal)
                && string.Equals(actualContextId, bound.ContextId, StringComparison.Ordinal)
                && string.Equals(actualToolName, PilotPluginToolName, StringComparison.Ordinal)
                && string.Equals(actualCanonicalParameters, PilotCanonicalParameters, StringComparison.Ordinal);
            if (!matches)
                return false;

            try
            {
                // The plugin journal stores canonical JSON while bind persists
                // the registry's exact serialized target. Compare canonical
                // identities, not their harmless property ordering.
                if (!string.Equals(
                    RequestLedger.CanonicalizeJsonObject(actualCompleteTarget, "snapshot.complete_target_json"),
                    RequestLedger.CanonicalizeJsonObject(bound.CompleteTargetJson, "bound.complete_target_json"),
                    StringComparison.Ordinal))
                    return false;
            }
            catch { return false; }

            if (bound.TryGetOwnerIdentity(out BoundPluginOwnerIdentity owner, out _))
            {
                return TryGetString(job, "owner_id", out string actualOwnerId)
                    && TryGetString(job, "provider_generation", out string actualGeneration)
                    && OwnerGuidEquals(actualOwnerId, owner.OwnerId)
                    && string.Equals(actualGeneration, owner.ProviderGeneration, StringComparison.Ordinal);
            }

            // Explicit in-memory contract mode predates plugin owner fields.
            return true;
        }

        private bool MatchesGovernedReadJob(JObject job, BoundPluginContext bound, string requestId)
        {
            if (job == null || bound == null
                || !_contexts.TryGetRequestOwnership(requestId, bound.ContextId,
                    out BoundPluginContext ownerContext, out BoundPluginRequestOwnership ownership)
                || ownerContext == null || ownership == null
                || !string.Equals(ownerContext.ContextId, bound.ContextId, StringComparison.Ordinal)
                || !string.Equals(ownership.RequestId, requestId, StringComparison.Ordinal)
                || !string.Equals(ownership.ContextId, bound.ContextId, StringComparison.Ordinal)
                || !string.Equals(ownership.CompleteTargetJson, bound.CompleteTargetJson, StringComparison.Ordinal)
                || !string.Equals(ownership.PayloadDigest,
                    SqliteBoundPluginOwnershipStore.CreatePayloadDigest(ownership), StringComparison.Ordinal)
                || !TryValidateGovernedPluginRequest(ownership.ToolName, ownership.CanonicalParametersJson,
                    out _, out string expectedPluginToolName, out _))
                return false;

            if (!TryGetString(job, "request_id", out string actualRequestId)
                || !TryGetString(job, "context_id", out string actualContextId)
                || !TryGetString(job, "tool_name", out string actualToolName)
                || !TryGetString(job, "canonical_parameters_json", out string actualCanonicalParameters)
                || !TryGetString(job, "complete_target_json", out string actualCompleteTarget)
                || !TryGetBoolean(job, "is_write", out bool actualIsWrite)
                || actualIsWrite
                || !string.Equals(actualRequestId, requestId, StringComparison.Ordinal)
                || !string.Equals(actualContextId, bound.ContextId, StringComparison.Ordinal)
                || !string.Equals(actualToolName, expectedPluginToolName, StringComparison.Ordinal)
                || !string.Equals(actualToolName, ownership.ToolName, StringComparison.Ordinal)
                || !string.Equals(actualCanonicalParameters, ownership.CanonicalParametersJson, StringComparison.Ordinal))
                return false;

            try
            {
                string target = RequestLedger.CanonicalizeJsonObject(actualCompleteTarget, "snapshot.complete_target_json");
                if (!string.Equals(target,
                    RequestLedger.CanonicalizeJsonObject(bound.CompleteTargetJson, "bound.complete_target_json"),
                    StringComparison.Ordinal))
                    return false;
            }
            catch { return false; }

            if (bound.TryGetOwnerIdentity(out BoundPluginOwnerIdentity owner, out _))
            {
                return string.Equals(ownership.OwnerId, owner.OwnerId, StringComparison.Ordinal)
                    && string.Equals(ownership.ProviderGeneration, owner.ProviderGeneration, StringComparison.Ordinal)
                    && TryGetString(job, "owner_id", out string actualOwnerId)
                    && TryGetString(job, "provider_generation", out string actualGeneration)
                    && OwnerGuidEquals(actualOwnerId, owner.OwnerId)
                    && string.Equals(actualGeneration, owner.ProviderGeneration, StringComparison.Ordinal);
            }

            // In-memory contracts intentionally model the pre-owner identity
            // mode. Production ledgers always require the complete identity.
            return _ledger == null
                && string.IsNullOrEmpty(ownership.OwnerId)
                && string.IsNullOrEmpty(ownership.ProviderGeneration);
        }

        private bool TryAcceptPluginSnapshot(string requestId, BoundPluginContext bound, JObject job,
            long? expectedSequenceBeforeIo, out string error)
        {
            error = null;
            if (!MatchesGovernedReadJob(job, bound, requestId)
                || !TryMapGovernedReadSnapshot(job, out AdvanceRequest advance, out error))
            {
                if (error == null) error = "plugin snapshot identity mismatch";
                TryMarkLedgerUnavailable(requestId, GetProviderGeneration(bound), expectedSequenceBeforeIo);
                return false;
            }

            // The ownership store is the first monotonic gate. A snapshot is
            // mirrored to the ledger only after this durable gate accepts it.
            if (!_contexts.TryRecordPluginSnapshot(requestId, job, out error))
            {
                TryMarkLedgerUnavailable(requestId, GetProviderGeneration(bound), expectedSequenceBeforeIo);
                return false;
            }

            if (_ledger == null)
                return true;

            if (!TryGetMatchingLedgerJob(requestId, bound, out LedgerJob current))
            {
                error = "request ledger identity is unavailable";
                return false;
            }

            AdvanceResult result;
            try
            {
                result = _ledger.Advance(advance with
                {
                    RequestId = requestId,
                    ProviderGeneration = current.ProviderGeneration
                });
            }
            catch
            {
                TryMarkLedgerUnavailable(requestId, current.ProviderGeneration, expectedSequenceBeforeIo);
                error = "request ledger snapshot update failed";
                return false;
            }

            if (result.Disposition is AdvanceDisposition.Applied or AdvanceDisposition.Duplicate)
                return true;

            TryMarkLedgerUnavailable(requestId, current.ProviderGeneration, expectedSequenceBeforeIo);
            error = "request ledger rejected the plugin snapshot";
            return false;
        }

        private static bool TryMapGovernedReadSnapshot(JObject job, out AdvanceRequest advance, out string error)
        {
            advance = null;
            error = null;
            if (!TryGetString(job, "phase", out string phaseToken)
                || !TryGetString(job, "effects", out string effectsToken)
                || !TryGetString(job, "verification", out string verificationToken)
                || !TryGetString(job, "observation", out string observationToken)
                || !TryGetInt64(job, "plugin_sequence", out long sequence) || sequence < 0
                || !TryGetRequiredNullableString(job, "result_json", out string resultJson)
                || !TryGetRequiredNullableString(job, "error_code", out string errorCode)
                || !TryGetRequiredNullableString(job, "error_message", out string errorMessage))
            {
                error = "plugin snapshot has malformed state or result fields";
                return false;
            }

            JobPhase phase = phaseToken switch
            {
                "queued" => JobPhase.Queued,
                "running" => JobPhase.Running,
                "cancellation_requested" => JobPhase.CancellationRequested,
                "completed" => JobPhase.Completed,
                "failed" => JobPhase.Failed,
                "cancelled" => JobPhase.Cancelled,
                "unknown" => JobPhase.Unknown,
                _ => (JobPhase)(-1)
            };
            VerificationState verification = verificationToken switch
            {
                "not_checked" => VerificationState.NotChecked,
                _ => (VerificationState)(-1)
            };

            if (!Enum.IsDefined(phase) || effectsToken != "none" || !Enum.IsDefined(verification)
                || observationToken is not ("plugin_snapshot" or "recovery"))
            {
                error = "plugin snapshot state is not allowed for governed reads";
                return false;
            }

            if (resultJson != null)
            {
                try { JToken.Parse(resultJson); }
                catch
                {
                    error = "plugin snapshot result_json is malformed";
                    return false;
                }
            }

            advance = new AdvanceRequest(
                RequestId: string.Empty,
                ProviderGeneration: string.Empty,
                PluginSequence: sequence,
                Phase: phase,
                Effects: EffectState.None,
                Verification: verification,
                Observation: ObservationState.AuthoritativePluginSnapshot,
                ResultJson: resultJson,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage);
            return true;
        }

        private static bool TryGetRequiredNullableString(JObject value, string propertyName, out string result)
        {
            result = null;
            JToken token = value?[propertyName];
            if (token == null) return false;
            if (token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.String) return false;
            result = token.Value<string>();
            return true;
        }

        private bool TryGetMatchingLedgerJob(string requestId, BoundPluginContext bound, out LedgerJob job)
        {
            job = null;
            if (_ledger == null || bound == null) return false;
            try
            {
                job = _ledger.Get(requestId);
                if (!_contexts.TryGetRequestOwnership(requestId, bound.ContextId,
                    out _, out BoundPluginRequestOwnership ownership))
                    return false;
                if (!TryValidateGovernedPluginRequest(ownership.ToolName,
                    ownership.CanonicalParametersJson, out string mcpToolName,
                    out string validatedPluginToolName, out _)
                    || !string.Equals(validatedPluginToolName, ownership.ToolName, StringComparison.Ordinal))
                    return false;
                return MatchesLedgerJob(job, bound, requestId,
                    mcpToolName, ownership.CanonicalParametersJson);
            }
            catch
            {
                job = null;
                return false;
            }
        }

        private long? CaptureLedgerSequence(string requestId, BoundPluginContext bound)
        {
            return TryGetMatchingLedgerJob(requestId, bound, out LedgerJob job) ? job.PluginSequence : null;
        }

        private static bool MatchesLedgerJob(LedgerJob job, BoundPluginContext bound, string requestId,
            string expectedToolName, string expectedCanonicalParametersJson)
        {
            if (job == null || bound == null || !bound.TryGetOwnerIdentity(out BoundPluginOwnerIdentity identity, out _))
                return false;
            if (!TryValidateGovernedReadRequest(expectedToolName, expectedCanonicalParametersJson,
                out string expectedPluginToolName, out _))
                return false;
            try
            {
                return string.Equals(job.RequestId, requestId, StringComparison.Ordinal)
                    && string.Equals(job.ContextId, bound.ContextId, StringComparison.Ordinal)
                    && string.Equals(job.ToolName, expectedToolName, StringComparison.Ordinal)
                    && string.Equals(job.CanonicalParametersJson, expectedCanonicalParametersJson, StringComparison.Ordinal)
                    && string.Equals(job.CompleteTargetJson,
                        RequestLedger.CanonicalizeJsonObject(bound.CompleteTargetJson, "completeTargetJson"), StringComparison.Ordinal)
                    && !job.IsWrite && !job.GlobalFence && job.HeldRiskKeys.Count == 0
                    && string.Equals(job.ProviderGeneration, identity.ProviderGeneration, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static string GetProviderGeneration(BoundPluginContext bound) =>
            bound != null && bound.TryGetOwnerIdentity(out BoundPluginOwnerIdentity identity, out _)
                ? identity.ProviderGeneration : string.Empty;

        private void TryMarkLedgerUnavailable(string requestId, string providerGeneration, long? expectedSequence,
            BoundPluginContext bound = null)
        {
            if (_ledger == null || string.IsNullOrWhiteSpace(providerGeneration))
                return;
            if (bound == null && !_contexts.TryGetContextFromRequestOwner(requestId, out bound))
                return;
            if (!TryGetMatchingLedgerJob(requestId, bound, out LedgerJob current)
                || !string.Equals(current.ProviderGeneration, providerGeneration, StringComparison.Ordinal))
                return;
            try { _ledger.MarkUnavailable(requestId, providerGeneration, expectedSequence); }
            catch { }
        }

        private void TryMarkReservationLedgerUnavailable(string requestId, string providerGeneration,
            long? expectedSequence, BoundPluginContext bound, string mcpToolName, string canonicalParametersJson)
        {
            if (_ledger == null || bound == null || string.IsNullOrWhiteSpace(providerGeneration))
                return;
            try
            {
                LedgerJob reserved = _ledger.Get(requestId);
                if (!MatchesLedgerJob(reserved, bound, requestId, mcpToolName, canonicalParametersJson)
                    || !string.Equals(reserved.ProviderGeneration, providerGeneration, StringComparison.Ordinal))
                    return;
                _ledger.MarkUnavailable(requestId, providerGeneration, expectedSequence);
            }
            catch { }
        }

        private bool TryCompleteReservation(RequestSubmissionLease reservation, RequestSubmissionOutcome outcome,
            string requestId, string providerGeneration, long? expectedSequenceBeforeIo)
        {
            if (reservation == null) return false;
            try
            {
                reservation.Complete(outcome);
                return true;
            }
            catch
            {
                TryMarkLedgerUnavailable(requestId, providerGeneration, expectedSequenceBeforeIo);
                return false;
            }
        }

        private static bool TryGetString(JObject value, string propertyName, out string result)
        {
            result = null;
            JToken token = value?[propertyName];
            if (token == null || token.Type != JTokenType.String)
                return false;

            result = (string)token;
            return result != null;
        }

        private static bool TryGetBoolean(JObject value, string propertyName, out bool result)
        {
            result = false;
            JToken token = value?[propertyName];
            if (token == null || token.Type != JTokenType.Boolean)
                return false;

            result = (bool)token;
            return true;
        }

        private static bool TryGetOptionalString(JObject value, string propertyName, out string result)
        {
            result = null;
            JToken token = value?[propertyName];
            if (token == null || token.Type == JTokenType.Null)
                return true;
            if (token.Type != JTokenType.String)
                return false;

            result = (string)token;
            return result != null;
        }

        private static bool TryGetInt64(JObject value, string propertyName, out long result)
        {
            result = 0;
            JToken token = value?[propertyName];
            if (token == null || token.Type != JTokenType.Integer)
                return false;

            try
            {
                result = (long)token;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CopyOptionalString(JObject source, JObject destination, string propertyName)
        {
            JToken token = source[propertyName];
            if (token == null)
                return;
            if (token.Type == JTokenType.Null)
            {
                destination[propertyName] = JValue.CreateNull();
                return;
            }
            if (token.Type == JTokenType.String)
                destination[propertyName] = token.DeepClone();
        }

        private static bool IsTransportUncertain(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return true;
            return string.Equals(error, QueueHaltedError, StringComparison.Ordinal)
                || error.IndexOf("communication", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("closed connection", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("empty response", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("response ID", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsValidIdentifier(string value) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Length <= CommonArgumentMcpServerTool.MaxRequestIdLength
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[value.Length - 1])
            && !value.Any(char.IsControl);

        private static bool IsEmptyObject(object value)
        {
            if (value == null)
                return false;
            try
            {
                var token = JToken.FromObject(value);
                return token is JObject obj && !obj.Properties().Any();
            }
            catch
            {
                return false;
            }
        }

        private static bool IsJsonObject(string value)
        {
            try { return JToken.Parse(value) is JObject; }
            catch { return false; }
        }

        private sealed record GovernedReadRequest(string McpToolName, string PluginToolName,
            string CanonicalParametersJson);

        private static string EndpointKey(DiscoveryInfo discovery) =>
            (discovery.Transport ?? string.Empty) + "|"
            + (discovery.Host ?? string.Empty) + "|"
            + (discovery.Port?.ToString() ?? string.Empty) + "|"
            + (discovery.PipeName ?? string.Empty) + "|"
            + (discovery.ControlPipeName ?? string.Empty) + "|"
            + (discovery.Token ?? string.Empty) + "|"
            + (discovery.Target ?? string.Empty) + "|"
            + discovery.Pid + "|"
            + (discovery.StartedAtUtc?.ToUniversalTime().Ticks.ToString() ?? string.Empty);

        private static string Envelope(string id, bool ok, object result, string error) =>
            JsonConvert.SerializeObject(new
            {
                id = id ?? Guid.NewGuid().ToString("N"),
                ok,
                result = ok ? result : null,
                error
            });
    }
}
