using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server.Execution
{
    public enum RequestSubmissionOutcome
    {
        DefinitelyRejected,
        OutcomeUnknown,
        Confirmed
    }

    public enum BoundPluginRequestState
    {
        Reserved,
        OutcomeUnknown,
        Confirmed
    }

    public sealed class BoundPluginOwnerIdentity
    {
        internal BoundPluginOwnerIdentity(string ownerId, int processId, DateTimeOffset processStartedAtUtc, string providerGeneration)
        {
            OwnerId = ownerId;
            ProcessId = processId;
            ProcessStartedAtUtc = processStartedAtUtc.ToUniversalTime();
            ProviderGeneration = providerGeneration;
        }

        public string OwnerId { get; }
        public int ProcessId { get; }
        public DateTimeOffset ProcessStartedAtUtc { get; }
        public string ProviderGeneration { get; }
    }

    public sealed class BoundPluginRequestOwnership
    {
        internal BoundPluginRequestOwnership(string requestId, string contextId, string toolName,
            string canonicalParametersJson, string payloadDigest, string completeTargetJson,
            string ownerId, string providerGeneration, BoundPluginRequestState state,
            bool recoveryQueryOnly, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc,
            long? pluginSequence = null, string snapshotDigest = null, bool isWrite = false,
            IEnumerable<string> riskKeys = null, string journalScopeId = null,
            string journalPathDigest = null, int payloadVersion = 2)
        {
            RequestId = requestId;
            ContextId = contextId;
            ToolName = toolName;
            CanonicalParametersJson = canonicalParametersJson;
            PayloadDigest = payloadDigest;
            CompleteTargetJson = completeTargetJson;
            OwnerId = ownerId;
            ProviderGeneration = providerGeneration;
            State = state;
            RecoveryQueryOnly = recoveryQueryOnly;
            CreatedAtUtc = createdAtUtc.ToUniversalTime();
            UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
            PluginSequence = pluginSequence;
            SnapshotDigest = snapshotDigest;
            IsWrite = isWrite;
            RiskKeys = Array.AsReadOnly((riskKeys ?? Array.Empty<string>()).ToArray());
            JournalScopeId = journalScopeId;
            JournalPathDigest = journalPathDigest;
            PayloadVersion = payloadVersion;
        }

        public string RequestId { get; }
        public string ContextId { get; }
        public string ToolName { get; }
        public string CanonicalParametersJson { get; }
        public string PayloadDigest { get; }
        public string CompleteTargetJson { get; }
        public string OwnerId { get; }
        public string ProviderGeneration { get; }
        public BoundPluginRequestState State { get; internal set; }
        public bool RecoveryQueryOnly { get; }
        public DateTimeOffset CreatedAtUtc { get; }
        public DateTimeOffset UpdatedAtUtc { get; internal set; }
        public long? PluginSequence { get; internal set; }
        public string SnapshotDigest { get; internal set; }
        public bool IsWrite { get; }
        public IReadOnlyList<string> RiskKeys { get; }
        public string JournalScopeId { get; }
        public string JournalPathDigest { get; }
        public int PayloadVersion { get; }
    }

    public sealed class RequestSubmissionLease
    {
        private readonly BoundPluginContextStore _store;
        private readonly string _requestId;
        private readonly string _contextId;
        private int _completed;

        internal RequestSubmissionLease(BoundPluginContextStore store, string requestId, string contextId)
        {
            _store = store;
            _requestId = requestId;
            _contextId = contextId;
        }

        public void Complete(RequestSubmissionOutcome outcome)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                _store.CompleteSubmission(_requestId, _contextId, outcome);
        }
    }

    public sealed class BoundPluginContext
    {
        private readonly DiscoveryInfo _discovery;

        public BoundPluginContext(string contextId, string completeTargetJson, DiscoveryInfo discovery,
            string journalScopeId = null, string journalPathDigest = null)
        {
            ContextId = contextId ?? throw new ArgumentNullException(nameof(contextId));
            CompleteTargetJson = completeTargetJson ?? throw new ArgumentNullException(nameof(completeTargetJson));
            _discovery = (discovery ?? throw new ArgumentNullException(nameof(discovery))).Snapshot();
            JournalScopeId = journalScopeId;
            JournalPathDigest = journalPathDigest;
        }

        public string ContextId { get; }
        public string CompleteTargetJson { get; }
        public DiscoveryInfo Discovery => _discovery.Snapshot();
        public string JournalScopeId { get; }
        public string JournalPathDigest { get; }

        internal bool TryGetJournalScopeIdentity(bool required, out string scopeId,
            out string pathDigest, out string error)
        {
            scopeId = null;
            pathDigest = null;
            error = null;

            bool scopeMissing = JournalScopeId == null;
            bool digestMissing = JournalPathDigest == null;
            if (scopeMissing && digestMissing)
            {
                if (required)
                {
                    error = "bound context journal scope and path digest are required";
                    return false;
                }
                return true;
            }

            if (scopeMissing || digestMissing
                || !Guid.TryParseExact(JournalScopeId, "N", out Guid scopeGuid)
                || scopeGuid == Guid.Empty
                || !string.Equals(JournalScopeId, scopeGuid.ToString("N"), StringComparison.Ordinal)
                || JournalPathDigest.Length != 64
                || JournalPathDigest.Any(character => !(character >= '0' && character <= '9')
                    && !(character >= 'A' && character <= 'F')))
            {
                error = "bound context journal scope or path digest is invalid";
                return false;
            }

            scopeId = JournalScopeId;
            pathDigest = JournalPathDigest;
            return true;
        }

        internal bool TryGetDrawingPathRiskKey(out string riskKey, out string error)
        {
            riskKey = null;
            error = null;
            try
            {
                JObject target = ParseObjectPreservingStrings(CompleteTargetJson);
                string path = target.Value<string>("drawing_path");
                if (!DrawingRiskKey.TryNormalizePath(path, out riskKey))
                {
                    error = "bound target has no verifiable saved drawing path";
                    return false;
                }

                return true;
            }
            catch
            {
                error = "bound target drawing path is invalid";
                return false;
            }
        }

        public bool TryGetOwnerIdentity(out BoundPluginOwnerIdentity identity, out string error)
        {
            identity = null;
            error = null;
            if (!TryGetJournalScopeIdentity(required: false, out _, out _, out error))
                return false;
            if (!BoundPluginContextStore.IsValidIdentifier(ContextId)
                || string.IsNullOrWhiteSpace(CompleteTargetJson)
                || string.IsNullOrWhiteSpace(_discovery.Token)
                || _discovery.Pid <= 0
                || string.IsNullOrWhiteSpace(_discovery.Target)
                || !_discovery.StartedAtUtc.HasValue
                || _discovery.StartedAtUtc.Value == default(DateTime)
                || _discovery.StartedAtUtc.Value.ToUniversalTime() > DateTime.UtcNow.AddMinutes(1)
                || !string.Equals(_discovery.Transport, "pipe", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(_discovery.Transport, "tcp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_discovery.Transport, "pipe", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(_discovery.PipeName) || string.IsNullOrWhiteSpace(_discovery.ControlPipeName))
                || string.Equals(_discovery.Transport, "tcp", StringComparison.OrdinalIgnoreCase)
                    && ((_discovery.Port ?? 0) <= 0 || string.IsNullOrWhiteSpace(_discovery.Host)))
            {
                error = "bound endpoint or context identity is invalid";
                return false;
            }

            try
            {
                JObject target = ParseObjectPreservingStrings(CompleteTargetJson);
                if (!string.Equals(target.Value<string>("context_id"), ContextId, StringComparison.Ordinal))
                {
                    error = "complete_target_json context_id does not match the bound context";
                    return false;
                }
                if (!Guid.TryParse(target.Value<string>("owner_id"), out Guid ownerGuid) || ownerGuid == Guid.Empty)
                {
                    error = "complete_target_json owner_id is invalid";
                    return false;
                }
                if (target["owner_pid"]?.Type != JTokenType.Integer)
                {
                    error = "complete_target_json owner_pid is invalid";
                    return false;
                }
                if (!TryParseOffsetTimestamp(target.Value<string>("owner_started_at_utc"), out DateTimeOffset started))
                {
                    error = "complete_target_json owner_started_at_utc is invalid";
                    return false;
                }
                if (started > DateTimeOffset.UtcNow.AddMinutes(1))
                {
                    error = "complete_target_json owner_started_at_utc is in the future";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(target.Value<string>("owner_generation")))
                {
                    error = "complete_target_json owner_generation is invalid";
                    return false;
                }

                int ownerPid = (int)target["owner_pid"];
                if (ownerPid != _discovery.Pid)
                {
                    error = "bound context owner PID does not match the selected plugin endpoint PID";
                    return false;
                }

                identity = new BoundPluginOwnerIdentity(ownerGuid.ToString("D"), ownerPid, started,
                    target.Value<string>("owner_generation"));
                return true;
            }
            catch
            {
                error = "complete_target_json is invalid";
                return false;
            }
        }

        private static bool TryParseOffsetTimestamp(string value, out DateTimeOffset parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(value)
                || !(value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(value, @"[+-][0-9]{2}:[0-9]{2}$")))
                return false;
            return DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed);
        }

        private static JObject ParseObjectPreservingStrings(string json)
        {
            using var input = new StringReader(json);
            using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None };
            JObject result = JObject.Load(reader);
            if (reader.Read())
                throw new JsonReaderException("Trailing JSON data is not allowed.");
            return result;
        }
    }

    /// <summary>
    /// Pins plugin routing at bind time and records which bound context owns
    /// each request ID. Production supplies a durable SQLite backend; the
    /// parameterless in-memory mode remains explicit for isolated contracts.
    /// </summary>
    public sealed class BoundPluginContextStore
    {
        public const string H1WriteToolName = H1FixedHatchWriteContract.PluginToolName;
        public const string FoundationWriteToolName = FoundationDrawingPlan.PluginToolName;
        public const string D1WriteToolName = "drawing_write";

        private sealed class RequestOwner
        {
            public RequestOwner(BoundPluginRequestOwnership ownership, BoundPluginContext context)
            {
                Ownership = ownership;
                Context = context;
            }

            public BoundPluginRequestOwnership Ownership { get; }
            public BoundPluginContext Context { get; }
            public int InFlightSubmissions { get; set; }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, BoundPluginContext> _contexts = new Dictionary<string, BoundPluginContext>(StringComparer.Ordinal);
        private readonly Dictionary<string, RequestOwner> _requestOwners = new Dictionary<string, RequestOwner>(StringComparer.Ordinal);
        private readonly SqliteBoundPluginOwnershipStore _durable;

        public BoundPluginContextStore() { }

        public BoundPluginContextStore(SqliteBoundPluginOwnershipStore durableStore)
        {
            _durable = durableStore ?? throw new ArgumentNullException(nameof(durableStore));
            foreach (BoundPluginContext context in durableStore.LoadContexts())
                _contexts.Add(context.ContextId, context);
            foreach (BoundPluginRequestOwnership owner in durableStore.LoadRequests())
            {
                if (!_contexts.TryGetValue(owner.ContextId, out BoundPluginContext context)
                    || !string.Equals(context.CompleteTargetJson, owner.CompleteTargetJson, StringComparison.Ordinal)
                    || !context.TryGetOwnerIdentity(out BoundPluginOwnerIdentity identity, out _)
                    || !string.Equals(identity.OwnerId, owner.OwnerId, StringComparison.Ordinal)
                    || !string.Equals(identity.ProviderGeneration, owner.ProviderGeneration, StringComparison.Ordinal)
                    || !RequestMatchesBoundContext(context, owner))
                    throw new InvalidOperationException("Stored request ownership does not match its immutable bound context.");
                _requestOwners.Add(owner.RequestId, new RequestOwner(owner, context));
            }
        }

        internal bool IsDurable => _durable != null;

        public bool TryAdd(BoundPluginContext context, out string error)
        {
            error = null;
            if (context == null)
            {
                error = "bound context is required";
                return false;
            }
            if (!IsValidIdentifier(context.ContextId)
                || string.IsNullOrWhiteSpace(context.CompleteTargetJson)
                || string.IsNullOrWhiteSpace(context.Discovery.Token))
            {
                error = "bound context identity, target, or plugin token is invalid";
                return false;
            }
            if (!context.TryGetJournalScopeIdentity(required: false, out _, out _, out error))
                return false;
            if (_durable != null && !context.TryGetOwnerIdentity(out _, out error))
                return false;

            lock (_gate)
            {
                if (_contexts.TryGetValue(context.ContextId, out var existing))
                {
                    if (!string.Equals(existing.CompleteTargetJson, context.CompleteTargetJson, StringComparison.Ordinal)
                        || !SameEndpoint(existing.Discovery, context.Discovery)
                        || !SameJournalScope(existing, context.JournalScopeId, context.JournalPathDigest))
                    {
                        error = "context_id is already bound to a different plugin endpoint or target";
                        return false;
                    }

                    return true;
                }

                // Commit the immutable endpoint pin before exposing it to callers.
                if (_durable != null && !_durable.AddContext(context, out error))
                    return false;
                _contexts.Add(context.ContextId, context);
                return true;
            }
        }

        public bool TryGetContext(string contextId, out BoundPluginContext context)
        {
            lock (_gate)
                return _contexts.TryGetValue(contextId ?? string.Empty, out context);
        }

        internal IReadOnlyList<BoundPluginRequestOwnership> GetRequestOwnersForStartup()
        {
            lock (_gate)
                return _requestOwners.Values
                    .Select(owner => owner.Ownership)
                    .OrderBy(owner => owner.RequestId, StringComparer.Ordinal)
                    .ToArray();
        }

        public bool TryReserveRequest(string requestId, string contextId, string toolName,
            string canonicalParametersJson, out BoundPluginContext context,
            out RequestSubmissionLease reservation, out string error, bool allowCreate = true)
        {
            return TryReserveRequestCore(requestId, contextId, toolName, canonicalParametersJson,
                isWrite: false, Array.Empty<string>(), out context, out reservation, out error, allowCreate);
        }

        public bool TryReserveWriteRequest(string requestId, string contextId,
            string canonicalParametersJson, string singlePathRiskKey,
            out BoundPluginContext context, out RequestSubmissionLease reservation,
            out string error, bool allowCreate = true)
 =>
            TryReserveFixedWriteRequest(requestId, contextId, canonicalParametersJson, singlePathRiskKey, out context, out reservation, out error, allowCreate, D1WriteToolName);

        public bool TryReserveH1WriteRequest(string requestId, string contextId,
            string canonicalParametersJson, string singlePathRiskKey,
            out BoundPluginContext context, out RequestSubmissionLease reservation,
            out string error, bool allowCreate = true)
 =>
            TryReserveFixedWriteRequest(requestId, contextId, canonicalParametersJson, singlePathRiskKey, out context, out reservation, out error, allowCreate, H1WriteToolName);

        public bool TryReserveFoundationWriteRequest(string requestId,string contextId,string canonicalParametersJson,string singlePathRiskKey,out BoundPluginContext context,out RequestSubmissionLease reservation,out string error,bool allowCreate=true) => TryReserveFixedWriteRequest(requestId,contextId,canonicalParametersJson,singlePathRiskKey,out context,out reservation,out error,allowCreate,FoundationWriteToolName);

        private bool TryReserveFixedWriteRequest(string requestId, string contextId,
            string canonicalParametersJson, string singlePathRiskKey,
            out BoundPluginContext context, out RequestSubmissionLease reservation,
            out string error, bool allowCreate, string fixedToolName)
        {
            context = null;
            reservation = null;
            error = null;

            string normalizedRiskKey;
            try
            {
                if (string.IsNullOrWhiteSpace(singlePathRiskKey)
                    || !singlePathRiskKey.StartsWith("PATH:", StringComparison.OrdinalIgnoreCase))
                {
                    error = "D1 write ownership requires exactly one explicit PATH: risk key";
                    return false;
                }

                normalizedRiskKey = DrawingRiskKey.Normalize(singlePathRiskKey);
                if (!normalizedRiskKey.StartsWith("PATH:", StringComparison.Ordinal))
                {
                    error = "D1 write ownership requires exactly one normalized PATH: risk key";
                    return false;
                }
            }
            catch (ArgumentException)
            {
                error = "D1 write risk key is invalid";
                return false;
            }

            return TryReserveRequestCore(requestId, contextId, fixedToolName, canonicalParametersJson,
                isWrite: true, new[] { normalizedRiskKey }, out context, out reservation, out error, allowCreate);
        }

        private bool TryReserveRequestCore(string requestId, string contextId, string toolName,
            string canonicalParametersJson, bool isWrite, IReadOnlyList<string> riskKeys,
            out BoundPluginContext context, out RequestSubmissionLease reservation,
            out string error, bool allowCreate)
        {
            context = null;
            reservation = null;
            error = null;
            lock (_gate)
            {
                if (!_contexts.TryGetValue(contextId ?? string.Empty, out context))
                {
                    error = "unknown context_id; bind the active document explicitly first";
                    return false;
                }

                requestId = requestId ?? string.Empty;
                if (!IsValidIdentifier(requestId)
                    || string.IsNullOrWhiteSpace(toolName)
                    || string.IsNullOrWhiteSpace(canonicalParametersJson)
                    || !IsJsonObject(canonicalParametersJson))
                {
                    context = null;
                    error = "request_id, tool name, or canonical parameters are invalid";
                    return false;
                }

                if ((!isWrite && (toolName == D1WriteToolName || toolName == H1WriteToolName || toolName == FoundationWriteToolName))
                    || (isWrite && toolName != D1WriteToolName && toolName != H1WriteToolName && toolName != FoundationWriteToolName))
                {
                    context = null;
                    error = "only the dedicated D1 write reservation may own drawing_write requests";
                    return false;
                }

                string journalScopeId = null;
                string journalPathDigest = null;
                if (!context.TryGetJournalScopeIdentity(isWrite, out journalScopeId, out journalPathDigest, out error))
                {
                    context = null;
                    return false;
                }

                if (isWrite)
                {
                    if (riskKeys == null || riskKeys.Count != 1
                        || !context.TryGetDrawingPathRiskKey(out string expectedRiskKey, out error)
                        || !string.Equals(riskKeys[0], expectedRiskKey, StringComparison.Ordinal))
                    {
                        context = null;
                        error = error ?? "D1 write risk key does not match the bound saved drawing path";
                        return false;
                    }
                }
                else if (riskKeys == null || riskKeys.Count != 0)
                {
                    context = null;
                    error = "read-only request ownership cannot hold write risk keys";
                    return false;
                }

                BoundPluginOwnerIdentity identity = null;
                if (context.TryGetOwnerIdentity(out var parsed, out _) )
                    identity = parsed;
                if (identity == null && _durable != null)
                {
                    context = null;
                    error = "bound context owner identity is invalid";
                    return false;
                }

                var proposed = CreateOwnership(requestId, context, toolName, canonicalParametersJson,
                    identity, isWrite, riskKeys, journalScopeId, journalPathDigest);
                if (_requestOwners.TryGetValue(requestId, out var owner))
                {
                    if (!SameRequest(owner.Ownership, proposed)
                        || !SameEndpoint(owner.Context.Discovery, context.Discovery))
                    {
                        context = null;
                        error = "request_id is already owned by a different context or immutable payload";
                        return false;
                    }
                    if (owner.Ownership.RecoveryQueryOnly)
                    {
                        context = null;
                        error = "restored request ownership is query-only; resubmission and cancellation are disabled";
                        return false;
                    }
                    owner.InFlightSubmissions++;
                    reservation = new RequestSubmissionLease(this, requestId, contextId);
                    return true;
                }

                if (!allowCreate)
                {
                    context = null;
                    error = "request_id has no durable owner for this existing ledger entry";
                    return false;
                }

                if (_durable != null && !_durable.TryReserve(proposed, out _, out error))
                {
                    context = null;
                    return false;
                }

                var newOwner = new RequestOwner(proposed, context) { InFlightSubmissions = 1 };
                _requestOwners.Add(requestId, newOwner);
                reservation = new RequestSubmissionLease(this, requestId, contextId);
                return true;
            }
        }

        private static BoundPluginRequestOwnership CreateOwnership(string requestId, BoundPluginContext context,
            string toolName, string canonicalParametersJson, BoundPluginOwnerIdentity identity,
            bool isWrite, IReadOnlyList<string> riskKeys, string journalScopeId, string journalPathDigest)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var value = new BoundPluginRequestOwnership(requestId, context.ContextId, toolName,
                canonicalParametersJson, string.Empty, context.CompleteTargetJson,
                identity?.OwnerId ?? string.Empty, identity?.ProviderGeneration ?? string.Empty,
                BoundPluginRequestState.Reserved, false, now, now, isWrite: isWrite,
                riskKeys: riskKeys, journalScopeId: journalScopeId,
                journalPathDigest: journalPathDigest, payloadVersion: 2);
            string digest = SqliteBoundPluginOwnershipStore.CreatePayloadDigest(value);
            return new BoundPluginRequestOwnership(requestId, context.ContextId, toolName,
                canonicalParametersJson, digest, context.CompleteTargetJson,
                identity?.OwnerId ?? string.Empty, identity?.ProviderGeneration ?? string.Empty,
                BoundPluginRequestState.Reserved, false, now, now, isWrite: isWrite,
                riskKeys: riskKeys, journalScopeId: journalScopeId,
                journalPathDigest: journalPathDigest, payloadVersion: 2);
        }

        private static bool SameRequest(BoundPluginRequestOwnership left, BoundPluginRequestOwnership right) =>
            string.Equals(left.ContextId, right.ContextId, StringComparison.Ordinal)
                    && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
            && string.Equals(left.CanonicalParametersJson, right.CanonicalParametersJson, StringComparison.Ordinal)
            && string.Equals(left.CompleteTargetJson, right.CompleteTargetJson, StringComparison.Ordinal)
            && string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal)
            && string.Equals(left.ProviderGeneration, right.ProviderGeneration, StringComparison.Ordinal)
            && left.IsWrite == right.IsWrite
            && left.RiskKeys.SequenceEqual(right.RiskKeys, StringComparer.Ordinal)
            && string.Equals(left.JournalScopeId, right.JournalScopeId, StringComparison.Ordinal)
            && string.Equals(left.JournalPathDigest, right.JournalPathDigest, StringComparison.Ordinal);

        private static bool SameJournalScope(BoundPluginContext context, string scopeId, string pathDigest) =>
            string.Equals(context.JournalScopeId, scopeId, StringComparison.Ordinal)
            && string.Equals(context.JournalPathDigest, pathDigest, StringComparison.Ordinal);

        private static bool RequestMatchesBoundContext(BoundPluginContext context,
            BoundPluginRequestOwnership owner)
        {
            if (!SameJournalScope(context, owner.JournalScopeId, owner.JournalPathDigest))
                return false;
            if (!owner.IsWrite)
                return true;

            return context.TryGetJournalScopeIdentity(required: true, out _, out _, out _)
                && context.TryGetDrawingPathRiskKey(out string targetRiskKey, out _)
                && owner.RiskKeys.Count == 1
                && string.Equals(owner.RiskKeys[0], targetRiskKey, StringComparison.Ordinal);
        }

        internal void CompleteSubmission(string requestId, string contextId, RequestSubmissionOutcome outcome)
        {
            lock (_gate)
            {
                if (!_requestOwners.TryGetValue(requestId ?? string.Empty, out var owner)
                    || !string.Equals(owner.Ownership.ContextId, contextId, StringComparison.Ordinal))
                    return;

                BoundPluginRequestState next = owner.Ownership.State;
                switch (outcome)
                {
                    case RequestSubmissionOutcome.Confirmed:
                        next = BoundPluginRequestState.Confirmed;
                        break;
                    case RequestSubmissionOutcome.OutcomeUnknown:
                        if (next != BoundPluginRequestState.Confirmed)
                            next = BoundPluginRequestState.OutcomeUnknown;
                        break;
                    case RequestSubmissionOutcome.DefinitelyRejected:
                        // Keep the durable reservation. A plugin rejection is
                        // not proof that the prior server never sent the request.
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(outcome));
                }

                try
                {
                    if (_durable != null && next != owner.Ownership.State)
                        _durable.UpdateState(requestId, next);
                    owner.Ownership.State = next;
                    owner.Ownership.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    if (owner.InFlightSubmissions > 0)
                        owner.InFlightSubmissions--;
                }
                catch
                {
                    // A failed terminal write leaves SQLite Reserved so next
                    // startup recovers conservatively. Keep in-memory access
                    // queryable if plugin I/O may already have happened.
                    owner.Ownership.State = BoundPluginRequestState.OutcomeUnknown;
                    owner.InFlightSubmissions = 0;
                    throw;
                }
            }
        }

        internal bool TryGetRequestOwnership(string requestId, string contextId,
            out BoundPluginContext context, out BoundPluginRequestOwnership ownership)
        {
            context = null;
            ownership = null;
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(requestId)
                    || string.IsNullOrWhiteSpace(contextId)
                    || !_requestOwners.TryGetValue(requestId, out var owner)
                    || !string.Equals(owner.Ownership.ContextId, contextId, StringComparison.Ordinal))
                    return false;

                context = owner.Context;
                ownership = owner.Ownership;
                return true;
            }
        }

        public bool TryGetOwnedRequest(string requestId, string contextId, bool requireConfirmed,
            out BoundPluginContext context, out BoundPluginRequestOwnership ownership)
        {
            context = null;
            ownership = null;
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(requestId)
                    || string.IsNullOrWhiteSpace(contextId)
                    || !_requestOwners.TryGetValue(requestId, out var owner)
                    || !string.Equals(owner.Ownership.ContextId, contextId, StringComparison.Ordinal)
                    || owner.Ownership.State == BoundPluginRequestState.Reserved
                    || requireConfirmed && owner.Ownership.State != BoundPluginRequestState.Confirmed)
                    return false;

                context = owner.Context;
                ownership = owner.Ownership;
                return true;
            }
        }

        internal bool TryGetContextFromRequestOwner(string requestId, out BoundPluginContext context)
        {
            context = null;
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(requestId)
                    || !_requestOwners.TryGetValue(requestId, out RequestOwner owner))
                    return false;
                context = owner.Context;
                return true;
            }
        }

        internal bool TryGetHistoricalQueryOwner(string requestId, string contextId,
            out BoundPluginContext context, out BoundPluginRequestOwnership ownership)
        {
            context = null;
            ownership = null;
            lock (_gate)
            {
                if (_durable == null
                    || string.IsNullOrWhiteSpace(requestId)
                    || string.IsNullOrWhiteSpace(contextId)
                    || !_requestOwners.TryGetValue(requestId, out RequestOwner owner)
                    || !string.Equals(owner.Ownership.ContextId, contextId, StringComparison.Ordinal))
                    return false;
                context = owner.Context;
                ownership = owner.Ownership;
                return true;
            }
        }

        public bool OwnsRequest(string requestId, string contextId, out BoundPluginContext context)
        {
            bool owns = TryGetOwnedRequest(requestId, contextId, false, out context, out _);
            return owns;
        }

        public bool OwnsConfirmedRequest(string requestId, string contextId, out BoundPluginContext context)
        {
            bool owns = TryGetOwnedRequest(requestId, contextId, true, out context, out _);
            return owns;
        }

        public void MarkRequestOutcome(string requestId, string contextId, bool confirmed)
        {
            lock (_gate)
            {
                if (!_requestOwners.TryGetValue(requestId ?? string.Empty, out var owner)
                    || !string.Equals(owner.Ownership.ContextId, contextId, StringComparison.Ordinal))
                    return;

                BoundPluginRequestState next = confirmed
                    ? BoundPluginRequestState.Confirmed
                    : owner.Ownership.State == BoundPluginRequestState.Confirmed
                        ? BoundPluginRequestState.Confirmed
                        : BoundPluginRequestState.OutcomeUnknown;
                if (next != owner.Ownership.State && _durable != null)
                    _durable.UpdateState(requestId, next);
                owner.Ownership.State = next;
                owner.Ownership.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public bool TryRecordPluginSnapshot(string requestId, JObject snapshot, out string error)
        {
            error = null;
            if (snapshot == null)
            {
                error = "request ownership is unavailable";
                return false;
            }

            if (_durable == null)
                return true;

            JToken sequenceToken = snapshot["plugin_sequence"];
            if (sequenceToken?.Type != JTokenType.Integer)
            {
                error = "plugin snapshot is missing a valid plugin_sequence";
                return false;
            }
            long sequence;
            try
            {
                sequence = (long)sequenceToken;
                if (sequence < 0) throw new InvalidCastException();
            }
            catch { error = "plugin snapshot sequence is invalid"; return false; }
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.ToString(Formatting.None))));

            lock (_gate)
            {
                if (!_requestOwners.TryGetValue(requestId ?? string.Empty, out RequestOwner stored)
                    || !string.Equals(stored.Ownership.ContextId, snapshot.Value<string>("context_id"), StringComparison.Ordinal))
                {
                    error = "request ownership is unavailable";
                    return false;
                }
                BoundPluginRequestOwnership owner = stored.Ownership;
                if (owner.PluginSequence.HasValue && sequence < owner.PluginSequence.Value)
                {
                    error = "plugin snapshot sequence moved backwards";
                    return false;
                }
                if (owner.PluginSequence.HasValue && sequence == owner.PluginSequence.Value)
                {
                    if (!string.Equals(owner.SnapshotDigest, digest, StringComparison.Ordinal))
                    {
                        error = "plugin returned conflicting snapshots for the same sequence";
                        return false;
                    }
                    return true;
                }
                if (_durable != null && !_durable.TryRecordSnapshot(requestId, sequence, digest, out error))
                    return false;
                owner.PluginSequence = sequence;
                owner.SnapshotDigest = digest;
                return true;
            }
        }

        private static bool SameEndpoint(DiscoveryInfo left, DiscoveryInfo right)
        {
            return left != null && right != null
                && string.Equals(left.Transport, right.Transport, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
                && left.Port == right.Port
                && string.Equals(left.PipeName, right.PipeName, StringComparison.Ordinal)
                && string.Equals(left.ControlPipeName, right.ControlPipeName, StringComparison.Ordinal)
                && string.Equals(left.Token, right.Token, StringComparison.Ordinal)
                && string.Equals(left.Target, right.Target, StringComparison.Ordinal)
                && left.Pid == right.Pid
                && left.StartedAtUtc?.ToUniversalTime().Ticks == right.StartedAtUtc?.ToUniversalTime().Ticks;
        }

        internal static bool IsValidIdentifier(string value) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Length <= CommonArgumentMcpServerTool.MaxRequestIdLength
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[value.Length - 1])
            && !value.Any(char.IsControl);

        private static bool IsJsonObject(string value)
        {
            try { return JObject.Parse(value) != null; }
            catch { return false; }
        }
    }
}
