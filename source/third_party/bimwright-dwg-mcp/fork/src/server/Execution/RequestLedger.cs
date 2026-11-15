#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Bimwright.Dwg.Shared.Execution;
using Microsoft.Data.Sqlite;

namespace Bimwright.Dwg.Server.Execution;

public sealed class RequestLedger : IDisposable
{
    private const int CurrentSchemaVersion = 3;
    private const int BusyTimeoutMilliseconds = 15_000;
    private const string GlobalFenceKey = "\u0001GLOBAL_DRAWING_FENCE";
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ReaderWriterLockSlim _lifecycle = new(LockRecursionPolicy.NoRecursion);
    private FileStream? _ownerLock;
    private bool _disposed;
    private bool _operationsStarted;
    private bool _recoveryCompleted;
    private bool _startupReconciliationCompleted;

    public RequestLedger(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A file-backed SQLite path is required.", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared, Pooling = false, DefaultTimeout = 15
        };
        _connectionString = builder.ToString();
        try
        {
            _ownerLock = new FileStream(_databasePath + ".owner.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Another RequestLedger owner already holds this database; multi-instance access is fail-closed.", exception);
        }
        try { InitializeDatabase(); }
        catch
        {
            _ownerLock.Dispose();
            _ownerLock = null;
            throw;
        }
    }

    public string DatabasePath => _databasePath;

    public void Dispose()
    {
        _lifecycle.EnterWriteLock();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _ownerLock?.Dispose();
            _ownerLock = null;
        }
        finally
        {
            _lifecycle.ExitWriteLock();
            GC.SuppressFinalize(this);
        }
    }

    public ReserveResult Reserve(ReserveRequest request)
    {
        using IDisposable lease = EnterOperation();
        _operationsStarted = true;
        ArgumentNullException.ThrowIfNull(request);
        NormalizedRequest normalized = NormalizeRequest(request);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        LedgerJob? existing = ReadJob(connection, transaction, normalized.RequestId);
        if (existing is not null)
        {
            transaction.Rollback();
            return string.Equals(existing.PayloadDigest, normalized.PayloadDigest, StringComparison.Ordinal)
                ? new ReserveResult(ReserveDisposition.Existing, existing, Array.Empty<string>())
                : new ReserveResult(ReserveDisposition.RequestIdConflict, existing, Array.Empty<string>());
        }

        var conflicts = new List<string>();
        if (!normalized.IsWrite)
        {
            // Read-only jobs do not claim drawing write fences.
        }
        else if (normalized.GlobalFence)
        {
            using SqliteCommand check = CreateCommand(connection, transaction, "SELECT risk_key FROM write_fences ORDER BY risk_key COLLATE BINARY;");
            using SqliteDataReader reader = check.ExecuteReader();
            while (reader.Read()) conflicts.Add(reader.GetString(0));
        }
        else
        {
            foreach (string key in normalized.RiskKeys.Append(GlobalFenceKey))
            {
                using SqliteCommand check = CreateCommand(connection, transaction, "SELECT risk_key FROM write_fences WHERE risk_key = $key LIMIT 1;");
                check.Parameters.AddWithValue("$key", key);
                if (check.ExecuteScalar() is string) conflicts.Add(key);
            }
        }
        if (conflicts.Count > 0)
        {
            transaction.Rollback();
            return new ReserveResult(ReserveDisposition.FenceConflict, null,
                conflicts.Select(ExternalRiskKey).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray());
        }

        using (SqliteCommand insert = CreateCommand(connection, transaction, @"
INSERT INTO request_jobs (
 request_id,payload_digest,context_id,tool_name,canonical_parameters_json,complete_target_json,
 is_write,risk_keys_json,phase,effects,verification,observation,plugin_sequence,result_json,error_code,error_message,
 created_at_utc,updated_at_utc,observed_at_utc,write_fence_released,phase_before_unknown,provider_generation,
 global_fence,plugin_snapshot_digest,readback_revision,readback_digest,verification_result_json,
 verification_error_code,verification_error_message,server_unavailable)
VALUES ($id,$digest,$context,$tool,$parameters,$target,$write,$keys,'queued','none','not_checked','none',NULL,
 NULL,NULL,NULL,$created,$updated,NULL,0,NULL,$generation,$global,NULL,NULL,NULL,NULL,NULL,NULL,0);"))
        {
            insert.Parameters.AddWithValue("$id", normalized.RequestId);
            insert.Parameters.AddWithValue("$digest", normalized.PayloadDigest);
            insert.Parameters.AddWithValue("$context", normalized.ContextId);
            insert.Parameters.AddWithValue("$tool", normalized.ToolName);
            insert.Parameters.AddWithValue("$parameters", normalized.ParametersJson);
            insert.Parameters.AddWithValue("$target", normalized.TargetJson);
            insert.Parameters.AddWithValue("$write", normalized.IsWrite ? 1 : 0);
            insert.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(normalized.RiskKeys));
            insert.Parameters.AddWithValue("$created", FormatTimestamp(now));
            insert.Parameters.AddWithValue("$updated", FormatTimestamp(now));
            insert.Parameters.AddWithValue("$generation", normalized.ProviderGeneration);
            insert.Parameters.AddWithValue("$global", normalized.GlobalFence ? 1 : 0);
            insert.ExecuteNonQuery();
        }
        foreach (string key in normalized.FenceKeys)
        {
            using SqliteCommand fence = CreateCommand(connection, transaction,
                "INSERT INTO write_fences(risk_key,request_id,created_at_utc) VALUES($key,$id,$created);");
            fence.Parameters.AddWithValue("$key", key);
            fence.Parameters.AddWithValue("$id", normalized.RequestId);
            fence.Parameters.AddWithValue("$created", FormatTimestamp(now));
            fence.ExecuteNonQuery();
        }
        LedgerJob created = ReadJob(connection, transaction, normalized.RequestId)!;
        transaction.Commit();
        return new ReserveResult(ReserveDisposition.Created, created, Array.Empty<string>());
    }

    /// <summary>Applies only plugin-authoritative snapshots. Independent readback has its own revision API.</summary>
    public AdvanceResult Advance(AdvanceRequest request) => AdvanceCore(request, expectedCurrentSequence: null, enforceExpectedSequence: false);

    /// <summary>Applies a snapshot only when the ledger sequence still matches the value captured before a historical query.</summary>
    public AdvanceResult AdvanceIfCurrentSequence(AdvanceRequest request, long? expectedCurrentSequence) =>
        AdvanceCore(request, expectedCurrentSequence, enforceExpectedSequence: true);

    private AdvanceResult AdvanceCore(AdvanceRequest request, long? expectedCurrentSequence, bool enforceExpectedSequence)
    {
        using IDisposable lease = EnterOperation();
        _operationsStarted = true;
        ArgumentNullException.ThrowIfNull(request);
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        if (request.PluginSequence < 0) throw new ArgumentOutOfRangeException(nameof(request.PluginSequence));
        EnsureEnumValue(request.Phase, nameof(request.Phase));
        EnsureEnumValue(request.Effects, nameof(request.Effects));
        EnsureEnumValue(request.Verification, nameof(request.Verification));
        EnsureEnumValue(request.Observation, nameof(request.Observation));
        if (request.Observation is not (ObservationState.PluginSnapshot or ObservationState.AuthoritativePluginSnapshot))
            throw new ArgumentException("Advance accepts plugin snapshots only; use RecordVerification for independent readback.", nameof(request.Observation));
        if (request.Verification == VerificationState.Passed)
            throw new ArgumentException("Plugin snapshots cannot assert independent verification.", nameof(request.Verification));

        DateTimeOffset observedAt = request.ObservedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        LedgerJob? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new AdvanceResult(AdvanceDisposition.NotFound, null, "request_id_not_found");
        }
        if (!string.Equals(current.ProviderGeneration, generation, StringComparison.Ordinal))
        {
            transaction.Rollback();
            return new AdvanceResult(AdvanceDisposition.RejectedTransition, current, "provider_generation_mismatch");
        }
        if (enforceExpectedSequence && current.PluginSequence != expectedCurrentSequence)
        {
            transaction.Rollback();
            return new AdvanceResult(AdvanceDisposition.RejectedTransition, current, "expected_plugin_sequence_mismatch");
        }

        string snapshotDigest = ComputePluginSnapshotDigest(generation, request);
        if (current.PluginSequence.HasValue)
        {
            if (request.PluginSequence < current.PluginSequence.Value)
            {
                transaction.Rollback();
                return new AdvanceResult(AdvanceDisposition.StaleSequence, current, "older_plugin_sequence");
            }
            if (request.PluginSequence == current.PluginSequence.Value)
            {
                if (!string.Equals(snapshotDigest, current.PluginSnapshotDigest, StringComparison.Ordinal))
                {
                    transaction.Rollback();
                    return new AdvanceResult(AdvanceDisposition.RejectedTransition, current, "same_sequence_snapshot_conflict");
                }
                if (current.ServerUnavailable)
                {
                    using SqliteCommand restore = CreateCommand(connection, transaction, @"
UPDATE request_jobs SET observation=$observation,server_unavailable=0,updated_at_utc=$updated,observed_at_utc=$observed
WHERE request_id=$id;");
                    restore.Parameters.AddWithValue("$observation", ToDatabaseValue(request.Observation));
                    restore.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
                    restore.Parameters.AddWithValue("$observed", FormatTimestamp(observedAt));
                    restore.Parameters.AddWithValue("$id", id);
                    restore.ExecuteNonQuery();
                    LedgerJob restored = ReadJob(connection, transaction, id)!;
                    transaction.Commit();
                    return new AdvanceResult(AdvanceDisposition.Duplicate, restored);
                }
                transaction.Rollback();
                return new AdvanceResult(AdvanceDisposition.Duplicate, current);
            }
        }

        if (!CanTransition(current, request, out string? transitionReason))
        {
            transaction.Rollback();
            return new AdvanceResult(AdvanceDisposition.RejectedTransition, current, transitionReason);
        }
        if (!CanAdvanceEffects(current, request, out string? effectReason))
        {
            transaction.Rollback();
            return new AdvanceResult(AdvanceDisposition.RejectedTransition, current, effectReason);
        }
        if (request.Phase == JobPhase.Unknown
            && (request.Observation != ObservationState.AuthoritativePluginSnapshot
                || (current.IsWrite && request.Effects != EffectState.Unknown)))
        {
            transaction.Rollback();
            return new AdvanceResult(AdvanceDisposition.RejectedTransition, current, "unknown_requires_authoritative_plugin_report");
        }
        if (request.Phase == JobPhase.Cancelled
            && (current.Phase != JobPhase.Queued || request.Effects != EffectState.None
                || request.Observation != ObservationState.AuthoritativePluginSnapshot))
        {
            transaction.Rollback();
            return new AdvanceResult(AdvanceDisposition.RejectedTransition, current, "only_authoritative_queued_cancel_is_safe");
        }
        if (current.Verification == VerificationState.Passed && request.Verification != VerificationState.Passed)
        {
            transaction.Rollback();
            return new AdvanceResult(AdvanceDisposition.RejectedTransition, current, "verification_cannot_regress_after_pass");
        }
        if (current.WriteFenceReleased)
        {
            transaction.Rollback();
            return new AdvanceResult(AdvanceDisposition.RejectedTransition, current, "job_already_finalized_and_fence_released");
        }

        bool release = current.IsWrite && IsSafeNoEffectRelease(request);
        string? prior = request.Phase == JobPhase.Unknown
            ? (current.Phase == JobPhase.Unknown ? (current.PhaseBeforeUnknown.HasValue ? ToDatabaseValue(current.PhaseBeforeUnknown.Value) : null) : ToDatabaseValue(current.Phase))
            : null;
        using (SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE request_jobs SET phase=$phase,effects=$effects,verification=$verification,observation=$observation,
 plugin_sequence=$sequence,result_json=$result,error_code=$code,error_message=$message,updated_at_utc=$updated,
 observed_at_utc=$observed,write_fence_released=$released,phase_before_unknown=$prior,
 plugin_snapshot_digest=$snapshot,server_unavailable=0 WHERE request_id=$id;"))
        {
            update.Parameters.AddWithValue("$phase", ToDatabaseValue(request.Phase));
            update.Parameters.AddWithValue("$effects", ToDatabaseValue(request.Effects));
            update.Parameters.AddWithValue("$verification", ToDatabaseValue(request.Verification));
            update.Parameters.AddWithValue("$observation", ToDatabaseValue(request.Observation));
            update.Parameters.AddWithValue("$sequence", request.PluginSequence);
            update.Parameters.AddWithValue("$result", (object?)request.ResultJson ?? DBNull.Value);
            update.Parameters.AddWithValue("$code", (object?)request.ErrorCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$message", (object?)request.ErrorMessage ?? DBNull.Value);
            update.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$observed", FormatTimestamp(observedAt));
            update.Parameters.AddWithValue("$released", release ? 1 : 0);
            update.Parameters.AddWithValue("$prior", (object?)prior ?? DBNull.Value);
            update.Parameters.AddWithValue("$snapshot", snapshotDigest);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        if (release) ReleaseFence(connection, transaction, id);
        LedgerJob updated = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new AdvanceResult(AdvanceDisposition.Applied, updated);
    }

    /// <summary>Records independent readback without changing plugin phase, effects, or sequence.</summary>
    public VerificationResult RecordVerification(VerificationRequest request)
    {
        using IDisposable lease = EnterOperation();
        _operationsStarted = true;
        ArgumentNullException.ThrowIfNull(request);
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        if (request.ReadbackRevision < 0) throw new ArgumentOutOfRangeException(nameof(request.ReadbackRevision));
        if (request.ExpectedPluginSequence.HasValue && request.ExpectedPluginSequence.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedPluginSequence));
        EnsureEnumValue(request.Verification, nameof(request.Verification));
        if (request.Verification is VerificationState.NotChecked or VerificationState.Unknown)
            throw new ArgumentException("Readback must report Passed or Failed.", nameof(request.Verification));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        DateTimeOffset observed = request.ObservedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        LedgerJob? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.NotFound, null, "request_id_not_found");
        }
        if (!current.IsWrite || current.Phase != JobPhase.Completed || current.Effects != EffectState.Committed)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "verification_requires_completed_committed_write");
        }
        if (current.ContextId != context || current.CompleteTargetJson != target || current.ProviderGeneration != generation)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "verification_identity_mismatch");
        }
        if (request.ExpectedPluginSequence.HasValue
            && current.PluginSequence != request.ExpectedPluginSequence.Value)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "expected_plugin_sequence_mismatch");
        }
        string digest = ComputeVerificationDigest(id, context, target, generation, request);
        if (current.ReadbackRevision.HasValue)
        {
            if (request.ReadbackRevision < current.ReadbackRevision.Value)
            {
                transaction.Rollback();
                return new VerificationResult(VerificationDisposition.StaleRevision, current, "older_readback_revision");
            }
            if (request.ReadbackRevision == current.ReadbackRevision.Value)
            {
                bool same = digest == current.ReadbackDigest;
                transaction.Rollback();
                return same ? new VerificationResult(VerificationDisposition.Duplicate, current)
                    : new VerificationResult(VerificationDisposition.RejectedState, current, "same_revision_snapshot_conflict");
            }
        }
        if (current.WriteFenceReleased)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "job_already_finalized_and_fence_released");
        }
        if (current.Verification == VerificationState.Passed && request.Verification != VerificationState.Passed)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "verification_cannot_regress_after_pass");
        }

        bool release = request.Verification == VerificationState.Passed;
        using (SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE request_jobs SET verification=$verification,observation='independent_readback',
 readback_revision=$revision,readback_digest=$digest,verification_result_json=$result,
 verification_error_code=$code,verification_error_message=$message,updated_at_utc=$updated,
 observed_at_utc=$observed,write_fence_released=$released WHERE request_id=$id;"))
        {
            update.Parameters.AddWithValue("$verification", ToDatabaseValue(request.Verification));
            update.Parameters.AddWithValue("$revision", request.ReadbackRevision);
            update.Parameters.AddWithValue("$digest", digest);
            update.Parameters.AddWithValue("$result", (object?)request.ResultJson ?? DBNull.Value);
            update.Parameters.AddWithValue("$code", (object?)request.ErrorCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$message", (object?)request.ErrorMessage ?? DBNull.Value);
            update.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$observed", FormatTimestamp(observed));
            update.Parameters.AddWithValue("$released", release ? 1 : 0);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        if (release) ReleaseFence(connection, transaction, id);
        LedgerJob updated = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new VerificationResult(VerificationDisposition.Applied, updated);
    }

    /// <summary>
    /// Persists independent verification while retaining the server risk fence.
    /// A separate exact finalize call releases it only after plugin finalization.
    /// </summary>
    public VerificationResult RecordVerificationPendingRelease(VerificationRequest request)
    {
        using IDisposable lease = EnterOperation();
        _operationsStarted = true;
        ArgumentNullException.ThrowIfNull(request);
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        if (request.ReadbackRevision < 0) throw new ArgumentOutOfRangeException(nameof(request.ReadbackRevision));
        if (!request.ExpectedPluginSequence.HasValue || request.ExpectedPluginSequence.Value < 0)
            throw new ArgumentException("Pending-release verification requires an exact plugin sequence.", nameof(request.ExpectedPluginSequence));
        EnsureEnumValue(request.Verification, nameof(request.Verification));
        if (request.Verification is VerificationState.NotChecked or VerificationState.Unknown)
            throw new ArgumentException("Readback must report Passed or Failed.", nameof(request.Verification));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        DateTimeOffset observed = request.ObservedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        LedgerJob? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.NotFound, null, "request_id_not_found");
        }
        if (!current.IsWrite || current.Phase != JobPhase.Completed || current.Effects != EffectState.Committed)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "verification_requires_completed_committed_write");
        }
        if (current.ContextId != context || current.CompleteTargetJson != target || current.ProviderGeneration != generation)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "verification_identity_mismatch");
        }
        if (current.PluginSequence != request.ExpectedPluginSequence.Value)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "expected_plugin_sequence_mismatch");
        }

        string digest = ComputeVerificationDigest(id, context, target, generation, request);
        if (current.ReadbackRevision.HasValue)
        {
            if (request.ReadbackRevision < current.ReadbackRevision.Value)
            {
                transaction.Rollback();
                return new VerificationResult(VerificationDisposition.StaleRevision, current, "older_readback_revision");
            }
            if (request.ReadbackRevision == current.ReadbackRevision.Value)
            {
                bool same = digest == current.ReadbackDigest
                    && current.VerificationStagedPluginSequence == request.ExpectedPluginSequence.Value;
                transaction.Rollback();
                return same ? new VerificationResult(VerificationDisposition.Duplicate, current)
                    : new VerificationResult(VerificationDisposition.RejectedState, current, "same_revision_snapshot_conflict");
            }
        }
        if (current.WriteFenceReleased)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "job_already_finalized_and_fence_released");
        }
        if (current.Verification == VerificationState.Passed && request.Verification != VerificationState.Passed)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "verification_cannot_regress_after_pass");
        }

        using (SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE request_jobs SET verification=$verification,observation='independent_readback',
 readback_revision=$revision,readback_digest=$digest,verification_result_json=$result,
 verification_error_code=$code,verification_error_message=$message,verification_staged_sequence=$sequence,
 updated_at_utc=$updated,observed_at_utc=$observed,write_fence_released=0,server_unavailable=0
WHERE request_id=$id;"))
        {
            update.Parameters.AddWithValue("$verification", ToDatabaseValue(request.Verification));
            update.Parameters.AddWithValue("$revision", request.ReadbackRevision);
            update.Parameters.AddWithValue("$digest", digest);
            update.Parameters.AddWithValue("$result", (object?)request.ResultJson ?? DBNull.Value);
            update.Parameters.AddWithValue("$code", (object?)request.ErrorCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$message", (object?)request.ErrorMessage ?? DBNull.Value);
            update.Parameters.AddWithValue("$sequence", request.ExpectedPluginSequence.Value);
            update.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$observed", FormatTimestamp(observed));
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }

        LedgerJob updated = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new VerificationResult(VerificationDisposition.Applied, updated);
    }

    /// <summary>
    /// Completes a staged, passed write after the plugin's durable finalizer has
    /// advanced exactly one sequence. Replays are idempotent only for that stage.
    /// </summary>
    public VerificationResult FinalizeVerifiedWriteFence(FinalizeVerificationRequest request)
    {
        using IDisposable lease = EnterOperation();
        _operationsStarted = true;
        ArgumentNullException.ThrowIfNull(request);
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        if (request.StagedPluginSequence < 0 || request.FinalizedPluginSequence < 0
            || request.FinalizedPluginSequence != request.StagedPluginSequence + 1
            || request.ReadbackRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Finalization sequence and readback revision are invalid.");
        if (!IsSha256Hex(request.ReadbackDigest) || !IsSha256Hex(request.CommittedResultDigest))
            throw new ArgumentException("Finalization digests must be 64 hexadecimal characters.", nameof(request));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        DateTimeOffset observed = request.ObservedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        LedgerJob? current = ReadJob(connection, transaction, id);
        if (current is null)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.NotFound, null, "request_id_not_found");
        }
        bool identityMatches = current.IsWrite && current.ContextId == context
            && current.CompleteTargetJson == target && current.ProviderGeneration == generation
            && current.Phase == JobPhase.Completed && current.Effects == EffectState.Committed
            && current.Verification == VerificationState.Passed
            && current.VerificationStagedPluginSequence == request.StagedPluginSequence
            && current.ReadbackRevision == request.ReadbackRevision
            && string.Equals(current.ReadbackDigest, request.ReadbackDigest, StringComparison.Ordinal)
            && string.Equals(ComputeCommittedResultDigest(current.ResultJson), request.CommittedResultDigest, StringComparison.Ordinal);
        if (!identityMatches)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "verification_finalize_identity_mismatch");
        }
        if (current.WriteFenceReleased)
        {
            bool sameFinal = current.PluginSequence == request.FinalizedPluginSequence;
            transaction.Rollback();
            return sameFinal ? new VerificationResult(VerificationDisposition.Duplicate, current)
                : new VerificationResult(VerificationDisposition.RejectedState, current, "finalize_sequence_conflict");
        }
        if (current.PluginSequence != request.StagedPluginSequence)
        {
            transaction.Rollback();
            return new VerificationResult(VerificationDisposition.RejectedState, current, "staged_plugin_sequence_mismatch");
        }

        using (SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE request_jobs SET plugin_sequence=$sequence,write_fence_released=1,server_unavailable=0,
 updated_at_utc=$updated,observed_at_utc=$observed WHERE request_id=$id;
"))
        {
            update.Parameters.AddWithValue("$sequence", request.FinalizedPluginSequence);
            update.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$observed", FormatTimestamp(observed));
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        ReleaseFence(connection, transaction, id);
        LedgerJob finalized = ReadJob(connection, transaction, id)!;
        transaction.Commit();
        return new VerificationResult(VerificationDisposition.Applied, finalized);
    }

    // Evidence-based failed-stage recovery. Does not release any fence or alter the committed result.
    public bool RecoverArchiveRoundoffStage(VerificationRequest request, string nativeProof, long expectedPreviousSequence)
    {
        using IDisposable lease=EnterOperation(); _operationsStarted=true;
        using SqliteConnection connection=OpenConnection();
        using SqliteTransaction transaction=connection.BeginTransaction(deferred:false);
        LedgerJob? current=ReadJob(connection,transaction,request.RequestId);
        if(current==null || !current.IsWrite || current.WriteFenceReleased || current.Phase!=JobPhase.Completed || current.Effects!=EffectState.Committed
          || current.Verification!=VerificationState.Failed || current.PluginSequence!=expectedPreviousSequence
          || current.VerificationStagedPluginSequence!=expectedPreviousSequence || current.ReadbackRevision!=expectedPreviousSequence
          || current.ContextId!=request.ContextId || current.CompleteTargetJson!=CanonicalizeJsonObject(request.CompleteTargetJson,"target")
          || current.ProviderGeneration!=request.ProviderGeneration || request.Verification!=VerificationState.Passed
          || request.ExpectedPluginSequence!=expectedPreviousSequence+1 || request.ReadbackRevision!=expectedPreviousSequence+1
          || !FoundationDrawingPlan.VerifyReadback(current.ResultJson,nativeProof,current.CanonicalParametersJson)
          || Newtonsoft.Json.Linq.JObject.Parse(nativeProof).Value<string>("schema") is not ("foundation-archive-roundoff-recovery-v1" or "foundation-save-font-recovery-v1" or "foundation-save-xref-cache-recovery-v1" or "foundation-text-anchor-recovery-v1" or "foundation-block-cache-recovery-v1")) {transaction.Rollback();return false;}
        string digest=ComputeVerificationDigest(current.RequestId,current.ContextId,current.CompleteTargetJson,current.ProviderGeneration,request);
        using var update=CreateCommand(connection,transaction,@"UPDATE request_jobs SET plugin_sequence=$sequence,verification='passed',observation='independent_readback',readback_revision=$sequence,readback_digest=$digest,verification_result_json=$result,verification_error_code=NULL,verification_error_message=NULL,verification_staged_sequence=$sequence,updated_at_utc=$updated,server_unavailable=0 WHERE request_id=$id;");
        update.Parameters.AddWithValue("$sequence",request.ReadbackRevision);update.Parameters.AddWithValue("$digest",digest);update.Parameters.AddWithValue("$result",request.ResultJson!);update.Parameters.AddWithValue("$updated",FormatTimestamp(DateTimeOffset.UtcNow));update.Parameters.AddWithValue("$id",current.RequestId);update.ExecuteNonQuery();transaction.Commit();return true;
    }

    public LedgerJob? Get(string requestId)
    {
        using IDisposable lease = EnterOperation();
        _operationsStarted = true;
        requestId = RequiredIdentifier(requestId, nameof(requestId));
        using SqliteConnection connection = OpenConnection();
        return ReadJob(connection, null, requestId);
    }

    /// <summary>
    /// Reconciles a request which was durably owned by the bound-plugin store
    /// before a prior server stopped. This API is limited to the startup window
    /// after recovery and before any request operations are served.
    /// </summary>
    public ReserveResult ReserveRecovered(ReserveRequest request)
    {
        using IDisposable lease = EnterOperation(exclusive: true);
        EnsureStartupReconciliationWindow();
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsWrite || request.GlobalFence || request.RiskKeys is { Count: > 0 })
            throw new ArgumentException("Recovered reservations are limited to read-only requests without risk keys.", nameof(request));

        NormalizedRequest normalized = NormalizeRequest(request);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        LedgerJob? existing = ReadJob(connection, transaction, normalized.RequestId);
        if (existing is not null)
        {
            bool same = string.Equals(existing.PayloadDigest, normalized.PayloadDigest, StringComparison.Ordinal)
                && string.Equals(existing.ContextId, normalized.ContextId, StringComparison.Ordinal)
                && string.Equals(existing.ToolName, normalized.ToolName, StringComparison.Ordinal)
                && string.Equals(existing.CanonicalParametersJson, normalized.ParametersJson, StringComparison.Ordinal)
                && string.Equals(existing.CompleteTargetJson, normalized.TargetJson, StringComparison.Ordinal)
                && !existing.IsWrite && !existing.GlobalFence
                && existing.HeldRiskKeys.Count == 0
                && string.Equals(existing.ProviderGeneration, normalized.ProviderGeneration, StringComparison.Ordinal);
            transaction.Rollback();
            return same
                ? new ReserveResult(ReserveDisposition.Existing, existing, Array.Empty<string>())
                : new ReserveResult(ReserveDisposition.RequestIdConflict, existing, Array.Empty<string>());
        }

        using (SqliteCommand insert = CreateCommand(connection, transaction, @"
INSERT INTO request_jobs (
 request_id,payload_digest,context_id,tool_name,canonical_parameters_json,complete_target_json,
 is_write,risk_keys_json,phase,effects,verification,observation,plugin_sequence,result_json,error_code,error_message,
 created_at_utc,updated_at_utc,observed_at_utc,write_fence_released,phase_before_unknown,provider_generation,
 global_fence,plugin_snapshot_digest,readback_revision,readback_digest,verification_result_json,
 verification_error_code,verification_error_message,server_unavailable)
VALUES ($id,$digest,$context,$tool,$parameters,$target,0,'[]','queued','none','not_checked','recovery',NULL,
 NULL,NULL,NULL,$created,$updated,$observed,0,NULL,$generation,0,NULL,NULL,NULL,NULL,NULL,NULL,1);"))
        {
            insert.Parameters.AddWithValue("$id", normalized.RequestId);
            insert.Parameters.AddWithValue("$digest", normalized.PayloadDigest);
            insert.Parameters.AddWithValue("$context", normalized.ContextId);
            insert.Parameters.AddWithValue("$tool", normalized.ToolName);
            insert.Parameters.AddWithValue("$parameters", normalized.ParametersJson);
            insert.Parameters.AddWithValue("$target", normalized.TargetJson);
            insert.Parameters.AddWithValue("$created", FormatTimestamp(now));
            insert.Parameters.AddWithValue("$updated", FormatTimestamp(now));
            insert.Parameters.AddWithValue("$observed", FormatTimestamp(now));
            insert.Parameters.AddWithValue("$generation", normalized.ProviderGeneration);
            insert.ExecuteNonQuery();
        }
        LedgerJob recovered = ReadJob(connection, transaction, normalized.RequestId)!;
        transaction.Commit();
        return new ReserveResult(ReserveDisposition.Created, recovered, Array.Empty<string>());
    }

    /// <summary>Lists rows only during the gated startup reconciliation window.</summary>
    public IReadOnlyList<LedgerJob> ListJobsForStartupReconciliation()
    {
        using IDisposable lease = EnterOperation(exclusive: true);
        EnsureStartupReconciliationWindow();
        using SqliteConnection connection = OpenConnection();
        var jobs = new List<LedgerJob>();
        using SqliteCommand command = CreateCommand(connection, null,
            "SELECT request_id FROM request_jobs ORDER BY request_id COLLATE BINARY;");
        using SqliteDataReader reader = command.ExecuteReader();
        var requestIds = new List<string>();
        while (reader.Read()) requestIds.Add(reader.GetString(0));
        reader.Close();
        foreach (string requestId in requestIds)
            jobs.Add(ReadJob(connection, null, requestId)!);
        return jobs;
    }

    /// <summary>
    /// Marks the server's observation unavailable without inventing a plugin
    /// state. The generation and nullable sequence are compare-and-set guards.
    /// </summary>
    public bool MarkUnavailable(string requestId, string providerGeneration, long? expectedPluginSequence)
    {
        using IDisposable lease = EnterOperation(exclusive: true);
        if (!_recoveryCompleted)
            throw new InvalidOperationException("RecoverUnfinished must run before request availability can be changed.");
        string id = RequiredIdentifier(requestId, nameof(requestId));
        string generation = RequiredGeneration(providerGeneration, nameof(providerGeneration));
        if (expectedPluginSequence < 0) throw new ArgumentOutOfRangeException(nameof(expectedPluginSequence));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE request_jobs SET observation='recovery',server_unavailable=1,
 updated_at_utc=$updated,observed_at_utc=$observed
WHERE request_id=$id AND provider_generation=$generation
 AND (($sequence IS NULL AND plugin_sequence IS NULL) OR plugin_sequence=$sequence);");
        update.Parameters.AddWithValue("$updated", FormatTimestamp(now));
        update.Parameters.AddWithValue("$observed", FormatTimestamp(now));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$generation", generation);
        update.Parameters.AddWithValue("$sequence", (object?)expectedPluginSequence ?? DBNull.Value);
        bool changed = update.ExecuteNonQuery() == 1;
        transaction.Commit();
        return changed;
    }

    /// <summary>Closes the startup-only reservation/listing window after reconciliation succeeds.</summary>
    public void CompleteStartupReconciliation()
    {
        using IDisposable lease = EnterOperation(exclusive: true);
        EnsureStartupReconciliationWindow();
        _startupReconciliationCompleted = true;
        _operationsStarted = true;
    }

    /// <summary>Marks server observations unavailable without changing plugin phase/effects/verification/sequence.</summary>
    public int RecoverUnfinished()
    {
        using IDisposable lease = EnterOperation(exclusive: true);
        if (_recoveryCompleted) return 0;
        if (_operationsStarted)
            throw new InvalidOperationException("RecoverUnfinished must run once before this owner handles any requests.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand update = CreateCommand(connection, transaction, @"
UPDATE request_jobs SET observation='recovery',server_unavailable=1,updated_at_utc=$updated,observed_at_utc=$observed
WHERE server_unavailable=0 AND (phase IN ('queued','running','cancellation_requested')
 OR (is_write=1 AND write_fence_released=0));");
        update.Parameters.AddWithValue("$updated", FormatTimestamp(now));
        update.Parameters.AddWithValue("$observed", FormatTimestamp(now));
        int count = update.ExecuteNonQuery();
        transaction.Commit();
        _recoveryCompleted = true;
        return count;
    }

    private void EnsureStartupReconciliationWindow()
    {
        if (!_recoveryCompleted || _operationsStarted || _startupReconciliationCompleted)
            throw new InvalidOperationException("Startup reconciliation must run after RecoverUnfinished and before request operations are served.");
    }

    public static string CanonicalizeJsonObject(string json, string parameterName)
    {
        if (json is null) throw new ArgumentNullException(parameterName);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The JSON value must be an object.", parameterName);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static string ComputeExpectedPayloadDigest(ReserveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NormalizeRequest(request).PayloadDigest;
    }

    internal static string ComputeExpectedVerificationDigest(VerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        return ComputeVerificationDigest(id, context, target, generation, request);
    }

    internal static bool IsValidSha256Hex(string? value) => value is not null && IsSha256Hex(value);

    public static string NormalizeRiskKey(string riskKey) => DrawingRiskKey.Normalize(riskKey);

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

    private void InitializeDatabase()
    {
        using SqliteConnection connection = OpenConnection();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if ((long)(version.ExecuteScalar() ?? 0L) > CurrentSchemaVersion)
                throw new InvalidOperationException("Request ledger schema is newer than this server.");
        }
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using (SqliteCommand schema = CreateCommand(connection, transaction, @"
CREATE TABLE IF NOT EXISTS request_jobs (
 request_id TEXT PRIMARY KEY COLLATE BINARY,payload_digest TEXT NOT NULL,context_id TEXT NOT NULL,tool_name TEXT NOT NULL,
 canonical_parameters_json TEXT NOT NULL,complete_target_json TEXT NOT NULL,is_write INTEGER NOT NULL CHECK(is_write IN(0,1)),
 risk_keys_json TEXT NOT NULL,phase TEXT NOT NULL,effects TEXT NOT NULL,verification TEXT NOT NULL,observation TEXT NOT NULL,
 plugin_sequence INTEGER NULL,result_json TEXT NULL,error_code TEXT NULL,error_message TEXT NULL,
 created_at_utc TEXT NOT NULL,updated_at_utc TEXT NOT NULL,observed_at_utc TEXT NULL,
 write_fence_released INTEGER NOT NULL DEFAULT 0 CHECK(write_fence_released IN(0,1)),phase_before_unknown TEXT NULL,
 provider_generation TEXT NOT NULL DEFAULT '',global_fence INTEGER NOT NULL DEFAULT 0 CHECK(global_fence IN(0,1)),
 plugin_snapshot_digest TEXT NULL,readback_revision INTEGER NULL,readback_digest TEXT NULL,
 verification_result_json TEXT NULL,verification_error_code TEXT NULL,verification_error_message TEXT NULL,
 server_unavailable INTEGER NOT NULL DEFAULT 0 CHECK(server_unavailable IN(0,1)),verification_staged_sequence INTEGER NULL);
CREATE TABLE IF NOT EXISTS write_fences (
 risk_key TEXT PRIMARY KEY COLLATE BINARY,request_id TEXT NOT NULL REFERENCES request_jobs(request_id) ON DELETE CASCADE,
 created_at_utc TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_request_jobs_context_created ON request_jobs(context_id,created_at_utc);"))
        {
            schema.ExecuteNonQuery();
        }
        EnsureColumn(connection, transaction, "provider_generation", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction, "global_fence", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction, "plugin_snapshot_digest", "TEXT NULL");
        EnsureColumn(connection, transaction, "readback_revision", "INTEGER NULL");
        EnsureColumn(connection, transaction, "readback_digest", "TEXT NULL");
        EnsureColumn(connection, transaction, "verification_result_json", "TEXT NULL");
        EnsureColumn(connection, transaction, "verification_error_code", "TEXT NULL");
        EnsureColumn(connection, transaction, "verification_error_message", "TEXT NULL");
        EnsureColumn(connection, transaction, "server_unavailable", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction, "verification_staged_sequence", "INTEGER NULL");
        using SqliteCommand setVersion = CreateCommand(connection, transaction, $"PRAGMA user_version={CurrentSchemaVersion};");
        setVersion.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction, string name, string declaration)
    {
        bool found = false;
        using SqliteCommand info = CreateCommand(connection, transaction, "PRAGMA table_info(request_jobs);");
        using (SqliteDataReader reader = info.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
        }
        if (found) return;
        using SqliteCommand alter = CreateCommand(connection, transaction, $"ALTER TABLE request_jobs ADD COLUMN {name} {declaration};");
        alter.ExecuteNonQuery();
    }

    private static NormalizedRequest NormalizeRequest(ReserveRequest request)
    {
        string id = RequiredIdentifier(request.RequestId, nameof(request.RequestId));
        string context = RequiredIdentifier(request.ContextId, nameof(request.ContextId));
        string tool = RequiredIdentifier(request.ToolName, nameof(request.ToolName));
        string generation = RequiredGeneration(request.ProviderGeneration, nameof(request.ProviderGeneration));
        string parameters = CanonicalizeJsonObject(request.CanonicalParametersJson, nameof(request.CanonicalParametersJson));
        string target = CanonicalizeJsonObject(request.CompleteTargetJson, nameof(request.CompleteTargetJson));
        string[] keys = request.IsWrite
            ? (request.RiskKeys ?? Array.Empty<string>()).Select(NormalizeRiskKey).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        if (!request.IsWrite && request.GlobalFence) throw new ArgumentException("Read requests cannot hold a global write fence.");
        if (!request.IsWrite && request.RiskKeys is { Count: > 0 }) throw new ArgumentException("Read requests cannot hold write risk keys.");
        if (request.IsWrite && request.GlobalFence && keys.Length != 0) throw new ArgumentException("Global fence cannot be weakened with caller keys.");
        if (request.IsWrite && !request.GlobalFence)
        {
            string? targetPath = ExtractTargetDrawingPath(target);
            if (targetPath is null)
                throw new ArgumentException("Target has no verifiable drawing path; reserve the global fence instead.", nameof(request.CompleteTargetJson));
            if (!keys.Contains(targetPath, StringComparer.Ordinal))
                throw new ArgumentException("Risk keys must include the complete target drawing path; aliases are supplemental only.", nameof(request.RiskKeys));
        }
        string[] fenceKeys = request.GlobalFence ? [GlobalFenceKey] : keys;
        string digest = ComputeRequestDigest(context, tool, generation, parameters, target, request.IsWrite, request.GlobalFence, keys);
        return new NormalizedRequest(id, context, tool, generation, parameters, target, request.IsWrite, request.GlobalFence, keys, fenceKeys, digest);
    }

    private static string? ExtractTargetDrawingPath(string canonicalTargetJson)
    {
        using JsonDocument document = JsonDocument.Parse(canonicalTargetJson);
        return FindDrawingPath(document.RootElement, inDrawingObject: false);
    }

    private static string? FindDrawingPath(JsonElement element, bool inDrawingObject)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string name = property.Name;
                bool namedDrawing = name.Equals("document", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("drawing", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("document_path", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("drawing_path", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("dwg_path", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("dwg", StringComparison.OrdinalIgnoreCase);
                if (property.Value.ValueKind == JsonValueKind.String
                    && (namedDrawing || (inDrawingObject && name.Equals("path", StringComparison.OrdinalIgnoreCase))))
                {
                    string? candidate = property.Value.GetString();
                    if (candidate is not null && DrawingRiskKey.TryNormalizePath(candidate, out string pathRiskKey))
                        return pathRiskKey;
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

    private static string ComputeRequestDigest(string context, string tool, string generation, string parameters, string target, bool write, bool global, IReadOnlyList<string> keys)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        using (JsonDocument p = JsonDocument.Parse(parameters))
        using (JsonDocument t = JsonDocument.Parse(target))
        {
            writer.WriteStartObject();
            writer.WriteNumber("digest_version", 2);
            writer.WriteString("context_id", context);
            writer.WriteString("tool_name", tool);
            writer.WriteString("provider_generation", generation);
            writer.WritePropertyName("parameters"); p.RootElement.WriteTo(writer);
            writer.WritePropertyName("complete_target"); t.RootElement.WriteTo(writer);
            writer.WriteBoolean("is_write", write);
            writer.WriteBoolean("global_fence", global);
            writer.WritePropertyName("risk_keys"); writer.WriteStartArray();
            foreach (string key in keys) writer.WriteStringValue(key);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static string ComputePluginSnapshotDigest(string generation, AdvanceRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("generation", generation);
            writer.WriteNumber("sequence", request.PluginSequence);
            writer.WriteString("phase", ToDatabaseValue(request.Phase));
            writer.WriteString("effects", ToDatabaseValue(request.Effects));
            writer.WriteString("verification", ToDatabaseValue(request.Verification));
            writer.WriteString("observation", ToDatabaseValue(request.Observation));
            WriteNullable(writer, "result", request.ResultJson);
            WriteNullable(writer, "error_code", request.ErrorCode);
            WriteNullable(writer, "error_message", request.ErrorMessage);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static string ComputeVerificationDigest(string id, string context, string target, string generation, VerificationRequest request)
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
            if (request.ExpectedPluginSequence.HasValue)
                writer.WriteNumber("expected_plugin_sequence", request.ExpectedPluginSequence.Value);
            else
                writer.WriteNull("expected_plugin_sequence");
            writer.WriteString("verification", ToDatabaseValue(request.Verification));
            WriteNullable(writer, "result", request.ResultJson);
            WriteNullable(writer, "error_code", request.ErrorCode);
            WriteNullable(writer, "error_message", request.ErrorMessage);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static string? ComputeCommittedResultDigest(string? resultJson) => resultJson is null
        ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resultJson)));

    private static bool IsSha256Hex(string value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                JsonProperty[] properties = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                for (int i = 1; i < properties.Length; i++)
                    if (properties[i - 1].Name == properties[i].Name) throw new ArgumentException("Duplicate JSON property names cannot be canonicalized.");
                writer.WriteStartObject();
                foreach (JsonProperty p in properties) { writer.WritePropertyName(p.Name); WriteCanonical(writer, p.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (JsonElement item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText(), false); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new ArgumentException("Undefined JSON cannot be canonicalized.");
        }
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
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
            throw new ArgumentException("Provider generation must be a 1-128 character opaque ASCII identifier.", name);
        return value;
    }

    private static bool CanTransition(LedgerJob current, AdvanceRequest next, out string? reason)
    {
        reason = null;
        if (IsTerminal(current.Phase))
        {
            // A committed snapshot may be observed before the plugin persists its
            // readback stage. Mirror only that next sequence; this does not change
            // phase/effects/result or release the fence. Independent verification
            // and the exact two-sided finalization still follow separately.
            bool readbackProgress=current.IsWrite&&!current.WriteFenceReleased
                &&current.Phase==JobPhase.Completed&&next.Phase==JobPhase.Completed
                &&current.Effects==EffectState.Committed&&next.Effects==EffectState.Committed
                &&current.Verification==VerificationState.NotChecked&&next.Verification==VerificationState.NotChecked
                &&next.Observation==ObservationState.AuthoritativePluginSnapshot
                &&current.PluginSequence.HasValue&&next.PluginSequence==current.PluginSequence.Value+1
                &&current.ResultJson==next.ResultJson&&current.ErrorCode==next.ErrorCode&&current.ErrorMessage==next.ErrorMessage;
            if(readbackProgress)return true;
            reason = "terminal_phase_is_immutable"; return false;
        }
        if (current.Phase == next.Phase) return true;
        if (current.Phase == JobPhase.Unknown)
        {
            if (next.Observation != ObservationState.AuthoritativePluginSnapshot)
            { reason = "unknown_requires_authoritative_same_generation_snapshot"; return false; }
            return true;
        }
        if (next.Phase == JobPhase.Cancelled)
        {
            bool queued = current.Phase == JobPhase.Queued && next.Effects == EffectState.None
                && next.Observation == ObservationState.AuthoritativePluginSnapshot;
            if (!queued) { reason = "only_queued_work_can_be_authoritatively_cancelled"; return false; }
            return true;
        }
        if (next.Phase == JobPhase.CancellationRequested && current.Phase != JobPhase.Running)
        { reason = "cancellation_request_requires_running_job"; return false; }
        bool allowed = current.Phase switch
        {
            JobPhase.Queued => next.Phase is JobPhase.Running or JobPhase.Completed or JobPhase.Failed or JobPhase.Unknown,
            JobPhase.Running => next.Phase is JobPhase.CancellationRequested or JobPhase.Completed or JobPhase.Failed or JobPhase.Unknown,
            JobPhase.CancellationRequested => next.Phase is JobPhase.Running or JobPhase.Completed or JobPhase.Failed or JobPhase.Unknown,
            _ => false
        };
        if (!allowed) reason = "unsupported_phase_transition";
        return allowed;
    }

    private static bool CanAdvanceEffects(LedgerJob current, AdvanceRequest next, out string? reason)
    {
        reason = null;
        if (current.Effects == EffectState.Committed && next.Effects != EffectState.Committed)
        { reason = "committed_effects_cannot_be_retracted"; return false; }
        if (current.Effects == EffectState.Unknown && next.Effects == EffectState.None
            && (next.Observation != ObservationState.AuthoritativePluginSnapshot || !IsTerminal(next.Phase)))
        { reason = "unknown_effects_require_authoritative_terminal_no_effect_evidence"; return false; }
        if (next.Effects == EffectState.None && IsTerminal(next.Phase)
            && next.Observation != ObservationState.AuthoritativePluginSnapshot)
        { reason = "terminal_no_effects_require_authoritative_plugin_observation"; return false; }
        if (next.Phase == JobPhase.Completed
            && (current.IsWrite ? next.Effects != EffectState.Committed : next.Effects != EffectState.None))
        { reason = current.IsWrite ? "completed_write_must_report_committed_effects" : "completed_read_must_have_no_effects"; return false; }
        if (next.Phase == JobPhase.Cancelled && next.Effects != EffectState.None)
        { reason = "cancelled_job_must_have_no_effects"; return false; }
        return true;
    }

    private static bool IsSafeNoEffectRelease(AdvanceRequest request) =>
        IsTerminal(request.Phase) && request.Effects == EffectState.None
        && request.Observation == ObservationState.AuthoritativePluginSnapshot;

    private static bool IsTerminal(JobPhase phase) => phase is JobPhase.Completed or JobPhase.Failed or JobPhase.Cancelled;

    private static void ReleaseFence(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using SqliteCommand command = CreateCommand(connection, transaction, "DELETE FROM write_fences WHERE request_id=$id;");
        command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }

    private static string ExternalRiskKey(string key) => key == GlobalFenceKey ? "<GLOBAL_DRAWING_FENCE>" : key;

    private static void EnsureEnumValue<T>(T value, string name) where T : struct, Enum
    { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(name); }

    private IDisposable EnterOperation(bool exclusive = false)
    {
        if (exclusive) _lifecycle.EnterWriteLock();
        else _lifecycle.EnterReadLock();
        if (_disposed || _ownerLock is null)
        {
            if (exclusive) _lifecycle.ExitWriteLock();
            else _lifecycle.ExitReadLock();
            throw new ObjectDisposedException(nameof(RequestLedger), "The database owner lock has been released.");
        }
        return new LifecycleLease(_lifecycle, exclusive);
    }

    private sealed class LifecycleLease(ReaderWriterLockSlim lifecycle, bool exclusive) : IDisposable
    {
        public void Dispose()
        {
            if (exclusive) lifecycle.ExitWriteLock();
            else lifecycle.ExitReadLock();
        }
    }

    private static LedgerJob? ReadJob(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = CreateCommand(connection, transaction, @"
SELECT request_id,context_id,tool_name,canonical_parameters_json,complete_target_json,payload_digest,is_write,
 phase,effects,verification,observation,plugin_sequence,result_json,error_code,error_message,created_at_utc,
 updated_at_utc,observed_at_utc,write_fence_released,phase_before_unknown,provider_generation,global_fence,
 readback_revision,verification_result_json,verification_error_code,verification_error_message,
 server_unavailable,plugin_snapshot_digest,readback_digest,verification_staged_sequence FROM request_jobs WHERE request_id=$id;");
        command.Parameters.AddWithValue("$id", id);
        LedgerJob job;
        using (SqliteDataReader r = command.ExecuteReader())
        {
            if (!r.Read()) return null;
            job = new LedgerJob(
                RequestId:r.GetString(0),ContextId:r.GetString(1),ToolName:r.GetString(2),
                CanonicalParametersJson:r.GetString(3),CompleteTargetJson:r.GetString(4),PayloadDigest:r.GetString(5),
                IsWrite:r.GetInt64(6)==1,Phase:ParsePhase(r.GetString(7)),Effects:ParseEffects(r.GetString(8)),
                Verification:ParseVerification(r.GetString(9)),Observation:ParseObservation(r.GetString(10)),
                PluginSequence:r.IsDBNull(11)?null:r.GetInt64(11),ResultJson:r.IsDBNull(12)?null:r.GetString(12),
                ErrorCode:r.IsDBNull(13)?null:r.GetString(13),ErrorMessage:r.IsDBNull(14)?null:r.GetString(14),
                CreatedAt:ParseTimestamp(r.GetString(15)),UpdatedAt:ParseTimestamp(r.GetString(16)),
                ObservedAt:r.IsDBNull(17)?null:ParseTimestamp(r.GetString(17)),WriteFenceReleased:r.GetInt64(18)==1,
                HeldRiskKeys:Array.Empty<string>(),PhaseBeforeUnknown:r.IsDBNull(19)?null:ParsePhase(r.GetString(19)),
                ProviderGeneration:r.GetString(20),GlobalFence:r.GetInt64(21)==1,
                ReadbackRevision:r.IsDBNull(22)?null:r.GetInt64(22),
                VerificationResultJson:r.IsDBNull(23)?null:r.GetString(23),
                VerificationErrorCode:r.IsDBNull(24)?null:r.GetString(24),
                VerificationErrorMessage:r.IsDBNull(25)?null:r.GetString(25),
                ServerUnavailable:r.GetInt64(26)==1,PluginSnapshotDigest:r.IsDBNull(27)?null:r.GetString(27),
                ReadbackDigest:r.IsDBNull(28)?null:r.GetString(28),
                VerificationStagedPluginSequence:r.IsDBNull(29)?null:r.GetInt64(29));
        }
        return job with { HeldRiskKeys = ReadRiskKeys(connection, transaction, id) };
    }

    private static string[] ReadRiskKeys(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = CreateCommand(connection, transaction,
            "SELECT risk_key FROM write_fences WHERE request_id=$id AND risk_key<>$global ORDER BY risk_key COLLATE BINARY;");
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$global", GlobalFenceKey);
        using SqliteDataReader reader = command.ExecuteReader();
        var keys = new List<string>(); while (reader.Read()) keys.Add(reader.GetString(0));
        return keys.ToArray();
    }

    private static SqliteCommand CreateCommand(SqliteConnection c, SqliteTransaction? t, string sql)
    { SqliteCommand cmd = c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=sql; return cmd; }
    private static string FormatTimestamp(DateTimeOffset v) => v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string v) => DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);

    private static string ToDatabaseValue(JobPhase v) => v switch
    {
        JobPhase.Queued=>"queued",JobPhase.Running=>"running",JobPhase.CancellationRequested=>"cancellation_requested",
        JobPhase.Completed=>"completed",JobPhase.Failed=>"failed",JobPhase.Cancelled=>"cancelled",JobPhase.Unknown=>"unknown",
        _=>throw new ArgumentOutOfRangeException(nameof(v))
    };
    private static string ToDatabaseValue(EffectState v) => v switch
    { EffectState.None=>"none",EffectState.Unknown=>"unknown",EffectState.Committed=>"committed",_=>throw new ArgumentOutOfRangeException(nameof(v)) };
    private static string ToDatabaseValue(VerificationState v) => v switch
    { VerificationState.NotChecked=>"not_checked",VerificationState.Passed=>"passed",VerificationState.Failed=>"failed",VerificationState.Unknown=>"unknown",_=>throw new ArgumentOutOfRangeException(nameof(v)) };
    private static string ToDatabaseValue(ObservationState v) => v switch
    {
        ObservationState.None=>"none",ObservationState.PluginSnapshot=>"plugin_snapshot",
        ObservationState.AuthoritativePluginSnapshot=>"authoritative_plugin_snapshot",
        ObservationState.IndependentReadback=>"independent_readback",ObservationState.Recovery=>"recovery",
        _=>throw new ArgumentOutOfRangeException(nameof(v))
    };
    private static JobPhase ParsePhase(string v) => v switch
    {
        "queued"=>JobPhase.Queued,"running"=>JobPhase.Running,"cancellation_requested"=>JobPhase.CancellationRequested,
        "completed"=>JobPhase.Completed,"failed"=>JobPhase.Failed,"cancelled"=>JobPhase.Cancelled,"unknown"=>JobPhase.Unknown,
        _=>throw new InvalidDataException($"Unknown phase {v}")
    };
    private static EffectState ParseEffects(string v) => v switch
    { "none"=>EffectState.None,"unknown"=>EffectState.Unknown,"committed"=>EffectState.Committed,_=>throw new InvalidDataException($"Unknown effects {v}") };
    private static VerificationState ParseVerification(string v) => v switch
    { "not_checked"=>VerificationState.NotChecked,"passed"=>VerificationState.Passed,"failed"=>VerificationState.Failed,"unknown"=>VerificationState.Unknown,_=>throw new InvalidDataException($"Unknown verification {v}") };
    private static ObservationState ParseObservation(string v) => v switch
    {
        "none"=>ObservationState.None,"plugin_snapshot"=>ObservationState.PluginSnapshot,
        "authoritative_plugin_snapshot"=>ObservationState.AuthoritativePluginSnapshot,
        "independent_readback"=>ObservationState.IndependentReadback,"recovery"=>ObservationState.Recovery,
        _=>throw new InvalidDataException($"Unknown observation {v}")
    };

    private sealed record NormalizedRequest(string RequestId,string ContextId,string ToolName,string ProviderGeneration,
        string ParametersJson,string TargetJson,bool IsWrite,bool GlobalFence,string[] RiskKeys,string[] FenceKeys,string PayloadDigest);
}
