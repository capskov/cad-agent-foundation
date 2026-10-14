using System.Text.Json;
using Bimwright.Dwg.Shared.Execution;

namespace Bimwright.Dwg.Plugin.Execution;

/// <summary>
/// Synchronous callbacks used by the host-independent queue. The verifier must
/// validate the submitted context and complete target exactly; it must not fall
/// back to whichever document happens to be active.
/// </summary>
public delegate bool PluginExactContextVerifier(string contextId, string completeTargetJson);

/// <summary>Explicit success or failure returned by a read-only tool handler.</summary>
public sealed class PluginReadOnlyJobResult
{
    private PluginReadOnlyJobResult(bool succeeded, string? resultJson, string? errorCode, string? errorMessage)
    {
        Succeeded = succeeded;
        ResultJson = resultJson;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }
    public string? ResultJson { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    /// <summary>Creates a success result; the queue validates JSON and preserves its bytes.</summary>
    public static PluginReadOnlyJobResult Success(string? resultJson) => new(true, resultJson, null, null);

    /// <summary>Creates an explicit tool failure with a stable code and message.</summary>
    public static PluginReadOnlyJobResult Failure(string errorCode, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new PluginReadOnlyJobResult(false, null, errorCode, errorMessage);
    }
}

/// <summary>Executes one allowlisted read-only job and returns an explicit outcome.</summary>
public delegate PluginReadOnlyJobResult? PluginReadOnlyJobHandler(PluginJobSnapshot job);

public enum PluginWriteJobOutcome
{
    RejectedBeforeTransaction,
    RolledBackBeforeCommit,
    Committed,
    OutcomeUnknown
}

/// <summary>
/// Restricted result contract for an explicitly allowlisted write handler.
/// There is no boolean success flag: ambiguous execution must be OutcomeUnknown.
/// A Committed result carries raw readback from a separate read-only transaction;
/// its verification state defaults to Unknown and never implies Passed.
/// </summary>
public sealed class PluginWriteJobResult
{
    private PluginWriteJobResult(PluginWriteJobOutcome outcome, string? resultJson,
        PluginVerificationState verification, string? readbackJson,
        string? errorCode, string? errorMessage)
    {
        Outcome = outcome;
        ResultJson = resultJson;
        Verification = verification;
        ReadbackJson = readbackJson;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public PluginWriteJobOutcome Outcome { get; }
    public string? ResultJson { get; }
    public PluginVerificationState Verification { get; }
    public string? ReadbackJson { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    public static PluginWriteJobResult RejectedBeforeTransaction(string errorCode, string errorMessage) =>
        NoEffect(PluginWriteJobOutcome.RejectedBeforeTransaction, errorCode, errorMessage);

    public static PluginWriteJobResult RolledBackBeforeCommit(string errorCode, string errorMessage) =>
        NoEffect(PluginWriteJobOutcome.RolledBackBeforeCommit, errorCode, errorMessage);

    public static PluginWriteJobResult Committed(string? resultJson,
        PluginVerificationState verification = PluginVerificationState.Unknown,
        string? readbackJson = null, string? verificationErrorCode = null,
        string? verificationErrorMessage = null) =>
        new(PluginWriteJobOutcome.Committed, resultJson, verification, readbackJson,
            verificationErrorCode, verificationErrorMessage);

    public static PluginWriteJobResult OutcomeUnknown(string errorCode, string errorMessage) =>
        new(PluginWriteJobOutcome.OutcomeUnknown, null, PluginVerificationState.Unknown,
            null, RequiredError(errorCode, nameof(errorCode)), RequiredError(errorMessage, nameof(errorMessage)));

    private static PluginWriteJobResult NoEffect(PluginWriteJobOutcome outcome, string errorCode, string errorMessage) =>
        new(outcome, null, PluginVerificationState.NotChecked, null,
            RequiredError(errorCode, nameof(errorCode)), RequiredError(errorMessage, nameof(errorMessage)));

    private static string RequiredError(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}

/// <summary>Executes one explicitly allowlisted write on the same host FIFO and pump thread.</summary>
public delegate PluginWriteJobResult? PluginWriteJobHandler(PluginJobSnapshot job);

public enum PluginExecutionQueuePumpDisposition
{
    NoWork,
    QueueDeadlineExpired,
    Completed,
    Failed,
    Halted
}

/// <summary>The durable outcome of one call to <see cref="PluginExecutionQueue.Pump"/>.</summary>
public sealed class PluginExecutionQueuePumpResult
{
    internal PluginExecutionQueuePumpResult(
        PluginExecutionQueuePumpDisposition disposition,
        PluginJobSnapshot? job = null,
        string? errorCode = null)
    {
        Disposition = disposition;
        Job = job;
        ErrorCode = errorCode;
    }

    public PluginExecutionQueuePumpDisposition Disposition { get; }
    public PluginJobSnapshot? Job { get; }
    public string? ErrorCode { get; }
}

/// <summary>
/// Claims and executes at most one durable job per pump call. The default
/// constructor remains read-only; a separate explicit constructor is required
/// to allow specific writes. The
/// constructing thread is treated as the host's permitted execution thread.
/// This class has no CAD or other host-specific references. Execution is
/// synchronous: a cancellation request that arrives after a handler starts does
/// not interrupt it, and its actual terminal result is recorded when it returns.
/// </summary>
public sealed class PluginExecutionQueue
{
    public const string WriteJobRejectedErrorCode = "read_only_queue_rejects_write";
    public const string ToolNotAllowedErrorCode = "tool_not_allowlisted";
    public const string ExactContextMismatchErrorCode = "exact_context_mismatch";
    public const string ExactContextVerificationFailedErrorCode = "exact_context_verification_failed";
    public const string HandlerFailedErrorCode = "read_only_handler_failed";
    public const string HandlerInvalidResultErrorCode = "read_only_handler_invalid_result";
    public const string WriteHandlerFailedErrorCode = "write_handler_outcome_unknown";
    public const string WriteHandlerInvalidResultErrorCode = "write_handler_outcome_unknown";
    public const string WriteNoEffectJournalUnconfirmedErrorCode = "write_no_effect_transition_unconfirmed";
    public const string WriteCommitJournalUnconfirmedErrorCode = "write_commit_transition_unconfirmed";
    public const string WriteVerificationJournalUnconfirmedErrorCode = "write_verification_stage_unconfirmed";
    public const string WriteOutcomeUnknownErrorCode = "write_outcome_unknown";
    public const string TerminalTransitionUnconfirmedErrorCode = "journal_terminal_transition_unconfirmed";
    public const string SubmissionUnconfirmedErrorCode = "journal_submission_unconfirmed";
    public const string QueueHaltedErrorCode = "plugin_execution_queue_halted";

    private const string WriteJobRejectedMessage = "Write jobs cannot run through the read-only queue.";
    private const string ToolNotAllowedMessage = "The tool is not in the configured read-only allowlist.";
    private const string ExactContextMismatchMessage = "The submitted context or complete target is no longer exact.";
    private const string ExactContextVerificationFailedMessage = "Exact context verification threw an exception.";
    private const string HandlerFailedMessage = "The read-only handler threw an exception.";
    private const string HandlerInvalidResultMessage = "The read-only handler did not return valid JSON.";

    private readonly object _admissionGate = new();
    private readonly IPluginIntentJournal _journal;
    private readonly HashSet<string> _allowedReadOnlyToolNames;
    private readonly HashSet<string> _allowedWriteToolNames;
    private readonly PluginExactContextVerifier _exactContextVerifier;
    private readonly PluginReadOnlyJobHandler _handler;
    private readonly PluginWriteJobHandler? _writeHandler;
    private readonly int _constructionThreadId;
    private int _pumpInProgress;
    private bool _halted;
    private string? _haltErrorCode;

    public PluginExecutionQueue(
        IPluginIntentJournal journal,
        IEnumerable<string> allowedReadOnlyToolNames,
        PluginExactContextVerifier exactContextVerifier,
        PluginReadOnlyJobHandler handler)
        : this(journal, allowedReadOnlyToolNames, Array.Empty<string>(), exactContextVerifier,
            handler, writeHandler: null, allowWrites: false)
    {
    }

    /// <summary>
    /// Explicit opt-in constructor. Writes still share this queue, exact-context
    /// verifier, host thread and durable journal with reads; no second queue is
    /// created. The allowlist must be non-empty.
    /// </summary>
    public PluginExecutionQueue(
        IPluginIntentJournal journal,
        IEnumerable<string> allowedReadOnlyToolNames,
        IEnumerable<string> allowedWriteToolNames,
        PluginExactContextVerifier exactContextVerifier,
        PluginReadOnlyJobHandler handler,
        PluginWriteJobHandler writeHandler)
        : this(journal, allowedReadOnlyToolNames, allowedWriteToolNames, exactContextVerifier,
            handler, writeHandler, allowWrites: true)
    {
    }

    private PluginExecutionQueue(
        IPluginIntentJournal journal,
        IEnumerable<string> allowedReadOnlyToolNames,
        IEnumerable<string> allowedWriteToolNames,
        PluginExactContextVerifier exactContextVerifier,
        PluginReadOnlyJobHandler handler,
        PluginWriteJobHandler? writeHandler,
        bool allowWrites)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(allowedReadOnlyToolNames);
        ArgumentNullException.ThrowIfNull(allowedWriteToolNames);
        ArgumentNullException.ThrowIfNull(exactContextVerifier);
        ArgumentNullException.ThrowIfNull(handler);
        if (allowWrites && writeHandler is null)
            throw new ArgumentNullException(nameof(writeHandler));

        _journal = journal;
        _allowedReadOnlyToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string toolName in allowedReadOnlyToolNames)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                throw new ArgumentException("Read-only tool names must be non-empty.", nameof(allowedReadOnlyToolNames));
            _allowedReadOnlyToolNames.Add(toolName);
        }

        _allowedWriteToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string toolName in allowedWriteToolNames)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                throw new ArgumentException("Write tool names must be non-empty.", nameof(allowedWriteToolNames));
            _allowedWriteToolNames.Add(toolName);
        }
        if (allowWrites && _allowedWriteToolNames.Count == 0)
            throw new ArgumentException("The opted-in write allowlist must contain at least one tool.", nameof(allowedWriteToolNames));

        _exactContextVerifier = exactContextVerifier;
        _handler = handler;
        _writeHandler = allowWrites ? writeHandler : null;
        _constructionThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>True after an uncertain admission, claim, or terminal transition.</summary>
    public bool IsHalted
    {
        get
        {
            lock (_admissionGate)
                return _halted;
        }
    }

    /// <summary>
    /// Durably admits a request only while the queue is healthy. The gate
    /// linearizes this check with journal submission and every transition to
    /// halted; callers may continue to use journal get/cancel operations.
    /// </summary>
    public bool TrySubmit(PluginJobSubmission request, DateTimeOffset nowUtc, out PluginSubmitResult? result)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_admissionGate)
        {
            if (_halted)
            {
                result = null;
                return false;
            }

            try
            {
                result = _journal.Submit(request, nowUtc);
                if (result is not null)
                    return true;

                HaltLocked(SubmissionUnconfirmedErrorCode);
                return false;
            }
            catch
            {
                // A submit acknowledgement can be lost after its transaction
                // commits, so do not risk admitting work against uncertain state.
                result = null;
                HaltLocked(SubmissionUnconfirmedErrorCode);
                return false;
            }
        }
    }

    /// <summary>
    /// Claims no more than one job. Calls from any thread other than the
    /// construction thread are rejected before the journal can be touched.
    /// Reentrant or concurrent calls are rejected without claiming another job.
    /// </summary>
    public PluginExecutionQueuePumpResult Pump(DateTimeOffset nowUtc)
    {
        if (Environment.CurrentManagedThreadId != _constructionThreadId)
            throw new InvalidOperationException("The queue must be pumped on its construction thread.");
        if (Interlocked.CompareExchange(ref _pumpInProgress, 1, 0) != 0)
            throw new InvalidOperationException("A queue pump is already in progress.");

        try
        {
            PluginJobSnapshot job;
            lock (_admissionGate)
            {
                if (_halted)
                    return HaltedLocked();

                PluginClaimResult claim;
                try
                {
                    claim = _journal.TryClaimNext(nowUtc);
                }
                catch
                {
                    // The claim commit may have succeeded even if its acknowledgement failed.
                    // No admission may pass this halt transition.
                    return HaltLocked(TerminalTransitionUnconfirmedErrorCode);
                }

                if (claim is null)
                    return HaltLocked(TerminalTransitionUnconfirmedErrorCode);

                if (claim.Disposition == PluginClaimDisposition.NoWork)
                    return new PluginExecutionQueuePumpResult(PluginExecutionQueuePumpDisposition.NoWork);

                if (claim.Disposition == PluginClaimDisposition.QueueDeadlineExpired)
                {
                    if (claim.Job is { Phase: PluginJobPhase.Cancelled, Effects: PluginEffectState.None,
                        ErrorCode: "queue_deadline_exceeded" } expired)
                        return new PluginExecutionQueuePumpResult(PluginExecutionQueuePumpDisposition.QueueDeadlineExpired, expired,
                            expired.ErrorCode);
                    return HaltLocked(TerminalTransitionUnconfirmedErrorCode, claim.Job);
                }

                if (claim.Disposition != PluginClaimDisposition.Claimed
                    || claim.Job is not { Phase: PluginJobPhase.Running } claimedJob)
                    return HaltLocked(TerminalTransitionUnconfirmedErrorCode, claim.Job);

                job = claimedJob;
            }

            if (job.IsWrite)
            {
                if (_writeHandler is null)
                    return RecordFailed(job, WriteJobRejectedErrorCode, WriteJobRejectedMessage, nowUtc);
                if (!_allowedWriteToolNames.Contains(job.ToolName))
                    return RecordNoEffect(job, PluginNoEffectOutcome.RejectedBeforeTransaction,
                        ToolNotAllowedErrorCode, ToolNotAllowedMessage, nowUtc);
            }

            if (!job.IsWrite && !_allowedReadOnlyToolNames.Contains(job.ToolName))
                return RecordFailed(job, ToolNotAllowedErrorCode, ToolNotAllowedMessage, nowUtc);

            bool exactContext;
            try
            {
                exactContext = _exactContextVerifier(job.ContextId, job.CompleteTargetJson);
            }
            catch
            {
                if (job.IsWrite && _writeHandler is not null)
                    return RecordNoEffect(job, PluginNoEffectOutcome.RejectedBeforeTransaction,
                        ExactContextVerificationFailedErrorCode, ExactContextVerificationFailedMessage, nowUtc);
                return RecordFailed(job, ExactContextVerificationFailedErrorCode,
                    ExactContextVerificationFailedMessage, nowUtc);
            }

            if (!exactContext)
            {
                if (job.IsWrite && _writeHandler is not null)
                    return RecordNoEffect(job, PluginNoEffectOutcome.RejectedBeforeTransaction,
                        ExactContextMismatchErrorCode, ExactContextMismatchMessage, nowUtc);
                return RecordFailed(job, ExactContextMismatchErrorCode, ExactContextMismatchMessage, nowUtc);
            }

            if (job.IsWrite)
                return ExecuteWrite(job, nowUtc);

            PluginReadOnlyJobResult? handlerResult;
            try
            {
                handlerResult = _handler(job);
                if (handlerResult is null)
                    return RecordFailed(job, HandlerInvalidResultErrorCode, HandlerInvalidResultMessage, nowUtc);
            }
            catch
            {
                return RecordFailed(job, HandlerFailedErrorCode, HandlerFailedMessage, nowUtc);
            }

            if (!handlerResult.Succeeded)
                return RecordFailed(job, handlerResult.ErrorCode!, handlerResult.ErrorMessage!, nowUtc);

            if (handlerResult.ResultJson is null)
                return RecordFailed(job, HandlerInvalidResultErrorCode, HandlerInvalidResultMessage, nowUtc);

            try
            {
                using JsonDocument _ = JsonDocument.Parse(handlerResult.ResultJson);
            }
            catch (JsonException)
            {
                return RecordFailed(job, HandlerInvalidResultErrorCode, HandlerInvalidResultMessage, nowUtc);
            }

            return RecordTerminal(job, PluginJobPhase.Completed, handlerResult.ResultJson, null, null, nowUtc);
        }
        finally
        {
            Volatile.Write(ref _pumpInProgress, 0);
        }
    }

    private PluginExecutionQueuePumpResult ExecuteWrite(PluginJobSnapshot job, DateTimeOffset nowUtc)
    {
        PluginWriteJobResult? handlerResult;
        try
        {
            handlerResult = _writeHandler!(job);
        }
        catch
        {
            return RecordUnknownWrite(job, WriteHandlerFailedErrorCode,
                "The write handler threw; the database effect may be unknown.", nowUtc);
        }

        if (handlerResult is null)
            return RecordUnknownWrite(job, WriteHandlerInvalidResultErrorCode,
                "The write handler returned no outcome.", nowUtc);

        switch (handlerResult.Outcome)
        {
            case PluginWriteJobOutcome.RejectedBeforeTransaction:
                return RecordNoEffect(job, PluginNoEffectOutcome.RejectedBeforeTransaction,
                    handlerResult.ErrorCode!, handlerResult.ErrorMessage!, nowUtc);
            case PluginWriteJobOutcome.RolledBackBeforeCommit:
                return RecordNoEffect(job, PluginNoEffectOutcome.RolledBackBeforeCommit,
                    handlerResult.ErrorCode!, handlerResult.ErrorMessage!, nowUtc);
            case PluginWriteJobOutcome.OutcomeUnknown:
                return RecordUnknownWrite(job, handlerResult.ErrorCode ?? WriteOutcomeUnknownErrorCode,
                    handlerResult.ErrorMessage ?? "The write handler reported an unknown outcome.", nowUtc);
            case PluginWriteJobOutcome.Committed:
                return RecordCommittedWrite(job, handlerResult, nowUtc);
            default:
                return RecordUnknownWrite(job, WriteHandlerInvalidResultErrorCode,
                    "The write handler returned an unsupported outcome.", nowUtc);
        }
    }

    private PluginExecutionQueuePumpResult RecordNoEffect(
        PluginJobSnapshot job,
        PluginNoEffectOutcome outcome,
        string errorCode,
        string errorMessage,
        DateTimeOffset nowUtc)
    {
        PluginTransitionResult transition;
        try
        {
            transition = _journal.RecordNoEffectTerminal(new PluginNoEffectTerminalRequest(
                job.RequestId, job.ContextId, job.CompleteTargetJson, job.OwnerId,
                job.ProviderGeneration, job.PayloadDigest, job.PluginSequence, outcome,
                errorCode, errorMessage), nowUtc);
        }
        catch
        {
            return HaltLocked(WriteNoEffectJournalUnconfirmedErrorCode, job);
        }

        bool confirmed = transition.Job is { } snapshot
            && SameWriteIdentity(job, snapshot)
            && snapshot.Phase == PluginJobPhase.Failed
            && snapshot.Effects == PluginEffectState.None
            && snapshot.WriteFenceReleased
            && snapshot.NoEffectClaimSequence == job.PluginSequence
            && snapshot.NoEffectOutcome == outcome;
        if (transition.Disposition is not (PluginTransitionDisposition.Applied or PluginTransitionDisposition.Duplicate)
            || !confirmed)
            return HaltLocked(WriteNoEffectJournalUnconfirmedErrorCode, transition.Job ?? job);

        return new PluginExecutionQueuePumpResult(PluginExecutionQueuePumpDisposition.Failed,
            transition.Job, errorCode);
    }

    private PluginExecutionQueuePumpResult RecordUnknownWrite(
        PluginJobSnapshot job, string errorCode, string errorMessage, DateTimeOffset nowUtc)
    {
        PluginTransitionResult transition;
        try
        {
            transition = _journal.RecordTerminal(job.RequestId, PluginJobPhase.Unknown,
                PluginEffectState.Unknown, null, errorCode, errorMessage, nowUtc);
        }
        catch
        {
            return HaltLocked(WriteOutcomeUnknownErrorCode, job);
        }

        bool confirmed = transition.Job is { } snapshot
            && SameWriteIdentity(job, snapshot)
            && snapshot.Phase == PluginJobPhase.Unknown
            && snapshot.Effects == PluginEffectState.Unknown
            && !snapshot.WriteFenceReleased;
        if (transition.Disposition is not (PluginTransitionDisposition.Applied or PluginTransitionDisposition.Duplicate)
            || !confirmed)
            return HaltLocked(WriteOutcomeUnknownErrorCode, transition.Job ?? job);

        return HaltLocked(errorCode, transition.Job);
    }

    private PluginExecutionQueuePumpResult RecordCommittedWrite(
        PluginJobSnapshot job, PluginWriteJobResult handlerResult, DateTimeOffset nowUtc)
    {
        if (!IsJson(handlerResult.ResultJson))
            return RecordUnknownWrite(job, WriteHandlerInvalidResultErrorCode,
                "The write handler did not provide a valid committed-result JSON handle.", nowUtc);

        PluginTransitionResult committed;
        try
        {
            committed = _journal.RecordCommitted(job.RequestId, handlerResult.ResultJson!, nowUtc);
        }
        catch
        {
            return HaltLocked(WriteCommitJournalUnconfirmedErrorCode, job);
        }
        if (committed.Disposition is not (PluginTransitionDisposition.Applied or PluginTransitionDisposition.Duplicate)
            || committed.Job is not { } committedJob
            || !SameWriteIdentity(job, committedJob)
            || committedJob.Effects != PluginEffectState.Committed
            || committedJob.ResultJson != handlerResult.ResultJson
            || committedJob.CommittedResultDigest is null
            || committedJob.WriteFenceReleased)
            return HaltLocked(WriteCommitJournalUnconfirmedErrorCode, committed.Job ?? job);

        PluginTransitionResult completed;
        try
        {
            completed = _journal.RecordTerminal(job.RequestId, PluginJobPhase.Completed,
                PluginEffectState.Committed, handlerResult.ResultJson, null, null, nowUtc);
        }
        catch
        {
            return HaltLocked(WriteCommitJournalUnconfirmedErrorCode, committedJob);
        }
        if (completed.Disposition is not (PluginTransitionDisposition.Applied or PluginTransitionDisposition.Duplicate)
            || completed.Job is not { } completedJob
            || !SameWriteIdentity(job, completedJob)
            || completedJob.Phase != PluginJobPhase.Completed
            || completedJob.Effects != PluginEffectState.Committed
            || completedJob.ResultJson != handlerResult.ResultJson
            || completedJob.CommittedResultDigest != committedJob.CommittedResultDigest
            || completedJob.WriteFenceReleased)
            return HaltLocked(WriteCommitJournalUnconfirmedErrorCode, completed.Job ?? committedJob);

        PluginVerificationState verification = handlerResult.Verification;
        string? readbackJson = handlerResult.ReadbackJson;
        string? verificationErrorCode = handlerResult.ErrorCode;
        string? verificationErrorMessage = handlerResult.ErrorMessage;
        if (!Enum.IsDefined(verification) || verification == PluginVerificationState.NotChecked)
        {
            verification = PluginVerificationState.Unknown;
            verificationErrorCode ??= "readback_state_invalid";
            verificationErrorMessage ??= "The write handler did not provide a trusted readback state.";
        }
        if (readbackJson is not null && !IsJson(readbackJson))
        {
            verification = PluginVerificationState.Unknown;
            readbackJson = null;
            verificationErrorCode ??= "readback_json_invalid";
            verificationErrorMessage ??= "The independent readback was not valid JSON.";
        }
        if (verification == PluginVerificationState.Passed && readbackJson is null)
        {
            verification = PluginVerificationState.Unknown;
            verificationErrorCode ??= "readback_missing";
            verificationErrorMessage ??= "Passed readback requires raw JSON evidence from a second read-only transaction.";
        }
        if (verification != PluginVerificationState.Passed && readbackJson is null
            && string.IsNullOrWhiteSpace(verificationErrorCode)
            && string.IsNullOrWhiteSpace(verificationErrorMessage))
        {
            verificationErrorCode = "readback_unknown";
            verificationErrorMessage = "The independent readback did not establish a result.";
        }

        long readbackRevision;
        try { readbackRevision = checked(completedJob.PluginSequence + 1); }
        catch { return HaltLocked(WriteVerificationJournalUnconfirmedErrorCode, completedJob); }

        var stagedRequest = new PluginVerificationRequest(
            completedJob.RequestId, completedJob.ContextId, completedJob.CompleteTargetJson,
            completedJob.ProviderGeneration, readbackRevision, verification, readbackJson,
            verificationErrorCode, verificationErrorMessage, completedJob.OwnerId,
            completedJob.PayloadDigest, completedJob.PluginSequence, completedJob.CommittedResultDigest);
        PluginTransitionResult staged;
        try
        {
            staged = _journal.RecordVerificationPendingRelease(stagedRequest, nowUtc);
        }
        catch
        {
            return HaltLocked(WriteVerificationJournalUnconfirmedErrorCode, completedJob);
        }

        bool stageConfirmed = staged.Disposition is PluginTransitionDisposition.Applied or PluginTransitionDisposition.Duplicate
            && staged.Job is { } stagedJob
            && SameWriteIdentity(completedJob, stagedJob)
            && stagedJob.Phase == PluginJobPhase.Completed
            && stagedJob.Effects == PluginEffectState.Committed
            && stagedJob.Verification == verification
            && stagedJob.ReadbackRevision == readbackRevision
            && stagedJob.VerificationStagedSequence == readbackRevision
            && stagedJob.PluginSequence == readbackRevision
            && stagedJob.ReadbackDigest is not null
            && stagedJob.CommittedResultDigest == completedJob.CommittedResultDigest
            && !stagedJob.WriteFenceReleased && !stagedJob.WriteFenceFinalized
            && SameFenceOwnership(completedJob, stagedJob);
        if (!stageConfirmed)
            return HaltLocked(WriteVerificationJournalUnconfirmedErrorCode, staged.Job ?? completedJob);

        string? verificationStatus = verification == PluginVerificationState.Passed
            ? null : staged.Job!.VerificationErrorCode ?? "write_readback_not_passed";
        return new PluginExecutionQueuePumpResult(PluginExecutionQueuePumpDisposition.Completed,
            staged.Job, verificationStatus);
    }

    private static bool SameWriteIdentity(PluginJobSnapshot expected, PluginJobSnapshot actual) =>
        expected.RequestId == actual.RequestId
        && expected.ContextId == actual.ContextId
        && expected.CompleteTargetJson == actual.CompleteTargetJson
        && expected.OwnerId == actual.OwnerId
        && expected.ProviderGeneration == actual.ProviderGeneration
        && expected.PayloadDigest == actual.PayloadDigest
        && expected.IsWrite && actual.IsWrite;

    private static bool SameFenceOwnership(PluginJobSnapshot expected, PluginJobSnapshot actual) =>
        expected.GlobalFence == actual.GlobalFence
        && expected.HeldRiskKeys.SequenceEqual(actual.HeldRiskKeys, StringComparer.Ordinal);

    private static bool IsJson(string? json)
    {
        if (json is null) return false;
        try
        {
            using JsonDocument _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private PluginExecutionQueuePumpResult RecordFailed(
        PluginJobSnapshot job,
        string errorCode,
        string errorMessage,
        DateTimeOffset nowUtc) => RecordTerminal(job, PluginJobPhase.Failed, null, errorCode, errorMessage, nowUtc);

    private PluginExecutionQueuePumpResult RecordTerminal(
        PluginJobSnapshot job,
        PluginJobPhase phase,
        string? resultJson,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset nowUtc)
    {
        lock (_admissionGate)
        {
            PluginTransitionResult transition;
            PluginEffectState terminalEffects = job.IsWrite ? job.Effects : PluginEffectState.None;
            try
            {
                transition = _journal.RecordTerminal(job.RequestId, phase, terminalEffects,
                    resultJson, errorCode, errorMessage, nowUtc);
            }
            catch
            {
                return HaltLocked(TerminalTransitionUnconfirmedErrorCode, job);
            }

            bool expectedTerminal = transition.Job is { } snapshot
                && snapshot.Phase == phase
                && snapshot.Effects == terminalEffects
                && string.Equals(snapshot.ResultJson, resultJson, StringComparison.Ordinal)
                && string.Equals(snapshot.ErrorCode, errorCode, StringComparison.Ordinal)
                && string.Equals(snapshot.ErrorMessage, errorMessage, StringComparison.Ordinal);
            if (transition.Disposition is not (PluginTransitionDisposition.Applied or PluginTransitionDisposition.Duplicate)
                || !expectedTerminal)
                return HaltLocked(TerminalTransitionUnconfirmedErrorCode, transition.Job ?? job);

            return new PluginExecutionQueuePumpResult(
                phase == PluginJobPhase.Completed
                    ? PluginExecutionQueuePumpDisposition.Completed
                    : PluginExecutionQueuePumpDisposition.Failed,
                transition.Job,
                errorCode);
        }
    }

    private PluginExecutionQueuePumpResult HaltLocked(
        string errorCode,
        PluginJobSnapshot? job = null)
    {
        _halted = true;
        _haltErrorCode ??= errorCode;
        return new PluginExecutionQueuePumpResult(PluginExecutionQueuePumpDisposition.Halted,
            job, errorCode);
    }

    private PluginExecutionQueuePumpResult HaltedLocked() => new(
        PluginExecutionQueuePumpDisposition.Halted,
        errorCode: _haltErrorCode ?? TerminalTransitionUnconfirmedErrorCode);
}
