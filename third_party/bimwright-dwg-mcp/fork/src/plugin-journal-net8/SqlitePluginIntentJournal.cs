using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bimwright.Dwg.Shared.Execution;
using Microsoft.Data.Sqlite;

namespace Bimwright.Dwg.Plugin.Execution;

/// <summary>
/// Durable, host-independent execution coordinator shared by AutoCAD instances.
/// Only this owner's GUID lock is held for the object's lifetime; SQLite's WAL
/// and short immediate transactions coordinate independent owners.
/// </summary>
public sealed class SqlitePluginIntentJournal : IPluginIntentJournal
{
    private const int SchemaVersion = 2;
    private const int BusyTimeoutMilliseconds = 15_000;
    private const int BusyTimeoutSeconds = BusyTimeoutMilliseconds / 1_000;
    private const string GlobalFenceKey = "\u0001GLOBAL_DRAWING_FENCE";
    private readonly string _databasePath;
    private readonly string _ownerLockDirectory;
    private readonly string _connectionString;
    private readonly IPluginOwnerLivenessProvider _livenessProvider;
    private readonly string _journalPathDigest;
    private readonly ReaderWriterLockSlim _lifecycle = new(LockRecursionPolicy.NoRecursion);
    private FileStream? _ownerLock;
    private string? _journalScopeId;
    private bool _initialized;
    private bool _disposed;
    private readonly string _ownerKey;

    public SqlitePluginIntentJournal(
        string databasePath,
        PluginOwnerIdentity owner,
        IPluginOwnerLivenessProvider livenessProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(livenessProvider);
        string ownerId = PluginOwnerLockIdentity.CanonicalOwnerGuid(owner.OwnerId);
        string generation = RequiredGeneration(owner.PluginGeneration, nameof(owner.PluginGeneration));
        if (owner.ProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(owner), "Owner PID must be positive.");

        Owner = new PluginOwnerIdentity(ownerId, owner.ProcessId, owner.ProcessStartedAtUtc, generation);
        _ownerKey = PluginOwnerLockIdentity.ComputeKey(Owner);
        _databasePath = Path.GetFullPath(databasePath);
        if (string.Equals(_databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A file-backed SQLite database is required.", nameof(databasePath));
        string? parent = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        _ownerLockDirectory = _databasePath + ".owners";
        _journalPathDigest = ComputeJournalPathDigest(_databasePath);
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds
        };
        _connectionString = connection.ToString();
        _livenessProvider = livenessProvider;
    }

    public PluginOwnerIdentity Owner { get; }

    /// <summary>Stable random identity persisted with this journal database.</summary>
    public string JournalScopeId => _journalScopeId
        ?? throw new InvalidOperationException("Journal initialization has not established its persistent scope identity.");

    /// <summary>Hash of the configured normalized database path; the raw path remains private.</summary>
    public string JournalPathDigest => _journalPathDigest;

    public int Initialize(DateTimeOffset nowUtc)
    {
        _lifecycle.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (_initialized) return 0;
            Directory.CreateDirectory(_ownerLockDirectory);
            string lockPath = PluginOwnerLockIdentity.GetLockPath(_databasePath, Owner);
            try
            {
                _ownerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                byte[] lockInfo = Encoding.UTF8.GetBytes(string.Join("\n", Owner.OwnerId, Owner.ProcessId.ToString(CultureInfo.InvariantCulture),
                    FormatTimestamp(Owner.ProcessStartedAtUtc), Owner.PluginGeneration));
                _ownerLock.SetLength(0);
                _ownerLock.Write(lockInfo, 0, lockInfo.Length);
                _ownerLock.Flush(flushToDisk: true);
            }
            catch (IOException exception)
            {
                _ownerLock?.Dispose();
                _ownerLock = null;
                throw new InvalidOperationException("Another plugin generation already holds this owner GUID lock.", exception);
            }

            try
            {
                InitializeDatabase();
                using (SqliteConnection connection = OpenConnection())
                using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
                using (SqliteCommand register = CreateCommand(connection, transaction, @"
INSERT OR IGNORE INTO plugin_owners(owner_key,owner_id,process_id,process_started_at_utc,plugin_generation,registered_at_utc)
VALUES($key,$id,$pid,$started,$generation,$registered);"))
                {
                    register.Parameters.AddWithValue("$key", _ownerKey);
                    register.Parameters.AddWithValue("$id", Owner.OwnerId);
                    register.Parameters.AddWithValue("$pid", Owner.ProcessId);
                    register.Parameters.AddWithValue("$started", FormatTimestamp(Owner.ProcessStartedAtUtc));
                    register.Parameters.AddWithValue("$generation", Owner.PluginGeneration);
                    register.Parameters.AddWithValue("$registered", FormatTimestamp(nowUtc));
                    register.ExecuteNonQuery();
                    transaction.Commit();
                }

                int recovered = RecoverLostOwnersCore(nowUtc);
                _initialized = true;
                return recovered;
            }
            catch
            {
                _ownerLock.Dispose();
                _ownerLock = null;
                throw;
            }
        }
        finally { _lifecycle.ExitWriteLock(); }
    }

    public PluginSubmitResult Submit(PluginJobSubmission request, DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        ArgumentNullException.ThrowIfNull(request);
        NormalizedSubmission normalized = NormalizeSubmission(request);
        DateTimeOffset now = nowUtc.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        PluginJobSnapshot? existing = ReadJob(connection, transaction, normalized.RequestId);
        if (existing is not null)
        {
            transaction.Rollback();
            bool same = string.Equals(existing.PayloadDigest, normalized.PayloadDigest, StringComparison.Ordinal);
            return new PluginSubmitResult(same ? PluginSubmitDisposition.Existing : PluginSubmitDisposition.RequestIdConflict, existing);
        }

        string[] conflicts = FindConflicts(connection, transaction, normalized);
        if (conflicts.Length > 0)
        {
            transaction.Rollback();
            return new PluginSubmitResult(PluginSubmitDisposition.FenceConflict, null, conflicts);
        }

        using (SqliteCommand insert = CreateCommand(connection, transaction, @"
INSERT INTO plugin_jobs(
 request_id,payload_digest,owner_key,owner_id,provider_generation,context_id,tool_name,canonical_parameters_json,
 complete_target_json,is_write,risk_keys_json,global_fence,phase,effects,verification,observation,plugin_sequence,
 result_json,error_code,error_message,created_at_utc,updated_at_utc,queue_deadline_utc,write_fence_released,
 readback_revision,readback_digest,verification_result_json,verification_error_code,verification_error_message)
VALUES($id,$digest,$owner_key,$owner_id,$generation,$context,$tool,$parameters,$target,$write,$keys,$global,
 'queued','none','not_checked','plugin_snapshot',0,NULL,NULL,NULL,$created,$updated,$deadline,0,NULL,NULL,NULL,NULL,NULL);"))
        {
            insert.Parameters.AddWithValue("$id", normalized.RequestId);
            insert.Parameters.AddWithValue("$digest", normalized.PayloadDigest);
            insert.Parameters.AddWithValue("$owner_key", _ownerKey);
            insert.Parameters.AddWithValue("$owner_id", Owner.OwnerId);
            insert.Parameters.AddWithValue("$generation", Owner.PluginGeneration);
            insert.Parameters.AddWithValue("$context", normalized.ContextId);
            insert.Parameters.AddWithValue("$tool", normalized.ToolName);
            insert.Parameters.AddWithValue("$parameters", normalized.ParametersJson);
            insert.Parameters.AddWithValue("$target", normalized.TargetJson);
            insert.Parameters.AddWithValue("$write", normalized.IsWrite ? 1 : 0);
            insert.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(normalized.RiskKeys));
            insert.Parameters.AddWithValue("$global", normalized.GlobalFence ? 1 : 0);
            insert.Parameters.AddWithValue("$created", FormatTimestamp(now));
            insert.Parameters.AddWithValue("$updated", FormatTimestamp(now));
            AddNullable(insert, "$deadline", normalized.QueueDeadlineUtc is DateTimeOffset deadline ? FormatTimestamp(deadline) : null);
            insert.ExecuteNonQuery();
        }

        foreach (string key in normalized.FenceKeys)
        {
            using SqliteCommand fence = CreateCommand(connection, transaction,
                "INSERT INTO plugin_write_fences(risk_key,request_id,created_at_utc) VALUES($key,$id,$created);");
            fence.Parameters.AddWithValue("$key", key);
            fence.Parameters.AddWithValue("$id", normalized.RequestId);
            fence.Parameters.AddWithValue("$created", FormatTimestamp(now));
            fence.ExecuteNonQuery();
        }

        PluginJobSnapshot created = ReadJob(connection, transaction, normalized.RequestId)!;
        transaction.Commit();
        return new PluginSubmitResult(PluginSubmitDisposition.Created, created);
    }

    public PluginJobSnapshot? GetSnapshot(string requestId)
    {
        using OperationLease lease = EnterOperation();
        string id = RequiredIdentifier(requestId, nameof(requestId));
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        PluginJobSnapshot? snapshot = ReadJob(connection, transaction, id);
        transaction.Commit();
        return snapshot;
    }

    public PluginHistoricalJobResult GetHistoricalSnapshot(string requestId)
    {
        using OperationLease lease = EnterOperation();
        string id = RequiredIdentifier(requestId, nameof(requestId));
        PluginJobSnapshot? snapshot;
        PluginOwnerIdentity? oldOwner = null;
        bool ownerRowMatches = false;
        try
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            snapshot = ReadJob(connection, transaction, id);
            if (snapshot != null)
            {
                using SqliteCommand ownerQuery = CreateCommand(connection, transaction, @"
SELECT o.owner_key,o.owner_id,o.process_id,o.process_started_at_utc,o.plugin_generation
FROM plugin_jobs j LEFT JOIN plugin_owners o ON o.owner_key=j.owner_key
WHERE j.request_id=$id;");
                ownerQuery.Parameters.AddWithValue("$id", id);
                using SqliteDataReader reader = ownerQuery.ExecuteReader();
                if (reader.Read() && !reader.IsDBNull(0))
                {
                    string ownerKey = reader.GetString(0);
                    var candidate = new PluginOwnerIdentity(reader.GetString(1), reader.GetInt32(2),
                        ParseTimestamp(reader.GetString(3)), reader.GetString(4));
                    oldOwner = candidate;
                    ownerRowMatches = string.Equals(candidate.OwnerId, snapshot.OwnerId, StringComparison.Ordinal)
                        && string.Equals(candidate.PluginGeneration, snapshot.ProviderGeneration, StringComparison.Ordinal)
                        && string.Equals(PluginOwnerLockIdentity.ComputeKey(candidate), ownerKey, StringComparison.Ordinal);
                }
                reader.Close();
            }
            transaction.Commit();
        }
        catch
        {
            return new PluginHistoricalJobResult(false, null, Owner, PluginOwnerLiveness.Indeterminate);
        }

        if (snapshot == null)
            return new PluginHistoricalJobResult(false, null, Owner, PluginOwnerLiveness.Indeterminate);
        if (!ownerRowMatches || oldOwner == null)
            return new PluginHistoricalJobResult(true, snapshot, Owner, PluginOwnerLiveness.Indeterminate);

        PluginOwnerLiveness liveness;
        try { liveness = _livenessProvider.GetLiveness(oldOwner); }
        catch { liveness = PluginOwnerLiveness.Indeterminate; }
        if (!Enum.IsDefined(liveness)) liveness = PluginOwnerLiveness.Indeterminate;
        return new PluginHistoricalJobResult(true, snapshot, Owner, liveness);
    }

    public PluginCancelResult CancelQueued(string requestId, DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        string id = RequiredIdentifier(requestId, nameof(requestId));
        DateTimeOffset now = nowUtc.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PluginJobSnapshot? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new PluginCancelResult(PluginCancelDisposition.NotFound, null);
        }
        if (!IsCurrentOwner(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginCancelResult(PluginCancelDisposition.Rejected, current, "job_belongs_to_another_owner");
        }
        if (IsTerminal(current.Phase))
        {
            transaction.Rollback();
            return new PluginCancelResult(PluginCancelDisposition.AlreadyTerminal, current);
        }

        if (current.Phase == PluginJobPhase.Queued)
        {
            bool expired = current.QueueDeadlineUtc.HasValue && current.QueueDeadlineUtc.Value <= now;
            UpdatePhase(connection, transaction, id, _ownerKey, PluginJobPhase.Cancelled, PluginEffectState.None,
                expired ? "queue_deadline_exceeded" : null,
                expired ? "The job queue deadline expired before it could be claimed." : null,
                now, current.PluginSequence + 1);
            ReleaseFenceIfSafe(connection, transaction, id, _ownerKey, release: true, now);
            PluginJobSnapshot cancelled = ReadJob(connection, transaction, id)!;
            transaction.Commit();
            return new PluginCancelResult(PluginCancelDisposition.Cancelled, cancelled,
                expired ? "queue_deadline_exceeded" : null);
        }

        if (current.Phase == PluginJobPhase.Running)
        {
            UpdatePhase(connection, transaction, id, _ownerKey, PluginJobPhase.CancellationRequested, current.Effects,
                current.ErrorCode, current.ErrorMessage, now, current.PluginSequence + 1, current.ResultJson);
            PluginJobSnapshot requested = ReadJob(connection, transaction, id)!;
            transaction.Commit();
            return new PluginCancelResult(PluginCancelDisposition.CancellationRequested, requested);
        }

        transaction.Rollback();
        return new PluginCancelResult(PluginCancelDisposition.Rejected, current, "job_is_not_cancellable");
    }

    public PluginClaimResult TryClaimNext(DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        DateTimeOffset now = nowUtc.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using (SqliteCommand active = CreateCommand(connection, transaction,
            "SELECT 1 FROM plugin_jobs WHERE owner_key=$owner AND phase IN('running','cancellation_requested') LIMIT 1;"))
        {
            active.Parameters.AddWithValue("$owner", _ownerKey);
            if (active.ExecuteScalar() is not null)
            {
                transaction.Rollback();
                return new PluginClaimResult(PluginClaimDisposition.NoWork, null);
            }
        }

        string? headId = null;
        using (SqliteCommand head = CreateCommand(connection, transaction, @"
SELECT request_id FROM plugin_jobs WHERE owner_key=$owner AND phase='queued'
ORDER BY job_order LIMIT 1;"))
        {
            head.Parameters.AddWithValue("$owner", _ownerKey);
            headId = head.ExecuteScalar() as string;
        }
        if (headId is null)
        {
            transaction.Rollback();
            return new PluginClaimResult(PluginClaimDisposition.NoWork, null);
        }

        PluginJobSnapshot queued = ReadJob(connection, transaction, headId)!;
        if (queued.QueueDeadlineUtc.HasValue && queued.QueueDeadlineUtc.Value <= now)
        {
            UpdatePhase(connection, transaction, headId, _ownerKey, PluginJobPhase.Cancelled, PluginEffectState.None,
                "queue_deadline_exceeded", "The job queue deadline expired before it could be claimed.",
                now, queued.PluginSequence + 1);
            ReleaseFenceIfSafe(connection, transaction, headId, _ownerKey, release: true, now);
            PluginJobSnapshot expired = ReadJob(connection, transaction, headId)!;
            transaction.Commit();
            return new PluginClaimResult(PluginClaimDisposition.QueueDeadlineExpired, expired, headId);
        }

        UpdatePhase(connection, transaction, headId, _ownerKey, PluginJobPhase.Running,
            queued.IsWrite ? PluginEffectState.Unknown : PluginEffectState.None,
            null, null, now, queued.PluginSequence + 1);
        PluginJobSnapshot claimed = ReadJob(connection, transaction, headId)!;
        transaction.Commit();
        return new PluginClaimResult(PluginClaimDisposition.Claimed, claimed);
    }

    public PluginTransitionResult RecordCommitted(string requestId, string resultJson, DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        string id = RequiredIdentifier(requestId, nameof(requestId));
        DateTimeOffset now = nowUtc.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PluginJobSnapshot? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.NotFound, null, "request_id_not_found");
        }
        if (!IsCurrentOwner(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "job_belongs_to_another_owner");
        }
        if (!current.IsWrite || current.Phase is not (PluginJobPhase.Running or PluginJobPhase.CancellationRequested))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "commit_requires_current_owner_running_write");
        }
        if (current.Effects == PluginEffectState.Committed)
        {
            bool same = string.Equals(current.ResultJson, resultJson, StringComparison.Ordinal)
                && string.Equals(current.CommittedResultDigest, ComputeContentDigest(resultJson), StringComparison.Ordinal);
            transaction.Rollback();
            return same
                ? new PluginTransitionResult(PluginTransitionDisposition.Duplicate, current)
                : new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "committed_result_conflict");
        }
        UpdatePhase(connection, transaction, id, _ownerKey, current.Phase, PluginEffectState.Committed,
            current.ErrorCode, current.ErrorMessage, now, current.PluginSequence + 1, resultJson);
        using (SqliteCommand digest = CreateCommand(connection, transaction, @"
UPDATE plugin_jobs SET committed_result_digest=$digest
WHERE request_id=$id AND owner_key=$owner AND effects='committed';"))
        {
            digest.Parameters.AddWithValue("$digest", ComputeContentDigest(resultJson));
            digest.Parameters.AddWithValue("$id", id);
            digest.Parameters.AddWithValue("$owner", _ownerKey);
            if (digest.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The committed result digest was not persisted with the write.");
        }
        PluginJobSnapshot committed = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new PluginTransitionResult(PluginTransitionDisposition.Applied, committed);
    }

    public PluginTransitionResult RecordTerminal(
        string requestId,
        PluginJobPhase phase,
        PluginEffectState effects,
        string? resultJson,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        string id = RequiredIdentifier(requestId, nameof(requestId));
        EnsureEnumValue(phase, nameof(phase));
        EnsureEnumValue(effects, nameof(effects));
        DateTimeOffset now = nowUtc.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PluginJobSnapshot? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.NotFound, null, "request_id_not_found");
        }
        if (!IsCurrentOwner(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "job_belongs_to_another_owner");
        }
        if (IsTerminal(current.Phase))
        {
            bool duplicate = current.Phase == phase && current.Effects == effects
                && current.ResultJson == resultJson && current.ErrorCode == errorCode && current.ErrorMessage == errorMessage;
            transaction.Rollback();
            return duplicate
                ? new PluginTransitionResult(PluginTransitionDisposition.Duplicate, current)
                : new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "terminal_phase_is_immutable");
        }
        if (current.Phase is not (PluginJobPhase.Running or PluginJobPhase.CancellationRequested))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "terminal_transition_requires_started_job");
        }
        if (phase is not (PluginJobPhase.Completed or PluginJobPhase.Failed or PluginJobPhase.Unknown))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "invalid_terminal_phase");
        }
        if (!CanChangeEffects(current, phase, effects, out string reason))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, reason);
        }
        if (phase == PluginJobPhase.Completed && current.IsWrite && effects != PluginEffectState.Committed)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "completed_write_must_report_committed_effects");
        }
        if (phase == PluginJobPhase.Completed && !current.IsWrite && effects != PluginEffectState.None)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "completed_read_must_have_no_effects");
        }
        if (phase == PluginJobPhase.Unknown && current.IsWrite && effects != PluginEffectState.Unknown
            && effects != PluginEffectState.Committed)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "unknown_write_requires_unknown_or_committed_effects");
        }

        UpdatePhase(connection, transaction, id, _ownerKey, phase, effects, errorCode, errorMessage,
            now, current.PluginSequence + 1, resultJson);
        bool release = phase == PluginJobPhase.Failed && effects == PluginEffectState.None;
        ReleaseFenceIfSafe(connection, transaction, id, _ownerKey, release, now);
        PluginJobSnapshot updated = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new PluginTransitionResult(PluginTransitionDisposition.Applied, updated);
    }

    /// <summary>
    /// Records one narrowly typed pre-commit no-effect outcome. Unlike the
    /// generic terminal API, this transition checks the exact claimed job and
    /// releases only its own persisted risk keys in the same transaction.
    /// </summary>
    public PluginTransitionResult RecordNoEffectTerminal(
        PluginNoEffectTerminalRequest request,
        DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        ArgumentNullException.ThrowIfNull(request);
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        string ownerId = PluginOwnerLockIdentity.CanonicalOwnerGuid(
            RequiredIdentifier(request.OwnerId, nameof(request.OwnerId)));
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        string digest = RequiredDigest(request.PayloadDigest, nameof(request.PayloadDigest));
        if (request.ClaimSequence < 0) throw new ArgumentOutOfRangeException(nameof(request.ClaimSequence));
        EnsureEnumValue(request.Outcome, nameof(request.Outcome));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ErrorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ErrorMessage);
        DateTimeOffset now = nowUtc.ToUniversalTime();

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PluginJobSnapshot? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.NotFound, null, "request_id_not_found");
        }
        if (!IsCurrentOwner(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "job_belongs_to_another_owner");
        }
        if (!MatchesWriteIdentity(current, context, target, ownerId, generation, digest))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "no_effect_identity_mismatch");
        }

        long terminalSequence = NextSequence(request.ClaimSequence);
        if (current.Phase == PluginJobPhase.Failed && current.Effects == PluginEffectState.None
            && current.WriteFenceReleased && current.NoEffectClaimSequence == request.ClaimSequence
            && current.NoEffectOutcome == request.Outcome && current.PluginSequence == terminalSequence
            && current.ErrorCode == request.ErrorCode && current.ErrorMessage == request.ErrorMessage
            && !HasRiskFence(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Duplicate, current);
        }

        if (!current.IsWrite || current.Phase != PluginJobPhase.Running
            || current.Effects != PluginEffectState.Unknown
            || current.PluginSequence != request.ClaimSequence
            || current.Verification != PluginVerificationState.NotChecked
            || current.ResultJson is not null || current.ReadbackRevision.HasValue
            || current.ReadbackDigest is not null || current.VerificationResultJson is not null
            || current.VerificationErrorCode is not null || current.VerificationErrorMessage is not null
            || current.CommittedResultDigest is not null || current.VerificationStagedSequence.HasValue
            || current.WriteFenceReleased || current.WriteFenceFinalized
            || current.NoEffectClaimSequence.HasValue
            || !HasRiskFence(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current,
                "no_effect_transition_requires_current_uncommitted_running_write_claim");
        }

        UpdatePhase(connection, transaction, id, _ownerKey, PluginJobPhase.Failed,
            PluginEffectState.None, request.ErrorCode, request.ErrorMessage, now, terminalSequence);
        using (SqliteCommand noEffect = CreateCommand(connection, transaction, @"
UPDATE plugin_jobs SET no_effect_claim_sequence=$claim_sequence,no_effect_outcome=$outcome
WHERE request_id=$id AND owner_key=$owner AND plugin_sequence=$sequence;"))
        {
            noEffect.Parameters.AddWithValue("$claim_sequence", request.ClaimSequence);
            noEffect.Parameters.AddWithValue("$outcome", ToDatabaseValue(request.Outcome));
            noEffect.Parameters.AddWithValue("$id", id);
            noEffect.Parameters.AddWithValue("$owner", _ownerKey);
            noEffect.Parameters.AddWithValue("$sequence", terminalSequence);
            if (noEffect.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The claimed no-effect job changed during its journal transaction.");
        }
        ReleaseFenceIfSafe(connection, transaction, id, _ownerKey, release: true, now);
        PluginJobSnapshot updated = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new PluginTransitionResult(PluginTransitionDisposition.Applied, updated);
    }

    public PluginTransitionResult RecordVerification(PluginVerificationRequest request, DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        ArgumentNullException.ThrowIfNull(request);
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        if (request.ReadbackRevision < 0) throw new ArgumentOutOfRangeException(nameof(request.ReadbackRevision));
        EnsureEnumValue(request.Verification, nameof(request.Verification));
        if (request.Verification == PluginVerificationState.NotChecked)
            throw new ArgumentException("Readback must report Passed, Failed, or Unknown.", nameof(request));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        DateTimeOffset now = nowUtc.ToUniversalTime();

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PluginJobSnapshot? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.NotFound, null, "request_id_not_found");
        }
        if (!IsCurrentOwner(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "job_belongs_to_another_owner");
        }
        if (!current.IsWrite || current.Phase != PluginJobPhase.Completed || current.Effects != PluginEffectState.Committed)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "verification_requires_completed_committed_write");
        }
        if (current.ContextId != context || current.CompleteTargetJson != target || current.ProviderGeneration != generation)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "verification_identity_mismatch");
        }
        if (current.VerificationStagedSequence.HasValue || current.WriteFenceFinalized)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current,
                "staged_verification_requires_fence_finalization");
        }

        string digest = ComputeVerificationDigest(id, context, target, generation, request);
        if (current.ReadbackRevision.HasValue)
        {
            if (request.ReadbackRevision < current.ReadbackRevision.Value)
            {
                transaction.Rollback();
                return new PluginTransitionResult(PluginTransitionDisposition.StaleRevision, current, "older_readback_revision");
            }
            if (request.ReadbackRevision == current.ReadbackRevision.Value)
            {
                bool same = string.Equals(current.ReadbackDigest, digest, StringComparison.Ordinal);
                transaction.Rollback();
                return same
                    ? new PluginTransitionResult(PluginTransitionDisposition.Duplicate, current)
                    : new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "same_revision_snapshot_conflict");
            }
        }
        if (current.Verification == PluginVerificationState.Passed && request.Verification != PluginVerificationState.Passed)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "verification_cannot_regress_after_pass");
        }

        bool release = request.Verification == PluginVerificationState.Passed;
        using (SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE plugin_jobs SET verification=$verification,readback_revision=$revision,readback_digest=$digest,
 verification_result_json=$result,verification_error_code=$code,verification_error_message=$message,
 updated_at_utc=$updated,write_fence_released=CASE WHEN $release=1 THEN 1 ELSE write_fence_released END
WHERE request_id=$id AND owner_key=$owner;"))
        {
            update.Parameters.AddWithValue("$verification", ToDatabaseValue(request.Verification));
            update.Parameters.AddWithValue("$revision", request.ReadbackRevision);
            update.Parameters.AddWithValue("$digest", digest);
            AddNullable(update, "$result", request.ResultJson);
            AddNullable(update, "$code", request.ErrorCode);
            AddNullable(update, "$message", request.ErrorMessage);
            update.Parameters.AddWithValue("$updated", FormatTimestamp(now));
            update.Parameters.AddWithValue("$release", release ? 1 : 0);
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$owner", _ownerKey);
            update.ExecuteNonQuery();
        }
        if (release) ReleaseFence(connection, transaction, id, _ownerKey);
        PluginJobSnapshot verified = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new PluginTransitionResult(PluginTransitionDisposition.Applied, verified);
    }

    /// <summary>
    /// Persists independent readback without releasing the write risk fence.
    /// The expected plugin sequence binds the readback to one exact completed
    /// snapshot; retries of the same stage are idempotent.
    /// </summary>
    public PluginTransitionResult RecordVerificationPendingRelease(
        PluginVerificationRequest request,
        DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        ArgumentNullException.ThrowIfNull(request);
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        string ownerId = PluginOwnerLockIdentity.CanonicalOwnerGuid(
            RequiredIdentifier(request.OwnerId, nameof(request.OwnerId)));
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        string payloadDigest = RequiredDigest(request.PayloadDigest, nameof(request.PayloadDigest));
        string committedResultDigest = RequiredDigest(request.CommittedResultDigest, nameof(request.CommittedResultDigest));
        if (request.ExpectedPluginSequence is not long expectedSequence || expectedSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedPluginSequence));
        if (request.ReadbackRevision < 0) throw new ArgumentOutOfRangeException(nameof(request.ReadbackRevision));
        EnsureEnumValue(request.Verification, nameof(request.Verification));
        if (request.Verification == PluginVerificationState.NotChecked)
            throw new ArgumentException("Staged readback must report Passed, Failed, or Unknown.", nameof(request));
        ValidateOptionalJsonObject(request.ResultJson, nameof(request.ResultJson));
        if (request.Verification == PluginVerificationState.Passed
            && (request.ResultJson is null || request.ErrorCode is not null || request.ErrorMessage is not null))
            throw new ArgumentException("Passed verification requires raw readback JSON and cannot include an error.", nameof(request));
        if (request.Verification != PluginVerificationState.Passed
            && request.ResultJson is null && string.IsNullOrWhiteSpace(request.ErrorCode)
            && string.IsNullOrWhiteSpace(request.ErrorMessage))
            throw new ArgumentException("Failed or unknown verification requires readback data or an error.", nameof(request));

        DateTimeOffset now = nowUtc.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PluginJobSnapshot? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.NotFound, null, "request_id_not_found");
        }
        if (!IsCurrentOwner(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "job_belongs_to_another_owner");
        }
        if (!MatchesWriteIdentity(current, context, target, ownerId, generation, payloadDigest)
            || !string.Equals(current.CommittedResultDigest, committedResultDigest, StringComparison.Ordinal)
            || current.Phase != PluginJobPhase.Completed || current.Effects != PluginEffectState.Committed)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current,
                "staged_verification_requires_exact_completed_committed_write");
        }

        string readbackDigest = ComputeStagedVerificationDigest(request);
        if (current.ReadbackRevision.HasValue)
        {
            if (request.ReadbackRevision < current.ReadbackRevision.Value)
            {
                transaction.Rollback();
                return new PluginTransitionResult(PluginTransitionDisposition.StaleRevision, current, "older_readback_revision");
            }
            if (request.ReadbackRevision == current.ReadbackRevision.Value)
            {
                bool same = string.Equals(current.ReadbackDigest, readbackDigest, StringComparison.Ordinal)
                    && current.VerificationStagedSequence.HasValue;
                transaction.Rollback();
                return same
                    ? new PluginTransitionResult(PluginTransitionDisposition.Duplicate, current)
                    : new PluginTransitionResult(PluginTransitionDisposition.Rejected, current,
                        "same_revision_snapshot_conflict");
            }
        }
        if (current.Verification == PluginVerificationState.Passed
            && request.Verification != PluginVerificationState.Passed)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current,
                "verification_cannot_regress_after_pass");
        }
        if (current.WriteFenceReleased || current.WriteFenceFinalized
            || !HasRiskFence(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current,
                "staged_verification_requires_held_write_fence");
        }
        if (current.PluginSequence != expectedSequence)
        {
            transaction.Rollback();
            return new PluginTransitionResult(
                expectedSequence < current.PluginSequence
                    ? PluginTransitionDisposition.StaleRevision : PluginTransitionDisposition.Rejected,
                current, "completed_snapshot_sequence_mismatch");
        }

        long stagedSequence = NextSequence(current.PluginSequence);
        using (SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE plugin_jobs SET verification=$verification,readback_revision=$revision,readback_digest=$digest,
 verification_result_json=$result,verification_error_code=$code,verification_error_message=$message,
 verification_staged_sequence=$staged_sequence,write_fence_finalized=0,plugin_sequence=$plugin_sequence,
 updated_at_utc=$updated WHERE request_id=$id AND owner_key=$owner AND plugin_sequence=$expected_sequence
 AND write_fence_released=0;"))
        {
            update.Parameters.AddWithValue("$verification", ToDatabaseValue(request.Verification));
            update.Parameters.AddWithValue("$revision", request.ReadbackRevision);
            update.Parameters.AddWithValue("$digest", readbackDigest);
            AddNullable(update, "$result", request.ResultJson);
            AddNullable(update, "$code", request.ErrorCode);
            AddNullable(update, "$message", request.ErrorMessage);
            update.Parameters.AddWithValue("$staged_sequence", stagedSequence);
            update.Parameters.AddWithValue("$plugin_sequence", stagedSequence);
            update.Parameters.AddWithValue("$updated", FormatTimestamp(now));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$owner", _ownerKey);
            update.Parameters.AddWithValue("$expected_sequence", expectedSequence);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The completed job changed while staging readback.");
        }
        PluginJobSnapshot staged = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new PluginTransitionResult(PluginTransitionDisposition.Applied, staged);
    }

    /// <summary>
    /// Releases only the specified request's original risk keys after its exact
    /// staged Passed readback has been confirmed. Finalization is idempotent
    /// across an acknowledgement loss and advances the plugin sequence once.
    /// </summary>
    public PluginTransitionResult FinalizeVerifiedWriteFence(
        PluginFinalizeVerifiedWriteFenceRequest request,
        DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        ArgumentNullException.ThrowIfNull(request);
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        string ownerId = PluginOwnerLockIdentity.CanonicalOwnerGuid(request.OwnerId);
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        string payloadDigest = RequiredDigest(request.PayloadDigest, nameof(request.PayloadDigest));
        string committedResultDigest = RequiredDigest(request.CommittedResultDigest, nameof(request.CommittedResultDigest));
        string readbackDigest = RequiredDigest(request.ReadbackDigest, nameof(request.ReadbackDigest));
        if (request.StagedPluginSequence < 0) throw new ArgumentOutOfRangeException(nameof(request.StagedPluginSequence));
        if (request.ReadbackRevision < 0) throw new ArgumentOutOfRangeException(nameof(request.ReadbackRevision));
        DateTimeOffset now = nowUtc.ToUniversalTime();

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PluginJobSnapshot? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.NotFound, null, "request_id_not_found");
        }
        if (!IsCurrentOwner(connection, transaction, id))
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current, "job_belongs_to_another_owner");
        }
        if (!MatchesWriteIdentity(current, context, target, ownerId, generation, payloadDigest)
            || !string.Equals(current.CommittedResultDigest, committedResultDigest, StringComparison.Ordinal)
            || current.Phase != PluginJobPhase.Completed || current.Effects != PluginEffectState.Committed
            || current.Verification != PluginVerificationState.Passed)
        {
            transaction.Rollback();
            return new PluginTransitionResult(PluginTransitionDisposition.Rejected, current,
                "finalize_requires_exact_passed_committed_write");
        }

        long finalizedSequence = NextSequence(request.StagedPluginSequence);
        bool confirmationMatches = current.VerificationStagedSequence == request.StagedPluginSequence
            && current.ReadbackRevision == request.ReadbackRevision
            && string.Equals(current.ReadbackDigest, readbackDigest, StringComparison.Ordinal);
        if (current.WriteFenceFinalized)
        {
            bool duplicate = confirmationMatches && current.WriteFenceReleased
                && current.PluginSequence == finalizedSequence
                && !HasRiskFence(connection, transaction, id);
            transaction.Rollback();
            return duplicate
                ? new PluginTransitionResult(PluginTransitionDisposition.Duplicate, current)
                : new PluginTransitionResult(PluginTransitionDisposition.Rejected, current,
                    "finalization_confirmation_conflict");
        }
        if (current.WriteFenceReleased || !confirmationMatches || !HasRiskFence(connection, transaction, id))
        {
            transaction.Rollback();
            PluginTransitionDisposition disposition = current.VerificationStagedSequence is long staged
                && request.StagedPluginSequence < staged
                ? PluginTransitionDisposition.StaleRevision : PluginTransitionDisposition.Rejected;
            return new PluginTransitionResult(disposition, current, "staged_fence_confirmation_mismatch");
        }
        if (current.PluginSequence != request.StagedPluginSequence)
        {
            transaction.Rollback();
            return new PluginTransitionResult(
                request.StagedPluginSequence < current.PluginSequence
                    ? PluginTransitionDisposition.StaleRevision : PluginTransitionDisposition.Rejected,
                current, "staged_plugin_sequence_mismatch");
        }

        using (SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE plugin_jobs SET write_fence_released=1,write_fence_finalized=1,
 plugin_sequence=$finalized_sequence,updated_at_utc=$updated
WHERE request_id=$id AND owner_key=$owner AND plugin_sequence=$staged_sequence
 AND verification_staged_sequence=$staged_sequence AND verification='passed'
 AND readback_revision=$revision AND readback_digest=$digest AND write_fence_released=0;"))
        {
            update.Parameters.AddWithValue("$finalized_sequence", finalizedSequence);
            update.Parameters.AddWithValue("$updated", FormatTimestamp(now));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$owner", _ownerKey);
            update.Parameters.AddWithValue("$staged_sequence", request.StagedPluginSequence);
            update.Parameters.AddWithValue("$revision", request.ReadbackRevision);
            update.Parameters.AddWithValue("$digest", readbackDigest);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The staged write changed during fence finalization.");
        }
        ReleaseFence(connection, transaction, id, _ownerKey);
        PluginJobSnapshot finalized = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new PluginTransitionResult(PluginTransitionDisposition.Applied, finalized);
    }

    public int RecoverLostOwners(DateTimeOffset nowUtc)
    {
        using OperationLease lease = EnterOperation();
        return RecoverLostOwnersCore(nowUtc);
    }

    public void Dispose()
    {
        _lifecycle.EnterWriteLock();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _initialized = false;
            _ownerLock?.Dispose();
            _ownerLock = null;
        }
        finally
        {
            _lifecycle.ExitWriteLock();
            GC.SuppressFinalize(this);
        }
    }

    private void InitializeDatabase()
    {
        using SqliteConnection connection = OpenConnection();
        using (SqliteCommand wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteScalar();
        }
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using (SqliteCommand meta = CreateCommand(connection, transaction,
            "CREATE TABLE IF NOT EXISTS plugin_journal_meta(meta_key TEXT PRIMARY KEY COLLATE BINARY,meta_value INTEGER NOT NULL);"))
            meta.ExecuteNonQuery();
        long? existingSchemaVersion;
        using (SqliteCommand version = CreateCommand(connection, transaction,
            "SELECT meta_value FROM plugin_journal_meta WHERE meta_key='schema_version';"))
        {
            object? existing = version.ExecuteScalar();
            existingSchemaVersion = existing is null ? null : (long)existing;
            if (existingSchemaVersion > SchemaVersion)
                throw new InvalidOperationException("Plugin journal schema is newer than this library.");
        }
        using (SqliteCommand schema = CreateCommand(connection, transaction, @"
CREATE TABLE IF NOT EXISTS plugin_owners(
 owner_key TEXT PRIMARY KEY COLLATE BINARY,owner_id TEXT NOT NULL,process_id INTEGER NOT NULL,
 process_started_at_utc TEXT NOT NULL,plugin_generation TEXT NOT NULL,registered_at_utc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS plugin_jobs(
 job_order INTEGER PRIMARY KEY AUTOINCREMENT,request_id TEXT NOT NULL UNIQUE COLLATE BINARY,
 payload_digest TEXT NOT NULL,owner_key TEXT NOT NULL REFERENCES plugin_owners(owner_key),owner_id TEXT NOT NULL,
 provider_generation TEXT NOT NULL,context_id TEXT NOT NULL,tool_name TEXT NOT NULL,
 canonical_parameters_json TEXT NOT NULL,complete_target_json TEXT NOT NULL,
 is_write INTEGER NOT NULL CHECK(is_write IN(0,1)),risk_keys_json TEXT NOT NULL,
 global_fence INTEGER NOT NULL CHECK(global_fence IN(0,1)),phase TEXT NOT NULL,effects TEXT NOT NULL,
 verification TEXT NOT NULL,observation TEXT NOT NULL,plugin_sequence INTEGER NOT NULL,
 result_json TEXT NULL,error_code TEXT NULL,error_message TEXT NULL,created_at_utc TEXT NOT NULL,
 updated_at_utc TEXT NOT NULL,queue_deadline_utc TEXT NULL,
 write_fence_released INTEGER NOT NULL DEFAULT 0 CHECK(write_fence_released IN(0,1)),
 readback_revision INTEGER NULL,readback_digest TEXT NULL,verification_result_json TEXT NULL,
 verification_error_code TEXT NULL,verification_error_message TEXT NULL,
 committed_result_digest TEXT NULL,verification_staged_sequence INTEGER NULL,
 write_fence_finalized INTEGER NOT NULL DEFAULT 0 CHECK(write_fence_finalized IN(0,1)),
 no_effect_claim_sequence INTEGER NULL,no_effect_outcome TEXT NULL);
CREATE TABLE IF NOT EXISTS plugin_write_fences(
 risk_key TEXT PRIMARY KEY COLLATE BINARY,request_id TEXT NOT NULL REFERENCES plugin_jobs(request_id) ON DELETE CASCADE,
 created_at_utc TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_plugin_jobs_owner_phase_order ON plugin_jobs(owner_key,phase,job_order);
CREATE INDEX IF NOT EXISTS ix_plugin_jobs_owner_id ON plugin_jobs(owner_id,provider_generation);
CREATE TABLE IF NOT EXISTS plugin_journal_identity(
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),scope_id TEXT NOT NULL,path_digest TEXT NOT NULL);"))
        {
            schema.ExecuteNonQuery();
        }
        EnsureColumn(connection, transaction, "committed_result_digest", "TEXT NULL");
        EnsureColumn(connection, transaction, "verification_staged_sequence", "INTEGER NULL");
        EnsureColumn(connection, transaction, "write_fence_finalized", "INTEGER NOT NULL DEFAULT 0 CHECK(write_fence_finalized IN(0,1))");
        EnsureColumn(connection, transaction, "no_effect_claim_sequence", "INTEGER NULL");
        EnsureColumn(connection, transaction, "no_effect_outcome", "TEXT NULL");
        string? journalScopeId = null;
        using (SqliteCommand identityQuery = CreateCommand(connection, transaction,
            "SELECT scope_id,path_digest FROM plugin_journal_identity WHERE singleton=1;"))
        using (SqliteDataReader reader = identityQuery.ExecuteReader())
        {
            if (reader.Read())
            {
                string scopeId = reader.GetString(0);
                string pathDigest = reader.GetString(1);
                if (!Guid.TryParseExact(scopeId, "N", out _)
                    || !string.Equals(pathDigest, _journalPathDigest, StringComparison.Ordinal))
                    throw new InvalidOperationException("The plugin execution journal identity does not match its configured path.");
                journalScopeId = scopeId;
            }
        }
        if (journalScopeId is null)
        {
            using SqliteCommand hasJobs = CreateCommand(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM plugin_jobs LIMIT 1);");
            bool existingJobs = Convert.ToInt64(hasJobs.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
            if (existingSchemaVersion >= SchemaVersion || (!existingSchemaVersion.HasValue && existingJobs))
                throw new InvalidOperationException("The persisted plugin journal scope identity is missing; refusing to generate a replacement.");

            string scopeId = Guid.NewGuid().ToString("N");
            using SqliteCommand identityInsert = CreateCommand(connection, transaction,
                "INSERT INTO plugin_journal_identity(singleton,scope_id,path_digest) VALUES(1,$scope,$digest);");
            identityInsert.Parameters.AddWithValue("$scope", scopeId);
            identityInsert.Parameters.AddWithValue("$digest", _journalPathDigest);
            identityInsert.ExecuteNonQuery();
            journalScopeId = scopeId;
        }
        using (SqliteCommand version = CreateCommand(connection, transaction, @"
INSERT INTO plugin_journal_meta(meta_key,meta_value) VALUES('schema_version',$version)
ON CONFLICT(meta_key) DO UPDATE SET meta_value=excluded.meta_value;"))
        {
            version.Parameters.AddWithValue("$version", SchemaVersion);
            version.ExecuteNonQuery();
        }
        transaction.Commit();
        _journalScopeId = journalScopeId;
    }

    private int RecoverLostOwnersCore(DateTimeOffset nowUtc)
    {
        List<StoredOwner> owners = ReadOwners();
        var lost = new List<string>();
        foreach (StoredOwner row in owners)
        {
            if (string.Equals(row.OwnerKey, _ownerKey, StringComparison.Ordinal)) continue;
            PluginOwnerLiveness status;
            try { status = _livenessProvider.GetLiveness(row.ToIdentity()); }
            catch { status = PluginOwnerLiveness.Indeterminate; }
            if (status == PluginOwnerLiveness.ConfirmedLost) lost.Add(row.OwnerKey);
        }
        if (lost.Count == 0) return 0;

        int changed = 0;
        DateTimeOffset now = nowUtc.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        foreach (string ownerKey in lost)
        {
            using SqliteCommand overflow = CreateCommand(connection, transaction, @"
SELECT COUNT(*) FROM plugin_jobs WHERE owner_key=$owner AND plugin_sequence=9223372036854775807
 AND ((phase IN('queued','running','cancellation_requested') AND observation<>'recovery')
      OR (phase='unknown' AND observation<>'recovery'));");
            overflow.Parameters.AddWithValue("$owner", ownerKey);
            if (Convert.ToInt64(overflow.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new OverflowException("Plugin recovery cannot increment a maximum plugin_sequence.");
        }
        foreach (string ownerKey in lost)
        {
            using SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE plugin_jobs SET phase='unknown',observation='recovery',updated_at_utc=$updated,
 plugin_sequence=plugin_sequence+1,
 effects=CASE WHEN is_write=1 AND phase IN('running','cancellation_requested') AND effects='none' THEN 'unknown' ELSE effects END
WHERE owner_key=$owner AND plugin_sequence<9223372036854775807 AND (
 (phase IN('queued','running','cancellation_requested') AND observation<>'recovery')
 OR (phase='unknown' AND observation<>'recovery'));");
            update.Parameters.AddWithValue("$updated", FormatTimestamp(now));
            update.Parameters.AddWithValue("$owner", ownerKey);
            changed += update.ExecuteNonQuery();
        }
        transaction.Commit();
        return changed;
    }

    private List<StoredOwner> ReadOwners()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT owner_key,owner_id,process_id,process_started_at_utc,plugin_generation FROM plugin_owners;";
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<StoredOwner>();
        while (reader.Read())
            rows.Add(new StoredOwner(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                ParseTimestamp(reader.GetString(3)), reader.GetString(4)));
        return rows;
    }

    private NormalizedSubmission NormalizeSubmission(PluginJobSubmission request)
    {
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string tool = RequiredIdentifier(request.ToolName, nameof(request.ToolName));
        string parameters = CanonicalizeJsonObject(request.CanonicalParametersJson, nameof(request.CanonicalParametersJson));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        string[] keys = request.IsWrite
            ? request.RiskKeys.Select(DrawingRiskKey.Normalize).Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        if (!request.IsWrite && request.GlobalFence)
            throw new ArgumentException("Read requests cannot reserve the global write fence.", nameof(request));
        if (!request.IsWrite && request.RiskKeys.Count != 0)
            throw new ArgumentException("Read requests cannot reserve write risk keys.", nameof(request));
        if (request.IsWrite && request.GlobalFence && keys.Length != 0)
            throw new ArgumentException("Global fence requests cannot include caller risk keys.", nameof(request));
        if (request.IsWrite && !request.GlobalFence)
        {
            if (!keys.Any(key => key.StartsWith("PATH:", StringComparison.Ordinal)))
                throw new ArgumentException("A drawing write requires at least one normalized PATH risk key.", nameof(request.RiskKeys));
            string? targetPath = ExtractTargetDrawingPath(target);
            if (targetPath is null)
                throw new ArgumentException("Target has no verifiable drawing path; reserve the global fence instead.", nameof(request.CompleteTargetJson));
            if (!keys.Contains(targetPath, StringComparer.Ordinal))
                throw new ArgumentException("Risk keys must include the complete target drawing path; aliases are supplemental only.", nameof(request.RiskKeys));
        }

        DateTimeOffset? deadline = request.QueueDeadlineUtc?.ToUniversalTime();
        string[] fenceKeys = request.IsWrite ? (request.GlobalFence ? [GlobalFenceKey] : keys) : Array.Empty<string>();
        string digest = ComputeSubmissionDigest(id, context, tool, parameters, target, request.IsWrite,
            request.GlobalFence, keys, deadline);
        return new NormalizedSubmission(id, context, tool, parameters, target, request.IsWrite,
            request.GlobalFence, keys, fenceKeys, deadline, digest);
    }

    private string[] FindConflicts(SqliteConnection connection, SqliteTransaction transaction, NormalizedSubmission request)
    {
        var conflicts = new SortedSet<string>(StringComparer.Ordinal);
        if (!request.IsWrite) return Array.Empty<string>();
        if (request.GlobalFence)
        {
            using SqliteCommand command = CreateCommand(connection, transaction,
                "SELECT risk_key FROM plugin_write_fences ORDER BY risk_key COLLATE BINARY;");
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) conflicts.Add(ExternalRiskKey(reader.GetString(0)));
        }
        else
        {
            foreach (string key in request.RiskKeys.Append(GlobalFenceKey))
            {
                using SqliteCommand command = CreateCommand(connection, transaction,
                    "SELECT 1 FROM plugin_write_fences WHERE risk_key=$key LIMIT 1;");
                command.Parameters.AddWithValue("$key", key);
                if (command.ExecuteScalar() is not null) conflicts.Add(ExternalRiskKey(key));
            }
        }
        return conflicts.ToArray();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds}; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static PluginJobSnapshot? ReadJob(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = CreateCommand(connection, transaction, @"
SELECT request_id,context_id,tool_name,canonical_parameters_json,complete_target_json,payload_digest,owner_id,
 provider_generation,is_write,phase,effects,verification,observation,plugin_sequence,result_json,error_code,error_message,
 created_at_utc,updated_at_utc,queue_deadline_utc,write_fence_released,global_fence,readback_revision,readback_digest,
 verification_result_json,verification_error_code,verification_error_message,committed_result_digest,
 verification_staged_sequence,write_fence_finalized,no_effect_claim_sequence,no_effect_outcome
FROM plugin_jobs WHERE request_id=$id;");
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string requestId = reader.GetString(0);
        string context = reader.GetString(1);
        string tool = reader.GetString(2);
        string parameters = reader.GetString(3);
        string target = reader.GetString(4);
        string digest = reader.GetString(5);
        string ownerId = reader.GetString(6);
        string generation = reader.GetString(7);
        bool isWrite = reader.GetInt64(8) == 1;
        PluginJobPhase phase = ParsePhase(reader.GetString(9));
        PluginEffectState effects = ParseEffects(reader.GetString(10));
        PluginVerificationState verification = ParseVerification(reader.GetString(11));
        PluginObservationState observation = ParseObservation(reader.GetString(12));
        long sequence = reader.GetInt64(13);
        string? result = NullableString(reader, 14);
        string? errorCode = NullableString(reader, 15);
        string? errorMessage = NullableString(reader, 16);
        DateTimeOffset created = ParseTimestamp(reader.GetString(17));
        DateTimeOffset updated = ParseTimestamp(reader.GetString(18));
        DateTimeOffset? deadline = reader.IsDBNull(19) ? null : ParseTimestamp(reader.GetString(19));
        bool released = reader.GetInt64(20) == 1;
        bool global = reader.GetInt64(21) == 1;
        long? readbackRevision = reader.IsDBNull(22) ? null : reader.GetInt64(22);
        string? readbackDigest = NullableString(reader, 23);
        string? verificationResult = NullableString(reader, 24);
        string? verificationCode = NullableString(reader, 25);
        string? verificationMessage = NullableString(reader, 26);
        string? committedResultDigest = NullableString(reader, 27);
        long? verificationStagedSequence = reader.IsDBNull(28) ? null : reader.GetInt64(28);
        bool writeFenceFinalized = reader.GetInt64(29) == 1;
        long? noEffectClaimSequence = reader.IsDBNull(30) ? null : reader.GetInt64(30);
        PluginNoEffectOutcome? noEffectOutcome = reader.IsDBNull(31)
            ? null : ParseNoEffectOutcome(reader.GetString(31));
        reader.Close();
        string[] heldKeys = ReadHeldRiskKeys(connection, transaction, requestId);
        return new PluginJobSnapshot(requestId, context, tool, parameters, target, digest, ownerId, generation,
            isWrite, phase, effects, verification, observation, sequence, result, errorCode, errorMessage,
            created, updated, deadline, released, global, heldKeys, readbackRevision, readbackDigest,
            verificationResult, verificationCode, verificationMessage, committedResultDigest,
            verificationStagedSequence, writeFenceFinalized, noEffectClaimSequence, noEffectOutcome);
    }

    private static string[] ReadHeldRiskKeys(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = CreateCommand(connection, transaction, @"
SELECT risk_key FROM plugin_write_fences WHERE request_id=$id AND risk_key<>$global ORDER BY risk_key COLLATE BINARY;");
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$global", GlobalFenceKey);
        using SqliteDataReader reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys.ToArray();
    }

    private bool IsCurrentOwner(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using SqliteCommand command = CreateCommand(connection, transaction,
            "SELECT 1 FROM plugin_jobs WHERE request_id=$id AND owner_key=$owner;");
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", _ownerKey);
        return command.ExecuteScalar() is not null;
    }

    private static void UpdatePhase(SqliteConnection connection, SqliteTransaction transaction, string id, string ownerKey,
        PluginJobPhase phase, PluginEffectState effects, string? errorCode, string? errorMessage,
        DateTimeOffset now, long sequence, string? resultJson = null)
    {
        using SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE plugin_jobs SET phase=$phase,effects=$effects,observation='plugin_snapshot',plugin_sequence=$sequence,
 result_json=$result,error_code=$code,error_message=$message,updated_at_utc=$updated
WHERE request_id=$id AND owner_key=$owner;");
        update.Parameters.AddWithValue("$phase", ToDatabaseValue(phase));
        update.Parameters.AddWithValue("$effects", ToDatabaseValue(effects));
        update.Parameters.AddWithValue("$sequence", sequence);
        AddNullable(update, "$result", resultJson);
        AddNullable(update, "$code", errorCode);
        AddNullable(update, "$message", errorMessage);
        update.Parameters.AddWithValue("$updated", FormatTimestamp(now));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$owner", ownerKey);
        update.ExecuteNonQuery();
    }

    private static void ReleaseFenceIfSafe(SqliteConnection connection, SqliteTransaction transaction,
        string id, string ownerKey, bool release, DateTimeOffset now)
    {
        if (!release) return;
        using SqliteCommand update = CreateCommand(connection, transaction,
            "UPDATE plugin_jobs SET write_fence_released=1,updated_at_utc=$updated WHERE request_id=$id AND owner_key=$owner;");
        update.Parameters.AddWithValue("$updated", FormatTimestamp(now));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$owner", ownerKey);
        update.ExecuteNonQuery();
        ReleaseFence(connection, transaction, id, ownerKey);
    }

    private static void ReleaseFence(SqliteConnection connection, SqliteTransaction transaction, string id, string ownerKey)
    {
        using SqliteCommand command = CreateCommand(connection, transaction, @"
DELETE FROM plugin_write_fences WHERE request_id=$id
AND EXISTS(SELECT 1 FROM plugin_jobs WHERE request_id=$id AND owner_key=$owner);");
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", ownerKey);
        command.ExecuteNonQuery();
    }

    private static bool CanChangeEffects(PluginJobSnapshot current, PluginJobPhase phase,
        PluginEffectState next, out string reason)
    {
        reason = string.Empty;
        if (current.Effects == PluginEffectState.Committed && next != PluginEffectState.Committed)
        { reason = "committed_effects_cannot_be_retracted"; return false; }
        if (current.Effects == PluginEffectState.Unknown && next != PluginEffectState.Unknown
            && !(phase == PluginJobPhase.Completed && current.IsWrite && next == PluginEffectState.Committed))
        { reason = "unknown_effects_cannot_be_cleared"; return false; }
        if (next == PluginEffectState.Committed && current.Effects != PluginEffectState.Committed
            && phase != PluginJobPhase.Completed)
        { reason = "commit_must_be_recorded_before_failed_or_unknown_terminal"; return false; }
        if (!current.IsWrite && next != PluginEffectState.None)
        { reason = "read_jobs_cannot_have_write_effects"; return false; }
        return true;
    }

    private static string CanonicalizeJsonObject(string json, string parameterName)
        => PluginJsonCanonicalizer.CanonicalizeObject(json, parameterName);

    private static string? ExtractTargetDrawingPath(string canonicalTargetJson)
    {
        using JsonDocument document = JsonDocument.Parse(canonicalTargetJson);
        return FindDrawingPath(document.RootElement, false);
    }

    private static string? FindDrawingPath(JsonElement element, bool inDrawingObject)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                bool namedDrawing = property.Name.Equals("document", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("drawing", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("document_path", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("drawing_path", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("dwg_path", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("dwg", StringComparison.OrdinalIgnoreCase);
                if (property.Value.ValueKind == JsonValueKind.String
                    && (namedDrawing || (inDrawingObject && property.Name.Equals("path", StringComparison.OrdinalIgnoreCase))))
                {
                    string? candidate = property.Value.GetString();
                    if (candidate is not null && DrawingRiskKey.TryNormalizePath(candidate, out string normalized)) return normalized;
                }
                string? nested = FindDrawingPath(property.Value, inDrawingObject || namedDrawing);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = FindDrawingPath(item, inDrawingObject);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private string ComputeSubmissionDigest(string id, string context, string tool, string parameters, string target,
        bool isWrite, bool globalFence, IReadOnlyList<string> riskKeys, DateTimeOffset? deadline)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        using (JsonDocument p = JsonDocument.Parse(parameters))
        using (JsonDocument t = JsonDocument.Parse(target))
        {
            writer.WriteStartObject();
            writer.WriteNumber("digest_version", 1);
            writer.WriteString("request_id", id);
            writer.WriteString("context_id", context);
            writer.WriteString("tool_name", tool);
            writer.WriteString("owner_id", Owner.OwnerId);
            writer.WriteNumber("owner_pid", Owner.ProcessId);
            writer.WriteString("owner_process_started_at_utc", FormatTimestamp(Owner.ProcessStartedAtUtc));
            writer.WriteString("provider_generation", Owner.PluginGeneration);
            writer.WritePropertyName("parameters"); p.RootElement.WriteTo(writer);
            writer.WritePropertyName("complete_target"); t.RootElement.WriteTo(writer);
            writer.WriteBoolean("is_write", isWrite);
            writer.WriteBoolean("global_fence", globalFence);
            writer.WritePropertyName("risk_keys");
            writer.WriteStartArray();
            foreach (string key in riskKeys) writer.WriteStringValue(key);
            writer.WriteEndArray();
            if (deadline.HasValue) writer.WriteString("queue_deadline_utc", FormatTimestamp(deadline.Value));
            else writer.WriteNull("queue_deadline_utc");
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static string ComputeStagedVerificationDigest(PluginVerificationRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        using (JsonDocument target = JsonDocument.Parse(request.CompleteTargetJson))
        {
            writer.WriteStartObject();
            writer.WriteString("request_id", request.RequestId);
            writer.WriteString("context_id", request.ContextId);
            writer.WritePropertyName("complete_target"); target.RootElement.WriteTo(writer);
            writer.WriteString("owner_id", request.OwnerId);
            writer.WriteString("provider_generation", request.ProviderGeneration);
            writer.WriteString("payload_digest", request.PayloadDigest);
            writer.WriteNumber("expected_plugin_sequence", request.ExpectedPluginSequence!.Value);
            writer.WriteString("committed_result_digest", request.CommittedResultDigest);
            writer.WriteNumber("readback_revision", request.ReadbackRevision);
            writer.WriteString("verification", ToDatabaseValue(request.Verification));
            WriteNullable(writer, "readback_result", request.ResultJson);
            WriteNullable(writer, "error_code", request.ErrorCode);
            WriteNullable(writer, "error_message", request.ErrorMessage);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static string ComputeContentDigest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string ComputeJournalPathDigest(string databasePath)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(databasePath));
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static long NextSequence(long sequence)
    {
        if (sequence < 0 || sequence == long.MaxValue)
            throw new OverflowException("Plugin sequence cannot be advanced.");
        return sequence + 1;
    }

    private static bool MatchesWriteIdentity(PluginJobSnapshot current, string contextId,
        string completeTargetJson, string ownerId, string providerGeneration, string payloadDigest) =>
        current.IsWrite
        && string.Equals(current.ContextId, contextId, StringComparison.Ordinal)
        && string.Equals(current.CompleteTargetJson, completeTargetJson, StringComparison.Ordinal)
        && string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal)
        && string.Equals(current.ProviderGeneration, providerGeneration, StringComparison.Ordinal)
        && string.Equals(current.PayloadDigest, payloadDigest, StringComparison.Ordinal);

    private static bool HasRiskFence(SqliteConnection connection, SqliteTransaction transaction, string requestId)
    {
        using SqliteCommand command = CreateCommand(connection, transaction,
            "SELECT 1 FROM plugin_write_fences WHERE request_id=$id LIMIT 1;");
        command.Parameters.AddWithValue("$id", requestId);
        return command.ExecuteScalar() is not null;
    }

    private static void ValidateOptionalJsonObject(string? json, string parameterName)
    {
        if (json is null) return;
        try
        {
            using JsonDocument _ = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The value must be valid JSON.", parameterName, exception);
        }
    }

    private static string RequiredDigest(string? value, string name)
    {
        if (value is null || value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Digest must be a 64-character hexadecimal SHA-256 value.", name);
        return value.ToUpperInvariant();
    }

    private static string ComputeVerificationDigest(string id, string context, string target, string generation,
        PluginVerificationRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("request_id", id);
            writer.WriteString("context_id", context);
            writer.WriteString("target", target);
            writer.WriteString("generation", generation);
            writer.WriteNumber("revision", request.ReadbackRevision);
            writer.WriteString("verification", ToDatabaseValue(request.Verification));
            WriteNullable(writer, "result", request.ResultJson);
            WriteNullable(writer, "error_code", request.ErrorCode);
            WriteNullable(writer, "error_message", request.ErrorMessage);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static string RequiredIdentifier(string? value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(char.IsControl)
            || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            throw new ArgumentException("Identifier must be 1-128 characters without controls or leading/trailing whitespace.", name);
        return value;
    }

    private static string RequiredGeneration(string? value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-')))
            throw new ArgumentException("Plugin generation must be a 1-128 character opaque ASCII identifier.", name);
        return value;
    }

    private static void EnsureEnumValue<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(name);
    }

    private static bool IsTerminal(PluginJobPhase phase) =>
        phase is PluginJobPhase.Completed or PluginJobPhase.Failed or PluginJobPhase.Cancelled or PluginJobPhase.Unknown;

    private static string ExternalRiskKey(string key) => key == GlobalFenceKey ? "<GLOBAL_DRAWING_FENCE>" : key;
    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction,
        string columnName, string columnDefinition)
    {
        bool exists = false;
        using (SqliteCommand info = CreateCommand(connection, transaction, "PRAGMA table_info(plugin_jobs);"))
        using (SqliteDataReader reader = info.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (!exists)
        {
            using SqliteCommand alter = CreateCommand(connection, transaction,
                $"ALTER TABLE plugin_jobs ADD COLUMN {columnName} {columnDefinition};");
            alter.ExecuteNonQuery();
        }
    }

    private static string ToDatabaseValue(PluginJobPhase value) => value switch
    {
        PluginJobPhase.Queued => "queued", PluginJobPhase.Running => "running",
        PluginJobPhase.CancellationRequested => "cancellation_requested", PluginJobPhase.Completed => "completed",
        PluginJobPhase.Failed => "failed", PluginJobPhase.Cancelled => "cancelled", PluginJobPhase.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static string ToDatabaseValue(PluginEffectState value) => value switch
    {
        PluginEffectState.None => "none", PluginEffectState.Unknown => "unknown", PluginEffectState.Committed => "committed",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static string ToDatabaseValue(PluginVerificationState value) => value switch
    {
        PluginVerificationState.NotChecked => "not_checked", PluginVerificationState.Passed => "passed",
        PluginVerificationState.Failed => "failed", PluginVerificationState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static string ToDatabaseValue(PluginObservationState value) => value switch
    {
        PluginObservationState.PluginSnapshot => "plugin_snapshot", PluginObservationState.Recovery => "recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static string ToDatabaseValue(PluginNoEffectOutcome value) => value switch
    {
        PluginNoEffectOutcome.RejectedBeforeTransaction => "rejected_before_transaction",
        PluginNoEffectOutcome.RolledBackBeforeCommit => "rolled_back_before_commit",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static PluginJobPhase ParsePhase(string value) => value switch
    {
        "queued" => PluginJobPhase.Queued, "running" => PluginJobPhase.Running,
        "cancellation_requested" => PluginJobPhase.CancellationRequested, "completed" => PluginJobPhase.Completed,
        "failed" => PluginJobPhase.Failed, "cancelled" => PluginJobPhase.Cancelled, "unknown" => PluginJobPhase.Unknown,
        _ => throw new InvalidDataException($"Unknown plugin job phase '{value}'.")
    };
    private static PluginEffectState ParseEffects(string value) => value switch
    {
        "none" => PluginEffectState.None, "unknown" => PluginEffectState.Unknown, "committed" => PluginEffectState.Committed,
        _ => throw new InvalidDataException($"Unknown plugin effect state '{value}'.")
    };
    private static PluginVerificationState ParseVerification(string value) => value switch
    {
        "not_checked" => PluginVerificationState.NotChecked, "passed" => PluginVerificationState.Passed,
        "failed" => PluginVerificationState.Failed, "unknown" => PluginVerificationState.Unknown,
        _ => throw new InvalidDataException($"Unknown verification state '{value}'.")
    };
    private static PluginObservationState ParseObservation(string value) => value switch
    {
        "plugin_snapshot" => PluginObservationState.PluginSnapshot, "recovery" => PluginObservationState.Recovery,
        _ => throw new InvalidDataException($"Unknown plugin observation state '{value}'.")
    };
    private static PluginNoEffectOutcome ParseNoEffectOutcome(string value) => value switch
    {
        "rejected_before_transaction" => PluginNoEffectOutcome.RejectedBeforeTransaction,
        "rolled_back_before_commit" => PluginNoEffectOutcome.RolledBackBeforeCommit,
        _ => throw new InvalidDataException($"Unknown no-effect outcome '{value}'.")
    };

    private OperationLease EnterOperation()
    {
        _lifecycle.EnterReadLock();
        if (_disposed)
        {
            _lifecycle.ExitReadLock();
            throw new ObjectDisposedException(nameof(SqlitePluginIntentJournal));
        }
        if (!_initialized)
        {
            _lifecycle.ExitReadLock();
            throw new InvalidOperationException("Initialize must complete confirmed-lost recovery before journal operations.");
        }
        if (_ownerLock is null)
        {
            _lifecycle.ExitReadLock();
            throw new ObjectDisposedException(nameof(SqlitePluginIntentJournal), "The owner generation lock is not held.");
        }
        return new OperationLease(_lifecycle);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SqlitePluginIntentJournal));
    }

    private sealed class OperationLease(ReaderWriterLockSlim lifecycle) : IDisposable
    {
        public void Dispose() => lifecycle.ExitReadLock();
    }

    private sealed record NormalizedSubmission(
        string RequestId, string ContextId, string ToolName, string ParametersJson, string TargetJson,
        bool IsWrite, bool GlobalFence, string[] RiskKeys, string[] FenceKeys,
        DateTimeOffset? QueueDeadlineUtc, string PayloadDigest);

    private sealed record StoredOwner(string OwnerKey, string OwnerId, int ProcessId,
        DateTimeOffset ProcessStartedAtUtc, string PluginGeneration)
    {
        public PluginOwnerIdentity ToIdentity() => new(OwnerId, ProcessId, ProcessStartedAtUtc, PluginGeneration);
    }
}
