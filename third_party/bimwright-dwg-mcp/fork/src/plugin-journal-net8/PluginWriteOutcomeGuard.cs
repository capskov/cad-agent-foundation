using Bimwright.Dwg.Shared.Execution;

namespace Bimwright.Dwg.Plugin.Execution;

/// <summary>A handler action deferred until the exact document context is locked.</summary>
public delegate PluginWriteJobResult? PluginWriteHandlerAction();

/// <summary>Invokes the handler while recording whether execution has begun.</summary>
public delegate PluginWriteJobResult? PluginWriteHandlerInvoker(PluginWriteHandlerAction handler);

/// <summary>Runs a handler invoker inside the host-specific document lock scope.</summary>
public delegate PluginWriteJobResult? PluginWriteDocumentLockScope(PluginWriteHandlerInvoker invokeHandler);

/// <summary>
/// Preserves the write outcome boundary around host-specific lock/context cleanup.
/// The wrapper has no AutoCAD dependency so its ambiguity rules can be tested offline.
/// </summary>
public static class PluginWriteOutcomeGuard
{
    public const string ContextUnavailableErrorCode = "d1_document_context_unavailable";
    public const string HandlerOutcomeUnknownErrorCode = "d1_write_outcome_unknown";
    public const string LockCleanupFailedErrorCode = "d1_document_lock_cleanup_failed";

    public static PluginWriteJobResult Execute(PluginWriteDocumentLockScope runInDocumentLock)
    {
        ArgumentNullException.ThrowIfNull(runInDocumentLock);

        bool handlerEntered = false;
        PluginWriteJobResult? handlerResult = null;

        try
        {
            PluginWriteJobResult? scopedResult = runInDocumentLock(handler =>
            {
                if (handlerEntered)
                    throw new InvalidOperationException("The document-lock scope invoked the write handler more than once.");
                if (handler is null)
                    throw new ArgumentNullException(nameof(handler));

                handlerEntered = true;
                handlerResult = handler();
                return handlerResult;
            });

            if (!handlerEntered)
                return RejectedBeforeHandler();

            if (handlerResult is null)
                return UnknownHandlerOutcome();

            if (!ReferenceEquals(scopedResult, handlerResult))
                return ScopeCleanupFailure(handlerResult);

            return handlerResult;
        }
        catch
        {
            if (!handlerEntered)
                return RejectedBeforeHandler();

            if (handlerResult is null)
                return UnknownHandlerOutcome();

            return ScopeCleanupFailure(handlerResult);
        }
    }

    private static PluginWriteJobResult RejectedBeforeHandler() =>
        PluginWriteJobResult.RejectedBeforeTransaction(
            ContextUnavailableErrorCode,
            "The bound document context could not be established before the write handler was invoked.");

    private static PluginWriteJobResult UnknownHandlerOutcome() =>
        PluginWriteJobResult.OutcomeUnknown(
            HandlerOutcomeUnknownErrorCode,
            "The write handler was invoked but threw or returned no outcome.");

    private static PluginWriteJobResult ScopeCleanupFailure(PluginWriteJobResult handlerResult)
    {
        if (handlerResult.Outcome == PluginWriteJobOutcome.Committed)
        {
            return PluginWriteJobResult.Committed(
                handlerResult.ResultJson,
                PluginVerificationState.Unknown,
                readbackJson: null,
                verificationErrorCode: LockCleanupFailedErrorCode,
                verificationErrorMessage: "The write committed, but document-lock cleanup failed; independent readback is not trusted.");
        }

        return PluginWriteJobResult.OutcomeUnknown(
            HandlerOutcomeUnknownErrorCode,
            "The document-lock scope failed after the write handler returned; the database outcome is unknown.");
    }
}
