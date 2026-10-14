#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimwright.Dwg.Plugin.Execution
{
    public enum PluginJobPhase
    {
        Queued,
        Running,
        CancellationRequested,
        Completed,
        Failed,
        Cancelled,
        Unknown
    }

    public enum PluginEffectState
    {
        None,
        Unknown,
        Committed
    }

    public enum PluginVerificationState
    {
        NotChecked,
        Passed,
        Failed,
        Unknown
    }

    public enum PluginObservationState
    {
        PluginSnapshot,
        Recovery
    }

    public enum PluginOwnerLiveness
    {
        Alive,
        ConfirmedLost,
        Indeterminate
    }

    public enum PluginSubmitDisposition
    {
        Created,
        Existing,
        RequestIdConflict,
        FenceConflict
    }

    public enum PluginCancelDisposition
    {
        Cancelled,
        CancellationRequested,
        AlreadyTerminal,
        NotFound,
        Rejected
    }

    public enum PluginClaimDisposition
    {
        Claimed,
        NoWork,
        QueueDeadlineExpired
    }

    public enum PluginTransitionDisposition
    {
        Applied,
        Duplicate,
        StaleRevision,
        Rejected,
        NotFound
    }

    /// <summary>
    /// A narrow, typed declaration of why a write handler can authoritatively
    /// say that no database effects were committed. This is not a boolean
    /// assertion and is accepted only by RecordNoEffectTerminal.
    /// </summary>
    public enum PluginNoEffectOutcome
    {
        RejectedBeforeTransaction,
        RolledBackBeforeCommit
    }

    /// <summary>
    /// One plugin runtime owner. Process start time prevents PID reuse from being
    /// mistaken for a live owner; PluginGeneration distinguishes reloads in one process.
    /// </summary>
    public sealed class PluginOwnerIdentity
    {
        public PluginOwnerIdentity(string ownerId, int processId, DateTimeOffset processStartedAtUtc, string pluginGeneration)
        {
            OwnerId = ownerId;
            ProcessId = processId;
            ProcessStartedAtUtc = processStartedAtUtc.ToUniversalTime();
            PluginGeneration = pluginGeneration;
        }

        public string OwnerId { get; private set; }
        public int ProcessId { get; private set; }
        public DateTimeOffset ProcessStartedAtUtc { get; private set; }
        public string PluginGeneration { get; private set; }
    }

    /// <summary>Read-only historical snapshot and the observer's view of its former owner.</summary>
    public sealed class PluginHistoricalJobResult
    {
        public PluginHistoricalJobResult(bool found, PluginJobSnapshot? job,
            PluginOwnerIdentity observerIdentity, PluginOwnerLiveness oldOwnerLiveness)
        {
            Found = found;
            Job = job;
            ObserverIdentity = observerIdentity ?? throw new ArgumentNullException(nameof(observerIdentity));
            OldOwnerLiveness = oldOwnerLiveness;
        }

        public bool Found { get; }
        public PluginJobSnapshot? Job { get; }
        public PluginOwnerIdentity ObserverIdentity { get; }
        public PluginOwnerLiveness OldOwnerLiveness { get; }
    }

    /// <summary>
    /// Recovery may change a job only when its owner is positively known to be
    /// gone. A timeout, busy CAD thread, or failed status query is indeterminate.
    /// Production uses the per-generation owner lock plus PID and process start time.
    /// </summary>
    public interface IPluginOwnerLivenessProvider
    {
        PluginOwnerLiveness GetLiveness(PluginOwnerIdentity owner);
    }

    /// <summary>
    /// Trusted, already-bound execution input. The transport must resolve ContextId
    /// and construct target/risk identity before calling the journal.
    /// </summary>
    public sealed class PluginJobSubmission
    {
        public PluginJobSubmission(
            string requestId,
            string contextId,
            string toolName,
            string canonicalParametersJson,
            string completeTargetJson,
            bool isWrite,
            IEnumerable<string> riskKeys,
            bool globalFence,
            DateTimeOffset? queueDeadlineUtc = null)
        {
            RequestId = requestId;
            ContextId = contextId;
            ToolName = toolName;
            CanonicalParametersJson = canonicalParametersJson;
            CompleteTargetJson = completeTargetJson;
            IsWrite = isWrite;
            RiskKeys = Array.AsReadOnly((riskKeys ?? Enumerable.Empty<string>()).ToArray());
            GlobalFence = globalFence;
            QueueDeadlineUtc = queueDeadlineUtc?.ToUniversalTime();
        }

        public string RequestId { get; private set; }
        public string ContextId { get; private set; }
        public string ToolName { get; private set; }
        public string CanonicalParametersJson { get; private set; }
        public string CompleteTargetJson { get; private set; }
        public bool IsWrite { get; private set; }
        public IReadOnlyList<string> RiskKeys { get; private set; }
        public bool GlobalFence { get; private set; }
        public DateTimeOffset? QueueDeadlineUtc { get; private set; }
    }

    public sealed class PluginJobSnapshot
    {
        public PluginJobSnapshot(
            string requestId,
            string contextId,
            string toolName,
            string canonicalParametersJson,
            string completeTargetJson,
            string payloadDigest,
            string ownerId,
            string providerGeneration,
            bool isWrite,
            PluginJobPhase phase,
            PluginEffectState effects,
            PluginVerificationState verification,
            PluginObservationState observation,
            long pluginSequence,
            string? resultJson,
            string? errorCode,
            string? errorMessage,
            DateTimeOffset createdAtUtc,
            DateTimeOffset updatedAtUtc,
            DateTimeOffset? queueDeadlineUtc,
            bool writeFenceReleased,
            bool globalFence,
            IEnumerable<string> heldRiskKeys,
            long? readbackRevision = null,
            string? readbackDigest = null,
            string? verificationResultJson = null,
            string? verificationErrorCode = null,
            string? verificationErrorMessage = null,
            string? committedResultDigest = null,
            long? verificationStagedSequence = null,
            bool writeFenceFinalized = false,
            long? noEffectClaimSequence = null,
            PluginNoEffectOutcome? noEffectOutcome = null)
        {
            RequestId = requestId;
            ContextId = contextId;
            ToolName = toolName;
            CanonicalParametersJson = canonicalParametersJson;
            CompleteTargetJson = completeTargetJson;
            PayloadDigest = payloadDigest;
            OwnerId = ownerId;
            ProviderGeneration = providerGeneration;
            IsWrite = isWrite;
            Phase = phase;
            Effects = effects;
            Verification = verification;
            Observation = observation;
            PluginSequence = pluginSequence;
            ResultJson = resultJson;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            CreatedAtUtc = createdAtUtc.ToUniversalTime();
            UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
            QueueDeadlineUtc = queueDeadlineUtc?.ToUniversalTime();
            WriteFenceReleased = writeFenceReleased;
            GlobalFence = globalFence;
            HeldRiskKeys = Array.AsReadOnly((heldRiskKeys ?? Enumerable.Empty<string>()).ToArray());
            ReadbackRevision = readbackRevision;
            ReadbackDigest = readbackDigest;
            VerificationResultJson = verificationResultJson;
            VerificationErrorCode = verificationErrorCode;
            VerificationErrorMessage = verificationErrorMessage;
            CommittedResultDigest = committedResultDigest;
            VerificationStagedSequence = verificationStagedSequence;
            WriteFenceFinalized = writeFenceFinalized;
            NoEffectClaimSequence = noEffectClaimSequence;
            NoEffectOutcome = noEffectOutcome;
        }

        public string RequestId { get; private set; }
        public string ContextId { get; private set; }
        public string ToolName { get; private set; }
        public string CanonicalParametersJson { get; private set; }
        public string CompleteTargetJson { get; private set; }
        public string PayloadDigest { get; private set; }
        public string OwnerId { get; private set; }
        public string ProviderGeneration { get; private set; }
        public bool IsWrite { get; private set; }
        public PluginJobPhase Phase { get; private set; }
        public PluginEffectState Effects { get; private set; }
        public PluginVerificationState Verification { get; private set; }
        public PluginObservationState Observation { get; private set; }
        public long PluginSequence { get; private set; }
        public string? ResultJson { get; private set; }
        public string? ErrorCode { get; private set; }
        public string? ErrorMessage { get; private set; }
        public DateTimeOffset CreatedAtUtc { get; private set; }
        public DateTimeOffset UpdatedAtUtc { get; private set; }
        public DateTimeOffset? QueueDeadlineUtc { get; private set; }
        public bool WriteFenceReleased { get; private set; }
        public bool GlobalFence { get; private set; }
        public IReadOnlyList<string> HeldRiskKeys { get; private set; }
        public long? ReadbackRevision { get; private set; }
        public string? ReadbackDigest { get; private set; }
        public string? VerificationResultJson { get; private set; }
        public string? VerificationErrorCode { get; private set; }
        public string? VerificationErrorMessage { get; private set; }
        /// <summary>SHA-256 of the immutable result first persisted by RecordCommitted.</summary>
        public string? CommittedResultDigest { get; private set; }
        /// <summary>Sequence written by RecordVerificationPendingRelease.</summary>
        public long? VerificationStagedSequence { get; private set; }
        /// <summary>True only when FinalizeVerifiedWriteFence completed durably.</summary>
        public bool WriteFenceFinalized { get; private set; }
        /// <summary>The durable claim sequence for the restricted no-effect terminal path.</summary>
        public long? NoEffectClaimSequence { get; private set; }
        public PluginNoEffectOutcome? NoEffectOutcome { get; private set; }
    }

    public sealed class PluginSubmitResult
    {
        public PluginSubmitResult(PluginSubmitDisposition disposition, PluginJobSnapshot? job, IEnumerable<string>? conflictingRiskKeys = null)
        {
            Disposition = disposition;
            Job = job;
            ConflictingRiskKeys = Array.AsReadOnly((conflictingRiskKeys ?? Enumerable.Empty<string>()).ToArray());
        }

        public PluginSubmitDisposition Disposition { get; private set; }
        public PluginJobSnapshot? Job { get; private set; }
        public IReadOnlyList<string> ConflictingRiskKeys { get; private set; }
    }

    public sealed class PluginCancelResult
    {
        public PluginCancelResult(PluginCancelDisposition disposition, PluginJobSnapshot? job, string? reason = null)
        {
            Disposition = disposition;
            Job = job;
            Reason = reason;
        }

        public PluginCancelDisposition Disposition { get; private set; }
        public PluginJobSnapshot? Job { get; private set; }
        public string? Reason { get; private set; }
    }

    public sealed class PluginClaimResult
    {
        public PluginClaimResult(PluginClaimDisposition disposition, PluginJobSnapshot? job, string? expiredRequestId = null)
        {
            Disposition = disposition;
            Job = job;
            ExpiredRequestId = expiredRequestId;
        }

        public PluginClaimDisposition Disposition { get; private set; }
        public PluginJobSnapshot? Job { get; private set; }
        public string? ExpiredRequestId { get; private set; }
    }

    public sealed class PluginTransitionResult
    {
        public PluginTransitionResult(PluginTransitionDisposition disposition, PluginJobSnapshot? job, string? reason = null)
        {
            Disposition = disposition;
            Job = job;
            Reason = reason;
        }

        public PluginTransitionDisposition Disposition { get; private set; }
        public PluginJobSnapshot? Job { get; private set; }
        public string? Reason { get; private set; }
    }

    public sealed class PluginVerificationRequest
    {
        public PluginVerificationRequest(
            string requestId,
            string contextId,
            string completeTargetJson,
            string providerGeneration,
            long readbackRevision,
            PluginVerificationState verification,
            string? resultJson = null,
            string? errorCode = null,
            string? errorMessage = null,
            string? ownerId = null,
            string? payloadDigest = null,
            long? expectedPluginSequence = null,
            string? committedResultDigest = null)
        {
            RequestId = requestId;
            ContextId = contextId;
            CompleteTargetJson = completeTargetJson;
            ProviderGeneration = providerGeneration;
            ReadbackRevision = readbackRevision;
            Verification = verification;
            ResultJson = resultJson;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            OwnerId = ownerId;
            PayloadDigest = payloadDigest;
            ExpectedPluginSequence = expectedPluginSequence;
            CommittedResultDigest = committedResultDigest;
        }

        public string RequestId { get; private set; }
        public string ContextId { get; private set; }
        public string CompleteTargetJson { get; private set; }
        public string ProviderGeneration { get; private set; }
        public long ReadbackRevision { get; private set; }
        public PluginVerificationState Verification { get; private set; }
        public string? ResultJson { get; private set; }
        public string? ErrorCode { get; private set; }
        public string? ErrorMessage { get; private set; }
        /// <summary>Required by the staged verification API; optional for the legacy API.</summary>
        public string? OwnerId { get; private set; }
        /// <summary>Required by the staged verification API; optional for the legacy API.</summary>
        public string? PayloadDigest { get; private set; }
        /// <summary>The exact completed snapshot sequence against which readback was performed.</summary>
        public long? ExpectedPluginSequence { get; private set; }
        /// <summary>Hash of the original committed handle/result, required by staged write verification.</summary>
        public string? CommittedResultDigest { get; private set; }
    }

    /// <summary>
    /// Exact identity and staged-readback confirmation needed to finalize one
    /// plugin write fence. The staged sequence and readback digest prevent stale
    /// confirmations from releasing a later state or a different request.
    /// </summary>
    public sealed class PluginFinalizeVerifiedWriteFenceRequest
    {
        public PluginFinalizeVerifiedWriteFenceRequest(
            string requestId,
            string contextId,
            string completeTargetJson,
            string ownerId,
            string providerGeneration,
            string payloadDigest,
            string committedResultDigest,
            long stagedPluginSequence,
            long readbackRevision,
            string readbackDigest)
        {
            RequestId = requestId;
            ContextId = contextId;
            CompleteTargetJson = completeTargetJson;
            OwnerId = ownerId;
            ProviderGeneration = providerGeneration;
            PayloadDigest = payloadDigest;
            CommittedResultDigest = committedResultDigest;
            StagedPluginSequence = stagedPluginSequence;
            ReadbackRevision = readbackRevision;
            ReadbackDigest = readbackDigest;
        }

        public string RequestId { get; private set; }
        public string ContextId { get; private set; }
        public string CompleteTargetJson { get; private set; }
        public string OwnerId { get; private set; }
        public string ProviderGeneration { get; private set; }
        public string PayloadDigest { get; private set; }
        public string CommittedResultDigest { get; private set; }
        public long StagedPluginSequence { get; private set; }
        public long ReadbackRevision { get; private set; }
        public string ReadbackDigest { get; private set; }
    }

    /// <summary>
    /// A restricted no-effect terminal request produced only after the opted-in
    /// write queue receives one of the two typed pre-commit outcomes.
    /// </summary>
    public sealed class PluginNoEffectTerminalRequest
    {
        public PluginNoEffectTerminalRequest(
            string requestId,
            string contextId,
            string completeTargetJson,
            string ownerId,
            string providerGeneration,
            string payloadDigest,
            long claimSequence,
            PluginNoEffectOutcome outcome,
            string errorCode,
            string errorMessage)
        {
            RequestId = requestId;
            ContextId = contextId;
            CompleteTargetJson = completeTargetJson;
            OwnerId = ownerId;
            ProviderGeneration = providerGeneration;
            PayloadDigest = payloadDigest;
            ClaimSequence = claimSequence;
            Outcome = outcome;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public string RequestId { get; private set; }
        public string ContextId { get; private set; }
        public string CompleteTargetJson { get; private set; }
        public string OwnerId { get; private set; }
        public string ProviderGeneration { get; private set; }
        public string PayloadDigest { get; private set; }
        public long ClaimSequence { get; private set; }
        public PluginNoEffectOutcome Outcome { get; private set; }
        public string ErrorCode { get; private set; }
        public string ErrorMessage { get; private set; }
    }

    /// <summary>
    /// Plugin-side source of execution truth. Implementations must durably commit
    /// each transition before returning success and serialize short writes across
    /// all AutoCAD owners sharing the journal. Reads may observe any owner; every
    /// mutation is restricted to this Owner. TryClaimNext atomically claims only
    /// this owner's FIFO head and permits at most one active job for this owner.
    /// Implementations require an injected liveness provider and must not treat an
    /// Indeterminate owner as lost. Initialize must finish recovery before Submit
    /// or TryClaimNext is enabled.
    /// </summary>
    public interface IPluginIntentJournal : IDisposable
    {
        PluginOwnerIdentity Owner { get; }
        /// <summary>Persistent identity for this journal database, created on first initialization.</summary>
        string JournalScopeId { get; }
        /// <summary>SHA-256 of the normalized configured journal path; the raw path is never exposed.</summary>
        string JournalPathDigest { get; }
        /// <summary>Registers this owner and completes confirmed-lost recovery before work is accepted.</summary>
        int Initialize(DateTimeOffset nowUtc);
        PluginSubmitResult Submit(PluginJobSubmission request, DateTimeOffset nowUtc);
        PluginJobSnapshot? GetSnapshot(string requestId);
        PluginHistoricalJobResult GetHistoricalSnapshot(string requestId);
        PluginCancelResult CancelQueued(string requestId, DateTimeOffset nowUtc);
        PluginClaimResult TryClaimNext(DateTimeOffset nowUtc);
        PluginTransitionResult RecordCommitted(string requestId, string resultJson, DateTimeOffset nowUtc);
        PluginTransitionResult RecordTerminal(
            string requestId,
            PluginJobPhase phase,
            PluginEffectState effects,
            string? resultJson,
            string? errorCode,
            string? errorMessage,
            DateTimeOffset nowUtc);
        PluginTransitionResult RecordNoEffectTerminal(PluginNoEffectTerminalRequest request, DateTimeOffset nowUtc);
        PluginTransitionResult RecordVerification(PluginVerificationRequest request, DateTimeOffset nowUtc);
        PluginTransitionResult RecordVerificationPendingRelease(PluginVerificationRequest request, DateTimeOffset nowUtc);
        PluginTransitionResult FinalizeVerifiedWriteFence(PluginFinalizeVerifiedWriteFenceRequest request, DateTimeOffset nowUtc);
        /// <summary>Marks only unresolved jobs of ConfirmedLost owners unknown; preserves committed and terminal state/fences.</summary>
        int RecoverLostOwners(DateTimeOffset nowUtc);
    }
}
