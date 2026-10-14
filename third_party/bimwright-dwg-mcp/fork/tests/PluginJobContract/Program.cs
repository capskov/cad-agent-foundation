using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Dwg.Plugin;
using Bimwright.Dwg.Plugin.Execution;
using Bimwright.Dwg.Shared;
using Bimwright.Dwg.Shared.Execution;
using Microsoft.Data.Sqlite;

if (args.Length > 0 && (args[0] == "--lease-gc-holder" || args[0] == "--lease-contender"))
{
    Environment.ExitCode = ProcessLeaseProbe(args);
    return;
}

if (args.Length > 0 && args[0] == "--discovery-mutex-probe")
{
    Environment.ExitCode = ProcessDiscoveryMutexProbe(args);
    return;
}

int passed = 0;
DateTimeOffset Now = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
string root = Path.Combine(Path.GetTempPath(), "plugin-job-contract-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    Run("Acad26 target JSON survives journal canonicalization and exact-context queue verification", () =>
    {
        PluginOwnerIdentity owner = Owner();
        string contextId = Guid.NewGuid().ToString("N");
        string documentSessionId = Guid.NewGuid().ToString("N");
        string databaseSessionId = Guid.NewGuid().ToString("N");
        string fingerprint = "acad26-target-fingerprint";
        string ownerStartedAtUtc = owner.ProcessStartedAtUtc.ToUniversalTime()
            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        string target = Acad26DocumentTargetJson.Create(contextId, documentSessionId,
            databaseSessionId, fingerprint, drawingPath: null, owner.OwnerId, owner.ProcessId,
            ownerStartedAtUtc, owner.PluginGeneration);
        using var journal = Open(Db("acad26-target-canonicalization"), owner, new FakeLivenessProvider());

        PluginJobSubmission valid = new("acad26-target-valid", contextId, "list_layers", "{}",
            target, false, Array.Empty<string>(), false);
        Equal(PluginSubmitDisposition.Created, journal.Submit(valid, Now).Disposition);
        Equal(target, journal.GetSnapshot(valid.RequestId)!.CompleteTargetJson);

        int verified = 0;
        int handled = 0;
        var queue = new PluginExecutionQueue(journal, ["list_layers"], (submittedContext, submittedTarget) =>
        {
            verified++;
            Equal(contextId, submittedContext);
            Equal(target, submittedTarget);
            return string.Equals(contextId, submittedContext, StringComparison.Ordinal)
                && string.Equals(target, submittedTarget, StringComparison.Ordinal);
        }, _ =>
        {
            handled++;
            return PluginReadOnlyJobResult.Success("{\"layers\":[{\"name\":\"0\"}]}");
        });

        PluginExecutionQueuePumpResult accepted = queue.Pump(Now.AddSeconds(1));
        Equal(PluginExecutionQueuePumpDisposition.Completed, accepted.Disposition);
        Equal(PluginJobPhase.Completed, accepted.Job!.Phase);
        Equal(1, verified);
        Equal(1, handled);

        string changedTarget = Acad26DocumentTargetJson.Create(contextId, documentSessionId,
            databaseSessionId, fingerprint, drawingPath: null, owner.OwnerId, owner.ProcessId,
            ownerStartedAtUtc, owner.PluginGeneration + "-changed");
        PluginJobSubmission tampered = new("acad26-target-tampered", contextId, "list_layers", "{}",
            changedTarget, false, Array.Empty<string>(), false);
        Equal(PluginSubmitDisposition.Created, journal.Submit(tampered, Now.AddSeconds(2)).Disposition);
        var strictQueue = new PluginExecutionQueue(journal, ["list_layers"],
            (submittedContext, submittedTarget) =>
                string.Equals(contextId, submittedContext, StringComparison.Ordinal)
                && string.Equals(target, submittedTarget, StringComparison.Ordinal),
            _ =>
            {
                handled++;
                return PluginReadOnlyJobResult.Success("{\"layers\":[]}");
            });

        PluginExecutionQueuePumpResult rejected = strictQueue.Pump(Now.AddSeconds(3));
        Equal(PluginExecutionQueuePumpDisposition.Failed, rejected.Disposition);
        Equal(PluginExecutionQueue.ExactContextMismatchErrorCode, rejected.Job!.ErrorCode);
        Equal(1, handled);
    });

    Run("explicit entity-property reads execute through the read-only queue allowlist", () =>
    {
        using var journal = Open(Db("entity-properties-read"), Owner(), new FakeLivenessProvider());
        const string parameters = "{\"handles\":[\"1A\"],\"include_geometry\":false}";
        PluginJobSubmission submission = new("entity-properties-read-request", "entity-properties-context",
            "get_entity_properties", parameters, "{\"context_id\":\"entity-properties-context\"}",
            false, Array.Empty<string>(), false);
        Equal(PluginSubmitDisposition.Created, journal.Submit(submission, Now).Disposition);

        int handled = 0;
        var queue = new PluginExecutionQueue(journal,
            ["list_layers", "get_entity_properties"],
            (contextId, _) => contextId == submission.ContextId,
            job =>
            {
                handled++;
                Equal("get_entity_properties", job.ToolName);
                Equal(parameters, job.CanonicalParametersJson);
                Equal(false, job.IsWrite);
                return PluginReadOnlyJobResult.Success("{\"entities\":[]}");
            });

        PluginExecutionQueuePumpResult result = queue.Pump(Now.AddSeconds(1));
        Equal(PluginExecutionQueuePumpDisposition.Completed, result.Disposition);
        Equal(PluginJobPhase.Completed, result.Job!.Phase);
        Equal(PluginEffectState.None, result.Job.Effects);
        Equal("{\"entities\":[]}", result.Job.ResultJson);
        Equal(1, handled);
    });

    Run("filtered entity queries execute through the existing read-only durable queue", () =>
    {
        using var journal = Open(Db("query-entities-read"), Owner(), new FakeLivenessProvider());
        const string parameters = "{\"entity_type\":\"HATCH\",\"include_geometry\":false,\"limit\":5000}";
        PluginJobSubmission submission = new("query-entities-read-request", "query-entities-context",
            "query_entities", parameters, "{\"context_id\":\"query-entities-context\"}",
            false, Array.Empty<string>(), false);
        Equal(PluginSubmitDisposition.Created, journal.Submit(submission, Now).Disposition);

        int handled = 0;
        var queue = new PluginExecutionQueue(journal,
            ["list_layers", "get_entity_properties", "query_entities"],
            (contextId, _) => contextId == submission.ContextId,
            job =>
            {
                handled++;
                Equal("query_entities", job.ToolName);
                Equal(parameters, job.CanonicalParametersJson);
                Equal(false, job.IsWrite);
                return PluginReadOnlyJobResult.Success("{\"count\":0,\"has_more\":false,\"truncated\":false,\"entities\":[]}");
            });

        PluginExecutionQueuePumpResult result = queue.Pump(Now.AddSeconds(1));
        Equal(PluginExecutionQueuePumpDisposition.Completed, result.Disposition);
        Equal(PluginJobPhase.Completed, result.Job!.Phase);
        Equal(PluginEffectState.None, result.Job.Effects);
        Equal("{\"count\":0,\"has_more\":false,\"truncated\":false,\"entities\":[]}", result.Job.ResultJson);
        Equal(1, handled);
    });

    Run("D1 preflight executes as a read-only queued job with no write fence or staged verification", () =>
    {
        using var journal = Open(Db("d1-preflight-readonly-queue"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission submission = new("d1-preflight-readonly-queue", "d1-preflight-context",
            "d1_preflight_r01", "{}", "{\"context_id\":\"d1-preflight-context\"}",
            false, Array.Empty<string>(), false);
        Equal(PluginSubmitDisposition.Created, journal.Submit(submission, Now).Disposition);

        int handled = 0;
        var queue = new PluginExecutionQueue(journal,
            ["list_layers", "get_entity_properties", "query_entities", "d1_preflight_r01"],
            (contextId, targetJson) => contextId == submission.ContextId
                && targetJson == submission.CompleteTargetJson,
            job =>
            {
                handled++;
                Equal("d1_preflight_r01", job.ToolName);
                Equal(false, job.IsWrite);
                Equal("{}", job.CanonicalParametersJson);
                return PluginReadOnlyJobResult.Success("{\"read_only\":true}");
            });

        PluginExecutionQueuePumpResult result = queue.Pump(Now.AddSeconds(1));
        Equal(PluginExecutionQueuePumpDisposition.Completed, result.Disposition);
        Equal(PluginJobPhase.Completed, result.Job!.Phase);
        Equal(PluginEffectState.None, result.Job.Effects);
        Equal(false, result.Job.IsWrite);
        Equal(false, result.Job.GlobalFence);
        Equal(0, result.Job.HeldRiskKeys.Count);
        Equal(false, result.Job.WriteFenceReleased);
        Equal(null, result.Job.ReadbackRevision);
        Equal(null, result.Job.VerificationStagedSequence);
        Equal("{\"read_only\":true}", result.Job.ResultJson);
        Equal(1, handled);
    });

    Run("host-independent queue completes one read-only item per pump and persists exact result JSON", () =>
    {
        using var journal = Open(Db("queue-success"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission first = QueueRead("queue-success-1");
        PluginJobSubmission second = QueueRead("queue-success-2");
        Equal(PluginSubmitDisposition.Created, journal.Submit(first, Now).Disposition);
        Equal(PluginSubmitDisposition.Created, journal.Submit(second, Now).Disposition);
        int verified = 0;
        int handled = 0;
        const string resultJson = " { \"ok\" : true, \"items\" : [1,2] } ";
        var queue = new PluginExecutionQueue(journal, ["drawing_read"], (contextId, targetJson) =>
        {
            verified++;
            Equal("ctx", contextId);
            Equal(first.CompleteTargetJson, targetJson);
            return true;
        }, _ =>
        {
            handled++;
            return PluginReadOnlyJobResult.Success(resultJson);
        });

        PluginExecutionQueuePumpResult firstPump = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Completed, firstPump.Disposition);
        Equal("queue-success-1", firstPump.Job!.RequestId);
        Equal(PluginJobPhase.Completed, firstPump.Job.Phase);
        Equal(PluginEffectState.None, firstPump.Job.Effects);
        Equal(resultJson, firstPump.Job.ResultJson);
        PluginExecutionQueuePumpResult secondPump = queue.Pump(Now.AddSeconds(1));
        Equal(PluginExecutionQueuePumpDisposition.Completed, secondPump.Disposition);
        Equal("queue-success-2", secondPump.Job!.RequestId);
        Equal(PluginExecutionQueuePumpDisposition.NoWork, queue.Pump(Now.AddSeconds(2)).Disposition);
        Equal(2, verified);
        Equal(2, handled);

        Equal(PluginJobPhase.Completed, journal.GetSnapshot("queue-success-1")!.Phase);
        Equal(PluginEffectState.None, journal.GetSnapshot("queue-success-1")!.Effects);
        Equal(resultJson, journal.GetSnapshot("queue-success-1")!.ResultJson);
    });

    Run("host-independent queue fails stale exact context before handler and persists terminal state", () =>
    {
        using var journal = Open(Db("queue-stale-context"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = QueueRead("queue-stale-context");
        journal.Submit(request, Now);
        int handled = 0;
        var queue = new PluginExecutionQueue(journal, ["drawing_read"], (contextId, targetJson) =>
        {
            Equal(request.ContextId, contextId);
            Equal(request.CompleteTargetJson, targetJson);
            return false;
        }, _ =>
        {
            handled++;
            return PluginReadOnlyJobResult.Success("{}");
        });

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Failed, result.Disposition);
        Equal(0, handled);
        Equal(PluginJobPhase.Failed, result.Job!.Phase);
        Equal(PluginEffectState.None, result.Job.Effects);
        Equal(PluginExecutionQueue.ExactContextMismatchErrorCode, result.Job.ErrorCode);
        Equal(PluginJobPhase.Failed, journal.GetSnapshot(request.RequestId)!.Phase);
    });

    Run("host-independent queue persists a stable failure when exact-context verification throws", () =>
    {
        using var journal = Open(Db("queue-context-verifier-throws"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = QueueRead("queue-context-verifier-throws");
        journal.Submit(request, Now);
        int handled = 0;
        var queue = new PluginExecutionQueue(journal, ["drawing_read"], (_, _) =>
            throw new InvalidOperationException("simulated verifier failure"), _ =>
        {
            handled++;
            return PluginReadOnlyJobResult.Success("{}");
        });

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Failed, result.Disposition);
        Equal(0, handled);
        Equal(PluginJobPhase.Failed, result.Job!.Phase);
        Equal(PluginEffectState.None, result.Job.Effects);
        Equal(PluginExecutionQueue.ExactContextVerificationFailedErrorCode, result.Job.ErrorCode);
        Equal(PluginJobPhase.Failed, journal.GetSnapshot(request.RequestId)!.Phase);
    });

    Run("host-independent queue rejects write jobs and unlisted tools before invoking callbacks", () =>
    {
        using (var journal = Open(Db("queue-write-rejected"), Owner(), new FakeLivenessProvider()))
        {
            PluginJobSubmission write = Write("queue-write-rejected");
            journal.Submit(write, Now);
            int verified = 0;
            int handled = 0;
            var queue = new PluginExecutionQueue(journal, ["drawing_write"], (_, _) =>
            {
                verified++;
                return true;
            }, _ =>
            {
                handled++;
                return PluginReadOnlyJobResult.Success("{}");
            });

            PluginExecutionQueuePumpResult result = queue.Pump(Now);
            Equal(PluginExecutionQueuePumpDisposition.Failed, result.Disposition);
            Equal(0, verified);
            Equal(0, handled);
            Equal(PluginEffectState.Unknown, result.Job!.Effects);
            Equal(PluginExecutionQueue.WriteJobRejectedErrorCode, result.Job.ErrorCode);
            True(!result.Job.WriteFenceReleased);
            Contains("PATH:C:\\ROAD\\A.DWG", result.Job.HeldRiskKeys);
            Equal(PluginJobPhase.Failed, journal.GetSnapshot(write.RequestId)!.Phase);
        }

        using (var journal = Open(Db("queue-tool-rejected"), Owner(), new FakeLivenessProvider()))
        {
            PluginJobSubmission disallowed = QueueRead("queue-tool-rejected", "drawing_unlisted");
            journal.Submit(disallowed, Now);
            int verified = 0;
            int handled = 0;
            var queue = new PluginExecutionQueue(journal, ["drawing_read"], (_, _) =>
            {
                verified++;
                return true;
            }, _ =>
            {
                handled++;
                return PluginReadOnlyJobResult.Success("{}");
            });

            PluginExecutionQueuePumpResult result = queue.Pump(Now);
            Equal(PluginExecutionQueuePumpDisposition.Failed, result.Disposition);
            Equal(0, verified);
            Equal(0, handled);
            Equal(PluginExecutionQueue.ToolNotAllowedErrorCode, result.Job!.ErrorCode);
            Equal(PluginJobPhase.Failed, journal.GetSnapshot(disallowed.RequestId)!.Phase);
        }
    });

    Run("host-independent queue respects queue deadlines without invoking the handler", () =>
    {
        using var journal = Open(Db("queue-deadline"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = QueueRead("queue-deadline", deadline: Now.AddTicks(-1));
        journal.Submit(request, Now.AddSeconds(-1));
        int handled = 0;
        var queue = new PluginExecutionQueue(journal, ["drawing_read"], (_, _) => true, _ =>
        {
            handled++;
            return PluginReadOnlyJobResult.Success("{}");
        });

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.QueueDeadlineExpired, result.Disposition);
        Equal(0, handled);
        Equal(PluginJobPhase.Cancelled, result.Job!.Phase);
        Equal(PluginEffectState.None, result.Job.Effects);
        Equal("queue_deadline_exceeded", result.Job.ErrorCode);
        Equal(PluginJobPhase.Cancelled, journal.GetSnapshot(request.RequestId)!.Phase);
    });

    Run("host-independent queue rejects reentrant and concurrent pumps without taking another job", () =>
    {
        using var journal = Open(Db("queue-reentrant"), Owner(), new FakeLivenessProvider());
        journal.Submit(QueueRead("queue-reentrant-1"), Now);
        journal.Submit(QueueRead("queue-reentrant-2"), Now);
        PluginExecutionQueue? queue = null;
        int calls = 0;
        queue = new PluginExecutionQueue(journal, ["drawing_read"], (_, _) => true, _ =>
        {
            calls++;
            if (calls == 1)
            {
                Throws<InvalidOperationException>(() => queue!.Pump(Now));
                Task<PluginExecutionQueuePumpResult> concurrent = Task.Run(() => queue!.Pump(Now));
                Throws<InvalidOperationException>(() => concurrent.GetAwaiter().GetResult());
            }
            return PluginReadOnlyJobResult.Success("{\"ok\":true}");
        });

        Equal(PluginExecutionQueuePumpDisposition.Completed, queue.Pump(Now).Disposition);
        Equal(PluginJobPhase.Queued, journal.GetSnapshot("queue-reentrant-2")!.Phase);
        Equal(PluginExecutionQueuePumpDisposition.Completed, queue.Pump(Now.AddSeconds(1)).Disposition);
        Equal(PluginExecutionQueuePumpDisposition.NoWork, queue.Pump(Now.AddSeconds(2)).Disposition);
        Equal(2, calls);
    });

    Run("host-independent queue rejects wrong-thread pumps before claiming work", () =>
    {
        using var journal = Open(Db("queue-wrong-thread"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = QueueRead("queue-wrong-thread");
        journal.Submit(request, Now);
        var queue = new PluginExecutionQueue(journal, ["drawing_read"], (_, _) => true,
            _ => PluginReadOnlyJobResult.Success("{}"));

        Task<PluginExecutionQueuePumpResult> wrongThread = Task.Run(() => queue.Pump(Now));
        Throws<InvalidOperationException>(() => wrongThread.GetAwaiter().GetResult());
        Equal(PluginJobPhase.Queued, journal.GetSnapshot(request.RequestId)!.Phase);
    });

    Run("host-independent queue catches handler exceptions and persists a failed terminal snapshot", () =>
    {
        using var journal = Open(Db("queue-handler-exception"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = QueueRead("queue-handler-exception");
        journal.Submit(request, Now);
        var queue = new PluginExecutionQueue(journal, ["drawing_read"], (_, _) => true,
            _ => throw new InvalidOperationException("simulated handler failure"));

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Failed, result.Disposition);
        Equal(PluginJobPhase.Failed, result.Job!.Phase);
        Equal(PluginEffectState.None, result.Job.Effects);
        Equal(PluginExecutionQueue.HandlerFailedErrorCode, result.Job.ErrorCode);
        Equal(PluginJobPhase.Failed, journal.GetSnapshot(request.RequestId)!.Phase);
    });

    Run("host-independent queue durably records explicit handler failures", () =>
    {
        using var journal = Open(Db("queue-explicit-handler-failure"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = QueueRead("queue-explicit-handler-failure");
        journal.Submit(request, Now);
        var queue = new PluginExecutionQueue(journal, ["drawing_read"], (_, _) => true,
            _ => PluginReadOnlyJobResult.Failure("drawing_unavailable", "The requested drawing is unavailable."));

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Failed, result.Disposition);
        Equal(PluginJobPhase.Failed, result.Job!.Phase);
        Equal(PluginEffectState.None, result.Job.Effects);
        Equal("drawing_unavailable", result.Job.ErrorCode);
        Equal("The requested drawing is unavailable.", result.Job.ErrorMessage);
        Equal(PluginJobPhase.Failed, journal.GetSnapshot(request.RequestId)!.Phase);
        Equal("drawing_unavailable", journal.GetSnapshot(request.RequestId)!.ErrorCode);
    });

    Run("host-independent queue terminalizes null results and invalid success JSON", () =>
    {
        using var journal = Open(Db("queue-invalid-handler-result"), Owner(), new FakeLivenessProvider());
        journal.Submit(QueueRead("queue-invalid-handler-result-1"), Now);
        journal.Submit(QueueRead("queue-invalid-handler-result-2"), Now);
        journal.Submit(QueueRead("queue-invalid-handler-result-3"), Now);
        int handled = 0;
        var queue = new PluginExecutionQueue(journal, ["drawing_read"], (_, _) => true, _ =>
        {
            handled++;
            return handled switch
            {
                1 => null,
                2 => PluginReadOnlyJobResult.Success(null),
                _ => PluginReadOnlyJobResult.Success("not-json")
            };
        });

        PluginExecutionQueuePumpResult nullResult = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Failed, nullResult.Disposition);
        Equal(PluginExecutionQueue.HandlerInvalidResultErrorCode, nullResult.Job!.ErrorCode);
        Equal(PluginJobPhase.Failed, nullResult.Job.Phase);
        Equal(PluginEffectState.None, nullResult.Job.Effects);
        Equal<string?>(null, nullResult.Job.ResultJson);

        PluginExecutionQueuePumpResult nullJson = queue.Pump(Now.AddSeconds(1));
        Equal(PluginExecutionQueuePumpDisposition.Failed, nullJson.Disposition);
        Equal(PluginExecutionQueue.HandlerInvalidResultErrorCode, nullJson.Job!.ErrorCode);
        Equal(PluginJobPhase.Failed, nullJson.Job.Phase);
        Equal(PluginEffectState.None, nullJson.Job.Effects);
        Equal<string?>(null, nullJson.Job.ResultJson);

        PluginExecutionQueuePumpResult invalidJson = queue.Pump(Now.AddSeconds(2));
        Equal(PluginExecutionQueuePumpDisposition.Failed, invalidJson.Disposition);
        Equal(PluginExecutionQueue.HandlerInvalidResultErrorCode, invalidJson.Job!.ErrorCode);
        Equal(PluginJobPhase.Failed, invalidJson.Job.Phase);
        Equal(PluginEffectState.None, invalidJson.Job.Effects);
        Equal<string?>(null, invalidJson.Job.ResultJson);
        Equal(PluginJobPhase.Failed, journal.GetSnapshot("queue-invalid-handler-result-1")!.Phase);
        Equal(PluginJobPhase.Failed, journal.GetSnapshot("queue-invalid-handler-result-2")!.Phase);
        Equal(PluginJobPhase.Failed, journal.GetSnapshot("queue-invalid-handler-result-3")!.Phase);
        Equal(3, handled);
    });

    Run("host-independent queue records the actual result when cancellation arrives during synchronous handling", () =>
    {
        using var journal = Open(Db("queue-cancel-during-handler"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = QueueRead("queue-cancel-during-handler");
        journal.Submit(request, Now);
        var queue = new PluginExecutionQueue(journal, ["drawing_read"], (_, _) => true, job =>
        {
            PluginCancelResult cancelled = journal.CancelQueued(job.RequestId, Now.AddTicks(1));
            Equal(PluginCancelDisposition.CancellationRequested, cancelled.Disposition);
            return PluginReadOnlyJobResult.Success("{\"read\":\"finished\"}");
        });

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Completed, result.Disposition);
        Equal(PluginJobPhase.Completed, result.Job!.Phase);
        Equal("{\"read\":\"finished\"}", result.Job.ResultJson);
        Equal(PluginJobPhase.Completed, journal.GetSnapshot(request.RequestId)!.Phase);
    });

    Run("host-independent queue halts after an unconfirmed terminal journal transition", () =>
    {
        using var durableJournal = Open(Db("queue-terminal-unconfirmed"), Owner(), new FakeLivenessProvider());
        durableJournal.Submit(QueueRead("queue-terminal-unconfirmed-1"), Now);
        durableJournal.Submit(QueueRead("queue-terminal-unconfirmed-2"), Now);
        var rejectingJournal = new RejectTerminalJournal(durableJournal);
        var queue = new PluginExecutionQueue(rejectingJournal, ["drawing_read"], (_, _) => true,
            _ => PluginReadOnlyJobResult.Success("{}"));

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Halted, result.Disposition);
        Equal(PluginExecutionQueue.TerminalTransitionUnconfirmedErrorCode, result.ErrorCode);
        True(queue.IsHalted);
        Equal(PluginExecutionQueuePumpDisposition.Halted, queue.Pump(Now.AddSeconds(1)).Disposition);
        Equal(1, rejectingJournal.ClaimCount);
        Equal(PluginJobPhase.Running, durableJournal.GetSnapshot("queue-terminal-unconfirmed-1")!.Phase);
        Equal(PluginJobPhase.Queued, durableJournal.GetSnapshot("queue-terminal-unconfirmed-2")!.Phase);
    });

    Run("host-independent queue halts after an uncertain claim acknowledgement without retrying or handling", () =>
    {
        using var durableJournal = Open(Db("queue-claim-unconfirmed"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = QueueRead("queue-claim-unconfirmed");
        durableJournal.Submit(request, Now);
        var throwingJournal = new ThrowingClaimJournal(durableJournal);
        int handled = 0;
        var queue = new PluginExecutionQueue(throwingJournal, ["drawing_read"], (_, _) => true, _ =>
        {
            handled++;
            return PluginReadOnlyJobResult.Success("{}");
        });

        Equal(PluginExecutionQueuePumpDisposition.Halted, queue.Pump(Now).Disposition);
        Equal(PluginExecutionQueuePumpDisposition.Halted, queue.Pump(Now.AddSeconds(1)).Disposition);
        Equal(1, throwingJournal.ClaimCount);
        Equal(0, handled);
        Equal(PluginJobPhase.Running, durableJournal.GetSnapshot(request.RequestId)!.Phase);
    });

    Run("host-independent queue does not retry after terminal data is durable but its acknowledgement throws", () =>
    {
        using var durableJournal = Open(Db("queue-terminal-ack-throws-after-commit"), Owner(), new FakeLivenessProvider());
        durableJournal.Submit(QueueRead("queue-terminal-ack-throws-after-commit-1"), Now);
        durableJournal.Submit(QueueRead("queue-terminal-ack-throws-after-commit-2"), Now);
        var throwingJournal = new ThrowAfterTerminalJournal(durableJournal);
        int handled = 0;
        var queue = new PluginExecutionQueue(throwingJournal, ["drawing_read"], (_, _) => true, _ =>
        {
            handled++;
            return PluginReadOnlyJobResult.Success("{\"ok\":true}");
        });

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Halted, result.Disposition);
        Equal(PluginExecutionQueue.TerminalTransitionUnconfirmedErrorCode, result.ErrorCode);
        Equal(PluginExecutionQueuePumpDisposition.Halted, queue.Pump(Now.AddSeconds(1)).Disposition);
        Equal(1, throwingJournal.ClaimCount);
        Equal(1, throwingJournal.TerminalWriteCount);
        Equal(1, handled);
        Equal(PluginJobPhase.Completed, durableJournal.GetSnapshot("queue-terminal-ack-throws-after-commit-1")!.Phase);
        Equal("{\"ok\":true}", durableJournal.GetSnapshot("queue-terminal-ack-throws-after-commit-1")!.ResultJson);
        Equal(PluginJobPhase.Queued, durableJournal.GetSnapshot("queue-terminal-ack-throws-after-commit-2")!.Phase);
    });

    Run("queue admission before a terminal uncertainty remains durable and admission after halt is rejected", () =>
    {
        using var durableJournal = Open(Db("queue-admission-order"), Owner(), new FakeLivenessProvider());
        var throwingJournal = new ThrowAfterTerminalJournal(durableJournal);
        PluginJobSubmission first = QueueRead("queue-admission-order-first");
        PluginJobSubmission admittedDuringHandler = QueueRead("queue-admission-order-during-handler");
        PluginJobSubmission afterHalt = QueueRead("queue-admission-order-after-halt");
        PluginExecutionQueue? queue = null;
        bool wasAdmittedBeforeHalt = false;
        queue = new PluginExecutionQueue(throwingJournal, ["drawing_read"], (_, _) => true, _ =>
        {
            Task<(bool Accepted, PluginSubmitResult? Result)> submit = Task.Run(() =>
            {
                bool accepted = queue!.TrySubmit(admittedDuringHandler, Now.AddTicks(1), out PluginSubmitResult? result);
                return (accepted, result);
            });
            if (!submit.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Admission remained locked while the handler was running.");

            (wasAdmittedBeforeHalt, PluginSubmitResult? submitResult) = submit.Result;
            Equal(PluginSubmitDisposition.Created, submitResult?.Disposition);
            return PluginReadOnlyJobResult.Success("{}");
        });

        True(queue.TrySubmit(first, Now, out PluginSubmitResult? firstResult));
        Equal(PluginSubmitDisposition.Created, firstResult?.Disposition);
        PluginExecutionQueuePumpResult pump = queue.Pump(Now.AddTicks(2));
        Equal(PluginExecutionQueuePumpDisposition.Halted, pump.Disposition);
        True(queue.IsHalted);
        True(wasAdmittedBeforeHalt);
        Equal(PluginJobPhase.Completed, durableJournal.GetSnapshot(first.RequestId)!.Phase);
        Equal(PluginJobPhase.Queued, throwingJournal.GetSnapshot(admittedDuringHandler.RequestId)!.Phase);

        True(!queue.TrySubmit(afterHalt, Now.AddTicks(3), out PluginSubmitResult? rejectedResult));
        Equal<PluginSubmitResult?>(null, rejectedResult);
        Equal<PluginJobSnapshot?>(null, throwingJournal.GetSnapshot(afterHalt.RequestId));
        Equal(PluginCancelDisposition.Cancelled,
            throwingJournal.CancelQueued(admittedDuringHandler.RequestId, Now.AddTicks(4)).Disposition);
        Equal(PluginJobPhase.Cancelled, throwingJournal.GetSnapshot(admittedDuringHandler.RequestId)!.Phase);
    });

    Run("claim uncertainty halts future submissions while journal get and cancel remain available", () =>
    {
        using var durableJournal = Open(Db("queue-claim-halt-admission"), Owner(), new FakeLivenessProvider());
        var throwingJournal = new ThrowingClaimJournal(durableJournal);
        var queue = new PluginExecutionQueue(throwingJournal, ["drawing_read"], (_, _) => true,
            _ => PluginReadOnlyJobResult.Success("{}"));
        PluginJobSubmission claimed = QueueRead("queue-claim-halt-admission-running");
        PluginJobSubmission pending = QueueRead("queue-claim-halt-admission-pending");
        True(queue.TrySubmit(claimed, Now, out _));
        True(queue.TrySubmit(pending, Now.AddTicks(1), out _));

        Equal(PluginExecutionQueuePumpDisposition.Halted, queue.Pump(Now.AddTicks(2)).Disposition);
        True(queue.IsHalted);
        Equal(PluginJobPhase.Running, throwingJournal.GetSnapshot(claimed.RequestId)!.Phase);
        Equal(PluginCancelDisposition.Cancelled,
            throwingJournal.CancelQueued(pending.RequestId, Now.AddTicks(3)).Disposition);

        True(!queue.TrySubmit(QueueRead("queue-claim-halt-admission-rejected"), Now.AddTicks(4), out _));
        Equal(PluginJobPhase.Cancelled, throwingJournal.GetSnapshot(pending.RequestId)!.Phase);
    });

    Run("pipe listener lifecycle prevents stale accept publication and interrupts a published wait on Stop", () =>
    {
        var lifecycle = new PipeListenerLifecycle<FakeAcceptResource>();
        long stoppedGeneration = lifecycle.Start();
        lifecycle.Stop(_ => { });

        int created = 0;
        FakeAcceptResource? beforeCreate = lifecycle.CreateAndPublish(stoppedGeneration, () =>
        {
            created++;
            return new FakeAcceptResource("same-name");
        }, _ => { });
        Equal<FakeAcceptResource?>(null, beforeCreate);
        Equal(0, created);

        long firstGeneration = lifecycle.Start();
        FakeAcceptResource active = lifecycle.CreateAndPublish(firstGeneration, () =>
        {
            created++;
            return new FakeAcceptResource("same-name");
        }, _ => { })!;
        Equal(1, created);
        Task<bool> waitingAccept = Task.Run(() =>
        {
            try
            {
                active.WaitForConnection();
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        });
        True(active.WaitStarted.Wait(TimeSpan.FromSeconds(5)));
        lifecycle.Stop(_ => { });
        True(waitingAccept.Wait(TimeSpan.FromSeconds(5)));
        True(waitingAccept.Result);
        True(active.IsDisposed);

        long restartedGeneration = lifecycle.Start();
        FakeAcceptResource restarted = lifecycle.CreateAndPublish(restartedGeneration, () =>
        {
            created++;
            return new FakeAcceptResource("same-name");
        }, _ => { })!;
        FakeAcceptResource? stale = lifecycle.CreateAndPublish(firstGeneration, () =>
        {
            created++;
            return new FakeAcceptResource("stale");
        }, _ => { });
        Equal<FakeAcceptResource?>(null, stale);
        Equal(2, created);
        True(ReferenceEquals(restarted, lifecycle.Active));
        True(!restarted.IsDisposed);
        lifecycle.Stop(_ => { });
        True(restarted.IsDisposed);
    });

    Run("pipe listener Stop waits for an in-progress resource creation and disposes its publication", () =>
    {
        var lifecycle = new PipeListenerLifecycle<FakeAcceptResource>();
        long generation = lifecycle.Start();
        using var creationEntered = new ManualResetEventSlim();
        using var finishCreation = new ManualResetEventSlim();
        FakeAcceptResource? created = null;
        Task<FakeAcceptResource?> create = Task.Run(() => lifecycle.CreateAndPublish(generation, () =>
        {
            creationEntered.Set();
            if (!finishCreation.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Test did not release resource creation.");
            created = new FakeAcceptResource("creation-race");
            return created;
        }, _ => { }));

        True(creationEntered.Wait(TimeSpan.FromSeconds(5)));
        Task stop = Task.Run(() => lifecycle.Stop(_ => { }));
        finishCreation.Set();
        True(create.Wait(TimeSpan.FromSeconds(5)));
        True(stop.Wait(TimeSpan.FromSeconds(5)));
        True(created is not null);
        True(created!.IsDisposed);
        True(!lifecycle.IsRunning);
        Equal<FakeAcceptResource?>(null, lifecycle.Active);
    });

    Run("late pipe Start cleanup cannot stop a restarted generation or delete its discovery metadata", () =>
    {
        var lifecycle = new PipeListenerLifecycle<FakeAcceptResource>();
        string? discoveryMetadata = null;
        long failedGeneration = lifecycle.Start();
        True(lifecycle.RunIfCurrent(failedGeneration, () => discoveryMetadata = "old-generation"));
        True(lifecycle.Stop(failedGeneration, _ => { }, () => discoveryMetadata = null));
        Equal<string?>(null, discoveryMetadata);

        long restartedGeneration = lifecycle.Start();
        True(lifecycle.RunIfCurrent(restartedGeneration, () => discoveryMetadata = "new-generation"));

        bool stoppedRestart = lifecycle.Stop(failedGeneration, _ => { }, () => discoveryMetadata = null);
        True(!stoppedRestart);
        True(lifecycle.IsRunning);
        Equal("new-generation", discoveryMetadata);

        lifecycle.Stop(restartedGeneration, _ => { }, () => discoveryMetadata = null);
        Equal<string?>(null, discoveryMetadata);
    });

    Run("malformed, unauthorized, and dispatch exception responses share the explicit null-result envelope", () =>
    {
        string dispatcherSource = File.ReadAllText(SourceFile("src/shared/Infrastructure/CommandDispatcher.cs"));
        string pipeSource = File.ReadAllText(SourceFile("src/shared/Transport/PipeTransportServer.cs"));

        True(dispatcherSource.Contains("JObject.Parse(requestLine)", StringComparison.Ordinal));
        True(dispatcherSource.Contains("return ErrorJson(id, \"unauthorized\");", StringComparison.Ordinal));
        True(dispatcherSource.Contains("return ErrorJson(id, ex.Message);", StringComparison.Ordinal));
        True(pipeSource.Contains(
            "CommandDispatcher.ErrorJson(null, $\"dispatch error: {ex.Message}\")",
            StringComparison.Ordinal));

        True(dispatcherSource.Contains("public static string ErrorJson(string id, string error)", StringComparison.Ordinal));
        True(dispatcherSource.Contains("ok = false,", StringComparison.Ordinal));
        True(dispatcherSource.Contains("result = (object)null,", StringComparison.Ordinal));
        True(dispatcherSource.Contains(
            "error = McpResponsePrivacy.SanitizeError(error)",
            StringComparison.Ordinal));
    });

    Run("pipe listener publishes the authentication token only inside the current generation gate", () =>
    {
        string pipeSource = File.ReadAllText(SourceFile("src/shared/Transport/PipeTransportServer.cs"));
        int guardedStart = pipeSource.IndexOf(
            "_lifecycle.RunIfCurrent(generation, () =>", StringComparison.Ordinal);
        True(guardedStart >= 0);
        int tokenPublication = pipeSource.IndexOf("AuthToken = authToken;", StringComparison.Ordinal);
        int guardedEnd = pipeSource.IndexOf("});", guardedStart, StringComparison.Ordinal);
        True(guardedStart >= 0 && tokenPublication > guardedStart && tokenPublication < guardedEnd);
        True(pipeSource.Contains("string authToken = Guid.NewGuid().ToString(\"N\");", StringComparison.Ordinal));
    });

    Run("control pipe discovery is generation-specific and cleanup is scoped to its published identity", () =>
    {
        string pipeSource = File.ReadAllText(SourceFile("src/shared/Transport/PipeTransportServer.cs"));

        True(pipeSource.Contains("control_pipe_name = ControlPipeName", StringComparison.Ordinal));
        True(pipeSource.Contains("_pipeName + \"-control-\" + Guid.NewGuid().ToString(\"N\")", StringComparison.Ordinal));
        True(pipeSource.Contains("PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly", StringComparison.Ordinal));
        True(pipeSource.Contains("_controlLifecycle.CreateAndPublish(controlGeneration", StringComparison.Ordinal));
        True(pipeSource.Contains("_controlLifecycle.Release(controlGeneration, pipe", StringComparison.Ordinal));
        True(pipeSource.Contains("_controlLifecycle.Stop(_ => { });", StringComparison.Ordinal));
        True(pipeSource.Contains("lock (_startStopGate)", StringComparison.Ordinal));
        True(pipeSource.Contains("current[\"auth_token\"]", StringComparison.Ordinal));
        True(pipeSource.Contains("current[\"control_pipe_name\"]", StringComparison.Ordinal));

        int writerStart = pipeSource.IndexOf("private void WriteDiscoveryFile()", StringComparison.Ordinal);
        int writerEnd = pipeSource.IndexOf("private void DeleteDiscoveryFile()", writerStart, StringComparison.Ordinal);
        True(writerStart >= 0 && writerEnd > writerStart);
        string discoveryWriter = pipeSource.Substring(writerStart, writerEnd - writerStart);
        int tempWrite = discoveryWriter.IndexOf("File.WriteAllText(temporaryPath", StringComparison.Ordinal);
        int replace = discoveryWriter.IndexOf("File.Replace(temporaryPath, destinationPath, null)", StringComparison.Ordinal);
        int move = discoveryWriter.IndexOf("File.Move(temporaryPath, destinationPath)", StringComparison.Ordinal);
        True(tempWrite >= 0 && replace > tempWrite && move > tempWrite);
        True(!discoveryWriter.Contains("File.WriteAllText(JsonDiscoveryPath", StringComparison.Ordinal));
    });

    Run("control pipe bounds input and output and accepts one authenticated job-control frame", () =>
    {
        string pipeSource = File.ReadAllText(SourceFile("src/shared/Transport/PipeTransportServer.cs"));
        string frameSource = File.ReadAllText(SourceFile("src/shared/Transport/PipeControlFrameProtocol.cs"));
        string dispatcherSource = File.ReadAllText(SourceFile("src/shared/Infrastructure/CommandDispatcher.cs"));
        int controlStart = dispatcherSource.IndexOf("public string DispatchControl(string requestLine)", StringComparison.Ordinal);
        int controlEnd = dispatcherSource.IndexOf("private static CommandResult DispatchBindActiveDocument", controlStart, StringComparison.Ordinal);
        True(controlStart >= 0 && controlEnd > controlStart);
        string controlDispatch = dispatcherSource.Substring(controlStart, controlEnd - controlStart);

        True(frameSource.Contains("MaxRequestFrameBytes = 4096", StringComparison.Ordinal));
        True(frameSource.Contains("MaxResponseFrameBytes = 8 * 1024 * 1024", StringComparison.Ordinal));
        True(frameSource.Contains("FrameDeadlineMilliseconds = 3000", StringComparison.Ordinal));
        True(frameSource.Contains("deadline.CancelAfter(deadlineMilliseconds)", StringComparison.Ordinal));
        True(frameSource.Contains("pipe.ReadAsync(oneByte, 0, 1, deadline.Token)", StringComparison.Ordinal));
        True(frameSource.Contains("pipe.WriteAsync(bytes, 0, bytes.Length, deadline.Token)", StringComparison.Ordinal));
        True(!pipeSource.Contains("ReadTimeout =", StringComparison.Ordinal));
        True(!pipeSource.Contains("WriteTimeout =", StringComparison.Ordinal));
        True(pipeSource.Contains("dispatcher.DispatchControl(requestLine)", StringComparison.Ordinal));
        True(!controlDispatch.Contains("DispatchBindActiveDocument", StringComparison.Ordinal));
        True(!controlDispatch.Contains("DispatchJobSubmit", StringComparison.Ordinal));
        True(!controlDispatch.Contains("ExecuteCommand", StringComparison.Ordinal));
        True(controlDispatch.Contains("DispatchJobGet(parameters)", StringComparison.Ordinal));
        True(controlDispatch.Contains("DispatchJobCancel(parameters)", StringComparison.Ordinal));
        True(controlDispatch.Contains("command_not_allowed_on_control_pipe", StringComparison.Ordinal));
        True(controlDispatch.Contains("unauthorized", StringComparison.Ordinal));
        True(dispatcherSource.Contains("job_get requires the control pipe", StringComparison.Ordinal));
        True(dispatcherSource.Contains("job_cancel requires the control pipe", StringComparison.Ordinal));
        True(dispatcherSource.Contains("App.PluginJournal.CancelQueued(requestId", StringComparison.Ordinal));
    });

    Run("control frame codec round-trips get and cancel over a real current-user named pipe", () =>
    {
        VerifyControlPipeRoundTrip("job_get", "job_get:accepted");
        VerifyControlPipeRoundTrip("job_cancel", "job_cancel:accepted");
        VerifyControlPipeRoundTrip("bind_active_document", "command_not_allowed_on_control_pipe");
        VerifyControlPipeRoundTrip("create_line", "command_not_allowed_on_control_pipe");
    });

    Run("discovery mutex serializes publishers and cleanup workers across threads", () =>
    {
        var publisher = new DiscoveryFileMutex("2026");
        using var waiterStarted = new ManualResetEventSlim();
        Task<bool> waiter = Task.Run(() =>
        {
            waiterStarted.Set();
            using var cleanup = new DiscoveryFileMutex("2026", timeoutMilliseconds: 5000);
            return true;
        });

        try
        {
            True(waiterStarted.Wait(TimeSpan.FromSeconds(2)));
            Thread.Sleep(100);
            True(!waiter.IsCompleted);
        }
        finally
        {
            publisher.Dispose();
        }

        True(waiter.Wait(TimeSpan.FromSeconds(3)) && waiter.Result);
    });

    Run("discovery mutex serializes across processes and releases for a later process", () =>
    {
        string target = (1000 + Random.Shared.Next(0, 9000)).ToString();
        using (var publisher = new DiscoveryFileMutex(target))
        {
            Equal("MUTEX_TIMED_OUT", RunDiscoveryMutexProbe(target, timeoutMilliseconds: 300));
        }

        Equal("MUTEX_ACQUIRED", RunDiscoveryMutexProbe(target, timeoutMilliseconds: 3000));
    });

    Run("discovery mutex uses a SID-scoped global name and an explicit current-user DACL", () =>
    {
        const string sid = "S-1-5-21-101-202-303-1001";
        string expectedName = "Global\\Bimwright.Discovery." + sid + ".2026";
        Equal(expectedName, DiscoveryFileMutex.BuildMutexName(sid, "2026"));
        True(DiscoveryFileMutex.BuildMutexName("S-1-5-21-404-505-606-1001", "2026") != expectedName);
        True(DiscoveryFileMutex.BuildMutexName(sid, "2025") != expectedName);
        Equal("O:" + sid + "D:P(A;;0x001F0001;;;" + sid + ")",
            DiscoveryFileMutex.BuildMutexSecurityDescriptorSddl(sid));

        string currentUserName = DiscoveryFileMutex.GetMutexNameForCurrentUser("2026");
        True(currentUserName.StartsWith("Global\\Bimwright.Discovery.S-", StringComparison.Ordinal));
        True(currentUserName.EndsWith(".2026", StringComparison.Ordinal));
        Throws<ArgumentException>(() => new DiscoveryFileMutex("20x6"));
        Throws<ArgumentException>(() => new DiscoveryFileMutex("20260"));
        Throws<ArgumentOutOfRangeException>(() => new DiscoveryFileMutex("2026", timeoutMilliseconds: 0));
    });

    Run("discovery mutex preserves timeout and abandoned-lock recovery", () =>
    {
        string target = (1000 + Random.Shared.Next(0, 9000)).ToString();
        using (var publisher = new DiscoveryFileMutex(target))
        {
            Task<Exception?> timedWaiter = Task.Run(() =>
            {
                try
                {
                    using var cleanup = new DiscoveryFileMutex(target, timeoutMilliseconds: 100);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            True(timedWaiter.Wait(TimeSpan.FromSeconds(3)));
            True(timedWaiter.Result is TimeoutException);
        }

        string mutexName = DiscoveryFileMutex.GetMutexNameForCurrentUser(target);
        using (var abandoned = new Mutex(false, mutexName))
        {
            var ownerThread = new Thread(() => abandoned.WaitOne());
            ownerThread.Start();
            True(ownerThread.Join(TimeSpan.FromSeconds(3)));

            using var recovered = new DiscoveryFileMutex(target, timeoutMilliseconds: 3000);
        }
    });

    Run("all plugin discovery publishers and stop cleanup share the per-target mutex", () =>
    {
        string pipeSource = File.ReadAllText(SourceFile("src/shared/Transport/PipeTransportServer.cs"));
        string tcpSource = File.ReadAllText(SourceFile("src/shared/Transport/TcpTransportServer.cs"));
        True(pipeSource.Contains("using (new DiscoveryFileMutex(_target))", StringComparison.Ordinal));
        True(tcpSource.Contains("using (new DiscoveryFileMutex(_target))", StringComparison.Ordinal));
        True(pipeSource.Contains("File.Replace(temporaryPath, destinationPath, null)", StringComparison.Ordinal));
        True(tcpSource.Contains("File.Replace(temporaryPath, destinationPath, null)", StringComparison.Ordinal));
        True(pipeSource.Contains("current[\"control_pipe_name\"]", StringComparison.Ordinal));
        True(tcpSource.Contains("current[\"auth_token\"]", StringComparison.Ordinal));
    });

    Run("control frame codec enforces async read/write deadlines and rejects oversized frames", () =>
    {
        string timeoutPipeName = "bimwright-control-timeout-" + Guid.NewGuid().ToString("N");
        Task<Exception?> timeoutServer = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(timeoutPipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync();
            try
            {
                await PipeControlFrameProtocol.ReadFrameAsync(server, deadlineMilliseconds: 200);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });
        using (var client = new NamedPipeClientStream(".", timeoutPipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            client.Connect(3000);
            True(timeoutServer.GetAwaiter().GetResult() is TimeoutException);
        }

        string oversizedPipeName = "bimwright-control-oversize-" + Guid.NewGuid().ToString("N");
        Task<Exception?> oversizedServer = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(oversizedPipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync();
            try
            {
                await PipeControlFrameProtocol.ReadFrameAsync(server, deadlineMilliseconds: 2000);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });
        using (var client = new NamedPipeClientStream(".", oversizedPipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            client.Connect(3000);
            byte[] oversizedFrame = new byte[PipeControlFrameProtocol.MaxRequestFrameBytes + 1];
            Array.Fill(oversizedFrame, (byte)'x');
            client.Write(oversizedFrame, 0, oversizedFrame.Length);
            True(oversizedServer.GetAwaiter().GetResult() is InvalidDataException);
        }

        string writeTimeoutPipeName = "bimwright-control-write-timeout-" + Guid.NewGuid().ToString("N");
        Task<Exception?> writeTimeoutServer = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(writeTimeoutPipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync();
            try
            {
                await PipeControlFrameProtocol.WriteFrameAsync(server,
                    new string('x', PipeControlFrameProtocol.MaxResponseFrameBytes), deadlineMilliseconds: 200);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });
        using (var client = new NamedPipeClientStream(".", writeTimeoutPipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            client.Connect(3000);
            True(writeTimeoutServer.GetAwaiter().GetResult() is TimeoutException);
        }
    });

    Run("two owners share one journal and a live owner's work is not recovered", () =>
    {
        string db = Db("two-owners");
        var liveness = new FakeLivenessProvider();
        PluginOwnerIdentity ownerA = Owner();
        PluginOwnerIdentity ownerB = Owner();
        var beforeInit = new SqlitePluginIntentJournal(db, ownerA, liveness);
        Throws<InvalidOperationException>(() => beforeInit.Submit(Read("before-init"), Now));
        Throws<InvalidOperationException>(() => beforeInit.TryClaimNext(Now));
        beforeInit.Initialize(Now);
        beforeInit.Dispose();
        using var a = Open(db, ownerA, liveness);
        PluginJobSnapshot running = Claimed(a, Write("owner-a-write"));
        using var b = Open(db, ownerB, liveness);
        Equal<PluginJobSnapshot?>(null, b.GetSnapshot("absent-request"));
        Equal(PluginJobPhase.Running, b.GetSnapshot(running.RequestId)!.Phase);
        Equal(PluginSubmitDisposition.FenceConflict, b.Submit(Write("owner-b-conflict"), Now).Disposition);
        Equal(PluginCancelDisposition.Rejected, b.CancelQueued(running.RequestId, Now).Disposition);
        Equal(PluginTransitionDisposition.Rejected, b.RecordCommitted(running.RequestId, "foreign", Now).Disposition);
        Equal(PluginTransitionDisposition.Rejected, b.RecordTerminal(running.RequestId, PluginJobPhase.Failed,
            PluginEffectState.Unknown, null, "foreign", null, Now).Disposition);
        Equal(0, b.RecoverLostOwners(Now));
        Equal(PluginJobPhase.Running, a.GetSnapshot(running.RequestId)!.Phase);
    });

    Run("startup recovers only confirmed-lost owners and leaves indeterminate owners intact", () =>
    {
        string db = Db("liveness");
        var liveness = new FakeLivenessProvider();
        PluginOwnerIdentity lost = Owner();
        PluginOwnerIdentity uncertain = Owner();
        using (var a = Open(db, lost, liveness)) a.Submit(Write("lost-queued"), Now);
        PluginJobSnapshot uncertainRunning;
        using (var b = Open(db, uncertain, liveness))
            uncertainRunning = Claimed(b, Write("uncertain-running", @"C:\Road\Uncertain.dwg"));
        liveness.Set(lost, PluginOwnerLiveness.ConfirmedLost);
        liveness.Set(uncertain, PluginOwnerLiveness.Indeterminate);
        using var recovery = Open(db, Owner(), liveness);
        PluginJobSnapshot recovered = recovery.GetSnapshot("lost-queued")!;
        Equal(PluginJobPhase.Unknown, recovered.Phase);
        Equal(PluginObservationState.Recovery, recovered.Observation);
        Equal(PluginEffectState.None, recovered.Effects);
        Equal(PluginJobPhase.Running, recovery.GetSnapshot(uncertainRunning.RequestId)!.Phase);
        Equal(PluginObservationState.PluginSnapshot, recovery.GetSnapshot(uncertainRunning.RequestId)!.Observation);
    });

    Run("same GUID and process with a new plugin generation recovers old work", () =>
    {
        string db = Db("generation-lock");
        using Process currentProcess = Process.GetCurrentProcess();
        var oldOwner = Owner("acad26-a", pid: currentProcess.Id,
            started: currentProcess.StartTime.ToUniversalTime());
        PluginOwnerIdentity newOwner = new(oldOwner.OwnerId, oldOwner.ProcessId,
            oldOwner.ProcessStartedAtUtc, "acad26-b");
        string completedId = "old-completed";
        string runningId = "old-running";
        string queuedId = "old-queued";
        long oldRunningSequence;
        long oldQueuedSequence;
        long completedSequence;
        using (var old = Open(db, oldOwner, new FakeLivenessProvider()))
        {
            // Finish the FIFO head, then prove a committed terminal survives recovery.
            PluginJobSnapshot head = Claimed(old, Write(completedId, @"C:\Road\Completed.dwg"));
            Equal(PluginJobPhase.Running, head.Phase);
            old.RecordCommitted(completedId, "saved", Now);
            old.RecordTerminal(completedId, PluginJobPhase.Completed, PluginEffectState.Committed,
                "saved", null, null, Now);
            completedSequence = old.GetSnapshot(completedId)!.PluginSequence;

            Claimed(old, Write(runningId, @"C:\Road\Running.dwg"));
            old.Submit(Write(queuedId, @"C:\Road\Queued.dwg"), Now);
            oldRunningSequence = old.GetSnapshot(runningId)!.PluginSequence;
            oldQueuedSequence = old.GetSnapshot(queuedId)!.PluginSequence;
        }
        // Exercise recovery of a legacy running write whose previous build stored None at start.
        using (SqliteConnection connection = Sqlite(db))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE plugin_jobs SET effects='none' WHERE request_id=$id;";
            command.Parameters.AddWithValue("$id", runningId);
            command.ExecuteNonQuery();
        }
        using var next = new SqlitePluginIntentJournal(db, newOwner, new FileAndProcessPluginOwnerLivenessProvider(db));
        Equal(2, next.Initialize(Now));
        PluginJobSnapshot recoveredRunning = next.GetSnapshot(runningId)!;
        Equal(PluginJobPhase.Unknown, recoveredRunning.Phase);
        Equal(PluginEffectState.Unknown, recoveredRunning.Effects);
        Equal(PluginObservationState.Recovery, recoveredRunning.Observation);
        Equal(oldRunningSequence + 1, recoveredRunning.PluginSequence);
        PluginJobSnapshot recoveredQueued = next.GetSnapshot(queuedId)!;
        Equal(PluginJobPhase.Unknown, recoveredQueued.Phase);
        Equal(PluginEffectState.None, recoveredQueued.Effects);
        Equal(oldQueuedSequence + 1, recoveredQueued.PluginSequence);
        PluginJobSnapshot terminalSnapshot = next.GetSnapshot(completedId)!;
        Equal(PluginJobPhase.Completed, terminalSnapshot.Phase);
        Equal(PluginEffectState.Committed, terminalSnapshot.Effects);
        Equal(completedSequence, terminalSnapshot.PluginSequence);
        Equal(false, terminalSnapshot.WriteFenceReleased);
        Equal(2, recoveredRunning.HeldRiskKeys.Count);
    });

    Run("process start mismatch detects PID reuse and the exact generation lock is alive", () =>
    {
        string db = Db("pid-reuse");
        Directory.CreateDirectory(db + ".owners");
        using Process process = Process.GetCurrentProcess();
        DateTimeOffset start = process.StartTime.ToUniversalTime();
        PluginOwnerIdentity live = new(Guid.NewGuid().ToString("N"), process.Id, start, "live-generation");
        var provider = new FileAndProcessPluginOwnerLivenessProvider(db);
        string lockPath = PluginOwnerLockIdentityForTest(db, live);
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Equal(PluginOwnerLiveness.Alive, provider.GetLiveness(live));
            PluginOwnerIdentity reusedPid = new(live.OwnerId, process.Id, start.AddSeconds(-5), live.PluginGeneration);
            Equal(PluginOwnerLiveness.ConfirmedLost, provider.GetLiveness(reusedPid));
            PluginOwnerIdentity newGeneration = new(live.OwnerId, process.Id, start, "new-generation");
            Equal(PluginOwnerLiveness.ConfirmedLost, provider.GetLiveness(newGeneration));
        }
    });

    Run("request ID replay covers owner, deadline, and canonical payload", () =>
    {
        string db = Db("digest");
        var liveness = new FakeLivenessProvider();
        PluginOwnerIdentity owner = Owner();
        using var journal = Open(db, owner, liveness);
        PluginSubmitResult first = journal.Submit(Write("same-id", parameters: "{\"b\":2,\"a\":1}", deadline: Now.AddMinutes(5)), Now);
        Equal(PluginSubmitDisposition.Created, first.Disposition);
        Equal(PluginSubmitDisposition.Existing,
            journal.Submit(Write("same-id", parameters: "{ \"a\":1, \"b\":2 }", deadline: Now.AddMinutes(5)), Now).Disposition);
        Equal(PluginSubmitDisposition.RequestIdConflict,
            journal.Submit(Write("same-id", parameters: "{\"a\":9,\"b\":2}", deadline: Now.AddMinutes(5)), Now).Disposition);
        Equal(PluginSubmitDisposition.RequestIdConflict, journal.Submit(new PluginJobSubmission("same-id", "other-context",
            "drawing_write", "{\"a\":1,\"b\":2}", "{\"document\":\"C:/Road/A.dwg\",\"instance\":\"acad-a\"}",
            true, ["path:C:/Road/A.dwg", "alias:legacy:A"], false, Now.AddMinutes(5)), Now).Disposition);
        Equal(PluginSubmitDisposition.RequestIdConflict, journal.Submit(new PluginJobSubmission("same-id", "ctx",
            "other_tool", "{\"a\":1,\"b\":2}", "{\"document\":\"C:/Road/A.dwg\",\"instance\":\"acad-a\"}",
            true, ["path:C:/Road/A.dwg", "alias:legacy:A"], false, Now.AddMinutes(5)), Now).Disposition);
        Equal(PluginSubmitDisposition.RequestIdConflict, journal.Submit(new PluginJobSubmission("same-id", "ctx",
            "drawing_write", "{\"a\":1,\"b\":2}", "{\"document\":\"C:/Road/A.dwg\",\"instance\":\"acad-b\"}",
            true, ["path:C:/Road/A.dwg", "alias:legacy:A"], false, Now.AddMinutes(5)), Now).Disposition);
        Equal(PluginSubmitDisposition.RequestIdConflict,
            journal.Submit(Write("same-id", deadline: Now.AddMinutes(6)), Now).Disposition);
        using var otherOwner = Open(db, Owner(), liveness);
        Equal(PluginSubmitDisposition.RequestIdConflict, otherOwner.Submit(Write("same-id", deadline: Now.AddMinutes(5)), Now).Disposition);
        Equal(first.Job!.QueueDeadlineUtc, journal.GetSnapshot("same-id")!.QueueDeadlineUtc);
    });

    Run("global fence conflicts in both directions and normal writes require trusted path", () =>
    {
        string db = Db("global-bidirectional");
        using var journal = Open(db, Owner(), new FakeLivenessProvider());
        Equal(PluginSubmitDisposition.Created, journal.Submit(GlobalWrite("global-first"), Now).Disposition);
        PluginSubmitResult pathConflict = journal.Submit(Write("path-after-global"), Now);
        Equal(PluginSubmitDisposition.FenceConflict, pathConflict.Disposition);
        Contains("<GLOBAL_DRAWING_FENCE>", pathConflict.ConflictingRiskKeys);

        string otherDb = Db("path-before-global");
        using var reverse = Open(otherDb, Owner(), new FakeLivenessProvider());
        Equal(PluginSubmitDisposition.Created, reverse.Submit(Write("path-first"), Now).Disposition);
        Equal(PluginSubmitDisposition.FenceConflict, reverse.Submit(GlobalWrite("global-after-path"), Now).Disposition);
        Throws<ArgumentException>(() => reverse.Submit(new PluginJobSubmission("alias-only", "ctx", "write", "{}",
            "{\"instance\":\"x\"}", true, ["alias:old-file"], false), Now));
    });

    Run("claim and queued cancel race is atomic; expired head is terminal and cannot be claimed late", () =>
    {
        for (int index = 0; index < 20; index++)
        {
            string db = Db("race-" + index);
            using var journal = Open(db, Owner(), new FakeLivenessProvider());
            journal.Submit(Write("race" + index), Now);
            using var gate = new Barrier(3);
            PluginCancelResult? cancel = null;
            PluginClaimResult? claim = null;
            Task cancelTask = Task.Run(() => { gate.SignalAndWait(); cancel = journal.CancelQueued("race" + index, Now); });
            Task claimTask = Task.Run(() => { gate.SignalAndWait(); claim = journal.TryClaimNext(Now); });
            gate.SignalAndWait();
            Task.WaitAll(cancelTask, claimTask);
            PluginJobPhase phase = journal.GetSnapshot("race" + index)!.Phase;
            if (cancel!.Disposition == PluginCancelDisposition.Cancelled)
            {
                Equal(PluginJobPhase.Cancelled, phase);
                Equal(PluginClaimDisposition.NoWork, claim!.Disposition);
                Equal(PluginClaimDisposition.NoWork, journal.TryClaimNext(Now).Disposition);
            }
            else
            {
                Equal(PluginCancelDisposition.CancellationRequested, cancel.Disposition);
                Equal(PluginJobPhase.CancellationRequested, phase);
                True(claim!.Disposition is PluginClaimDisposition.Claimed or PluginClaimDisposition.NoWork);
                Equal(false, journal.GetSnapshot("race" + index)!.WriteFenceReleased);
            }
        }

        string deadlineDb = Db("deadline");
        using var expiredJournal = Open(deadlineDb, Owner(), new FakeLivenessProvider());
        expiredJournal.Submit(Write("expired", deadline: Now), Now.AddMinutes(-1));
        PluginClaimResult expired = expiredJournal.TryClaimNext(Now);
        Equal(PluginClaimDisposition.QueueDeadlineExpired, expired.Disposition);
        Equal(PluginJobPhase.Cancelled, expired.Job!.Phase);
        Equal("queue_deadline_exceeded", expired.Job.ErrorCode);
        Equal(PluginEffectState.None, expired.Job.Effects);
        Equal(true, expired.Job.WriteFenceReleased);
        Equal(PluginClaimDisposition.NoWork, expiredJournal.TryClaimNext(Now).Disposition);

        for (int index = 0; index < 10; index++)
        {
            string raceDb = Db("deadline-race-" + index);
            using var journal = Open(raceDb, Owner(), new FakeLivenessProvider());
            journal.Submit(Write("deadline-race" + index, deadline: Now), Now.AddMinutes(-1));
            using var gate = new Barrier(3);
            PluginCancelResult? cancel = null;
            PluginClaimResult? claim = null;
            Task cancelTask = Task.Run(() => { gate.SignalAndWait(); cancel = journal.CancelQueued("deadline-race" + index, Now); });
            Task claimTask = Task.Run(() => { gate.SignalAndWait(); claim = journal.TryClaimNext(Now); });
            gate.SignalAndWait();
            Task.WaitAll(cancelTask, claimTask);
            PluginJobSnapshot final = journal.GetSnapshot("deadline-race" + index)!;
            Equal(PluginJobPhase.Cancelled, final.Phase);
            Equal("queue_deadline_exceeded", final.ErrorCode);
            True(cancel!.Disposition is PluginCancelDisposition.Cancelled or PluginCancelDisposition.AlreadyTerminal);
            True(claim!.Disposition is PluginClaimDisposition.QueueDeadlineExpired or PluginClaimDisposition.NoWork);
            Equal(PluginClaimDisposition.NoWork, journal.TryClaimNext(Now).Disposition);
        }
    });

    Run("running cancellation only requests cancellation; one active owner job still allows reads", () =>
    {
        string db = Db("running-cancel");
        using var journal = Open(db, Owner(), new FakeLivenessProvider());
        PluginJobSnapshot running = Claimed(journal, Write("running-write"));
        Equal(PluginSubmitDisposition.Created, journal.Submit(Read("read-during-write"), Now).Disposition);
        Equal(PluginClaimDisposition.NoWork, journal.TryClaimNext(Now).Disposition);
        Equal(PluginJobPhase.Running, journal.GetSnapshot(running.RequestId)!.Phase);
        PluginCancelResult cancel = journal.CancelQueued(running.RequestId, Now);
        Equal(PluginCancelDisposition.CancellationRequested, cancel.Disposition);
        Equal(PluginJobPhase.CancellationRequested, cancel.Job!.Phase);
        Equal(PluginEffectState.Unknown, cancel.Job.Effects);
        Equal(false, cancel.Job.WriteFenceReleased);
        Equal(PluginClaimDisposition.NoWork, journal.TryClaimNext(Now).Disposition);
    });

    Run("cancellation after commit preserves result and duplicate commit", () =>
    {
        string db = Db("cancel-after-commit");
        using var journal = Open(db, Owner(), new FakeLivenessProvider());
        PluginJobSnapshot running = Claimed(journal, Write("committed-running-write"));
        Equal(PluginTransitionDisposition.Applied,
            journal.RecordCommitted(running.RequestId, "durable-result", Now).Disposition);

        PluginCancelResult cancellation = journal.CancelQueued(running.RequestId, Now);
        Equal(PluginCancelDisposition.CancellationRequested, cancellation.Disposition);
        Equal(PluginJobPhase.CancellationRequested, cancellation.Job!.Phase);
        Equal(PluginEffectState.Committed, cancellation.Job.Effects);
        Equal("durable-result", cancellation.Job.ResultJson);

        PluginTransitionResult duplicate = journal.RecordCommitted(running.RequestId, "durable-result", Now);
        Equal(PluginTransitionDisposition.Duplicate, duplicate.Disposition);
        Equal("durable-result", duplicate.Job!.ResultJson);
        Equal(PluginEffectState.Committed, journal.GetSnapshot(running.RequestId)!.Effects);
    });

    Run("read jobs keep no effects and failed writes with unknown effects retain fences", () =>
    {
        string db = Db("effects");
        using var journal = Open(db, Owner(), new FakeLivenessProvider());
        PluginJobSnapshot read = Claimed(journal, Read("read-job"));
        Equal(PluginEffectState.None, read.Effects);
        PluginTransitionResult readFailed = journal.RecordTerminal(read.RequestId, PluginJobPhase.Failed,
            PluginEffectState.None, null, "read_failed", "synthetic", Now);
        Equal(PluginTransitionDisposition.Applied, readFailed.Disposition);
        Equal(PluginEffectState.None, readFailed.Job!.Effects);

        PluginJobSnapshot write = Claimed(journal, Write("failed-unknown"));
        Equal(PluginTransitionDisposition.Rejected,
            journal.RecordTerminal(write.RequestId, PluginJobPhase.Failed, PluginEffectState.None,
                null, "failed", "cannot prove no effects", Now).Disposition);
        PluginTransitionResult failed = journal.RecordTerminal(write.RequestId, PluginJobPhase.Failed,
            PluginEffectState.Unknown, null, "failed", "effects unknown", Now);
        Equal(PluginTransitionDisposition.Applied, failed.Disposition);
        Equal(PluginEffectState.Unknown, failed.Job!.Effects);
        Equal(false, failed.Job.WriteFenceReleased);
        Equal(PluginTransitionDisposition.Rejected, journal.RecordVerification(new PluginVerificationRequest(
            failed.Job.RequestId, failed.Job.ContextId, failed.Job.CompleteTargetJson,
            failed.Job.ProviderGeneration, 1, PluginVerificationState.Passed), Now).Disposition);
        Equal(PluginEffectState.Unknown, journal.GetSnapshot(write.RequestId)!.Effects);
        using var other = Open(db, Owner(), new FakeLivenessProvider());
        Equal(PluginSubmitDisposition.FenceConflict, other.Submit(Write("blocked-by-failure"), Now).Disposition);
    });

    Run("durable start and commit survive reopen; committed failure keeps fence", () =>
    {
        string db = Db("durable");
        PluginOwnerIdentity owner = Owner();
        var liveness = new FakeLivenessProvider();
        using (var first = Open(db, owner, liveness))
        {
            PluginJobSnapshot running = Claimed(first, Write("durable-write"));
            Equal(PluginJobPhase.Running, running.Phase);
            Equal(PluginEffectState.Unknown, running.Effects);
            PluginTransitionResult committed = first.RecordCommitted(running.RequestId, "commit-token", Now);
            Equal(PluginTransitionDisposition.Applied, committed.Disposition);
            Equal(PluginEffectState.Committed, committed.Job!.Effects);
            PluginTransitionResult duplicateCommit = first.RecordCommitted(running.RequestId, "commit-token", Now);
            Equal(PluginTransitionDisposition.Duplicate, duplicateCommit.Disposition);
            Equal(PluginTransitionDisposition.Rejected,
                first.RecordCommitted(running.RequestId, "different", Now).Disposition);
            PluginTransitionResult failed = first.RecordTerminal(running.RequestId, PluginJobPhase.Failed,
                PluginEffectState.Committed, "partial-save", "post_commit_error", "synthetic", Now);
            Equal(PluginTransitionDisposition.Applied, failed.Disposition);
            Equal(false, failed.Job!.WriteFenceReleased);
            Equal(PluginTransitionDisposition.Duplicate, first.RecordTerminal(running.RequestId, PluginJobPhase.Failed,
                PluginEffectState.Committed, "partial-save", "post_commit_error", "synthetic", Now).Disposition);
            Equal(PluginTransitionDisposition.Rejected, first.RecordTerminal(running.RequestId, PluginJobPhase.Failed,
                PluginEffectState.Committed, "changed", "post_commit_error", "synthetic", Now).Disposition);
        }
        using var reopened = Open(db, owner, liveness);
        PluginJobSnapshot persisted = reopened.GetSnapshot("durable-write")!;
        Equal(PluginJobPhase.Failed, persisted.Phase);
        Equal(PluginEffectState.Committed, persisted.Effects);
        Equal("partial-save", persisted.ResultJson);
        Equal(3L, persisted.PluginSequence);
        using var other = Open(db, Owner(), liveness);
        Equal(PluginSubmitDisposition.FenceConflict, other.Submit(Write("still-blocked"), Now).Disposition);
    });

    Run("readback has its own revision stream; Unknown and Failed retain fence; only Passed releases", () =>
    {
        string db = Db("readback");
        using var journal = Open(db, Owner(), new FakeLivenessProvider());
        PluginJobSnapshot running = Claimed(journal, Write("readback-write"));
        journal.RecordCommitted(running.RequestId, "saved", Now);
        PluginJobSnapshot completed = journal.RecordTerminal(running.RequestId, PluginJobPhase.Completed,
            PluginEffectState.Committed, "saved", null, null, Now).Job!;
        long sequence = completed.PluginSequence;
        Equal(false, completed.WriteFenceReleased);

        PluginVerificationRequest unknown = Readback(completed, 1, PluginVerificationState.Unknown, error: "timeout");
        PluginTransitionResult unknownResult = journal.RecordVerification(unknown, Now);
        Equal(PluginTransitionDisposition.Applied, unknownResult.Disposition);
        Equal(PluginVerificationState.Unknown, unknownResult.Job!.Verification);
        Equal(sequence, unknownResult.Job.PluginSequence);
        Equal(false, unknownResult.Job.WriteFenceReleased);
        Equal(PluginTransitionDisposition.Duplicate, journal.RecordVerification(unknown, Now).Disposition);

        PluginTransitionResult failed = journal.RecordVerification(Readback(completed, 2, PluginVerificationState.Failed,
            error: "mismatch"), Now);
        Equal(PluginVerificationState.Failed, failed.Job!.Verification);
        Equal(sequence, failed.Job.PluginSequence);
        Equal(false, failed.Job.WriteFenceReleased);
        Equal(PluginTransitionDisposition.StaleRevision,
            journal.RecordVerification(Readback(completed, 1, PluginVerificationState.Passed), Now).Disposition);

        PluginTransitionResult passed = journal.RecordVerification(Readback(completed, 3, PluginVerificationState.Passed,
            result: "{\"disk\":true}"), Now);
        Equal(PluginTransitionDisposition.Applied, passed.Disposition);
        Equal(PluginVerificationState.Passed, passed.Job!.Verification);
        Equal(sequence, passed.Job.PluginSequence);
        Equal(true, passed.Job.WriteFenceReleased);
        Equal(0, passed.Job.HeldRiskKeys.Count);
        Equal(PluginTransitionDisposition.Duplicate, journal.RecordVerification(
            Readback(completed, 3, PluginVerificationState.Passed, result: "{\"disk\":true}"), Now).Disposition);
        Equal(PluginTransitionDisposition.Rejected, journal.RecordVerification(
            Readback(completed, 4, PluginVerificationState.Failed), Now).Disposition);
        Equal(PluginSubmitDisposition.Created, journal.Submit(Write("after-readback"), Now).Disposition);
    });

    Run("snapshot reads see a consistent phase and held-fence revision during readback", () =>
    {
        string db = Db("snapshot-consistency");
        using var journal = Open(db, Owner(), new FakeLivenessProvider());
        PluginJobSnapshot running = Claimed(journal, Write("coherent-snapshot"));
        journal.RecordCommitted(running.RequestId, "saved", Now);
        PluginJobSnapshot completed = journal.RecordTerminal(running.RequestId, PluginJobPhase.Completed,
            PluginEffectState.Committed, "saved", null, null, Now).Job!;
        var failures = new ConcurrentQueue<string>();
        Task reader = Task.Run(() =>
        {
            for (int index = 0; index < 150; index++)
            {
                PluginJobSnapshot snapshot = journal.GetSnapshot(completed.RequestId)!;
                if (snapshot.WriteFenceReleased != (snapshot.HeldRiskKeys.Count == 0))
                    failures.Enqueue("release flag and risk-key snapshot disagreed");
            }
        });
        Task verifier = Task.Run(() => journal.RecordVerification(
            Readback(completed, 1, PluginVerificationState.Passed), Now));
        Task.WaitAll(reader, verifier);
        Equal(0, failures.Count);
    });

    Run("journal scope ID and configured-path digest persist and reject a relocated database", () =>
    {
        string db = Db("journal-scope");
        string relocated = db + "-relocated";
        PluginOwnerIdentity owner = Owner();
        string scopeId;
        string pathDigest;
        using (var journal = Open(db, owner, new FakeLivenessProvider()))
        {
            scopeId = journal.JournalScopeId;
            pathDigest = journal.JournalPathDigest;
            True(Guid.TryParseExact(scopeId, "N", out _));
            Equal(64, pathDigest.Length);
        }

        using (var reopened = Open(db, Owner(), new FakeLivenessProvider()))
        {
            Equal(scopeId, reopened.JournalScopeId);
            Equal(pathDigest, reopened.JournalPathDigest);
        }

        File.Copy(db, relocated);
        var movedJournal = new SqlitePluginIntentJournal(relocated, Owner(), new FakeLivenessProvider());
        Throws<InvalidOperationException>(() => movedJournal.Initialize(Now));
        movedJournal.Dispose();
    });

    Run("missing journal scope identity fails closed instead of minting a replacement", () =>
    {
        string db = Db("journal-scope-missing");
        using (Open(db, Owner(), new FakeLivenessProvider())) { }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM plugin_journal_identity WHERE singleton=1;";
            Equal(1, command.ExecuteNonQuery());
        }

        var missing = new SqlitePluginIntentJournal(db, Owner(), new FakeLivenessProvider());
        Throws<InvalidOperationException>(() => missing.Initialize(Now));
        missing.Dispose();
    });

    Run("write outcome guard rejects a document-context failure before handler entry", () =>
    {
        PluginWriteJobResult threwBeforeEntry = PluginWriteOutcomeGuard.Execute(_ =>
            throw new InvalidOperationException("injected context lookup failure"));
        Equal(PluginWriteJobOutcome.RejectedBeforeTransaction, threwBeforeEntry.Outcome);

        PluginWriteJobResult returnedBeforeEntry = PluginWriteOutcomeGuard.Execute(_ => null);
        Equal(PluginWriteJobOutcome.RejectedBeforeTransaction, returnedBeforeEntry.Outcome);
    });

    Run("write outcome guard records handler exceptions and missing outcomes as unknown", () =>
    {
        PluginWriteJobResult threwInHandler = PluginWriteOutcomeGuard.Execute(invokeHandler =>
            invokeHandler(() => throw new InvalidOperationException("injected handler failure")));
        Equal(PluginWriteJobOutcome.OutcomeUnknown, threwInHandler.Outcome);
        Equal(PluginVerificationState.Unknown, threwInHandler.Verification);

        PluginWriteJobResult missingOutcome = PluginWriteOutcomeGuard.Execute(invokeHandler =>
            invokeHandler(() => null));
        Equal(PluginWriteJobOutcome.OutcomeUnknown, missingOutcome.Outcome);
        Equal(PluginVerificationState.Unknown, missingOutcome.Verification);
        Equal(null, missingOutcome.ResultJson);
    });

    Run("write outcome guard preserves commit but drops passed readback after lock cleanup failure", () =>
    {
        using var journal = Open(Db("guard-committed-lock-cleanup"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = Write("guard-committed-lock-cleanup");
        Equal(PluginSubmitDisposition.Created, journal.Submit(request, Now).Disposition);
        const string committedJson = "{\"leader_handle\":\"A\",\"mtext_handle\":\"B\"}";
        var queue = new PluginExecutionQueue(journal, [], ["drawing_write"],
            (contextId, targetJson) => contextId == request.ContextId
                && targetJson == request.CompleteTargetJson,
            _ => PluginReadOnlyJobResult.Success("{}"),
            _ => PluginWriteOutcomeGuard.Execute(invokeHandler =>
            {
                PluginWriteJobResult handlerResult = invokeHandler(() => PluginWriteJobResult.Committed(
                    committedJson, PluginVerificationState.Passed, "{\"verified\":true}"))!;
                Equal(PluginWriteJobOutcome.Committed, handlerResult.Outcome);
                throw new InvalidOperationException("injected document-lock dispose failure");
            }));

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Completed, result.Disposition);
        PluginJobSnapshot snapshot = result.Job!;
        Equal(PluginJobPhase.Completed, snapshot.Phase);
        Equal(PluginEffectState.Committed, snapshot.Effects);
        Equal(committedJson, snapshot.ResultJson);
        Equal(PluginVerificationState.Unknown, snapshot.Verification);
        Equal(null, snapshot.VerificationResultJson);
        Equal(PluginWriteOutcomeGuard.LockCleanupFailedErrorCode, snapshot.VerificationErrorCode);
        Equal(false, snapshot.WriteFenceReleased);
        Equal(false, snapshot.WriteFenceFinalized);
    });

    Run("write outcome guard makes non-committed results unknown when lock cleanup fails", () =>
    {
        using var journal = Open(Db("guard-noncommitted-lock-cleanup"), Owner(), new FakeLivenessProvider());
        PluginJobSubmission request = Write("guard-noncommitted-lock-cleanup");
        Equal(PluginSubmitDisposition.Created, journal.Submit(request, Now).Disposition);
        var queue = new PluginExecutionQueue(journal, [], ["drawing_write"],
            (contextId, targetJson) => contextId == request.ContextId
                && targetJson == request.CompleteTargetJson,
            _ => PluginReadOnlyJobResult.Success("{}"),
            _ => PluginWriteOutcomeGuard.Execute(invokeHandler =>
            {
                invokeHandler(() => PluginWriteJobResult.RolledBackBeforeCommit(
                    "synthetic_rollback", "the handler confirmed rollback"));
                throw new InvalidOperationException("injected document-lock dispose failure");
            }));

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Halted, result.Disposition);
        PluginJobSnapshot snapshot = result.Job!;
        Equal(PluginJobPhase.Unknown, snapshot.Phase);
        Equal(PluginEffectState.Unknown, snapshot.Effects);
        Equal(null, snapshot.ResultJson);
        Equal(false, snapshot.WriteFenceReleased);
    });

    Run("D1 opt-in queue allowlists one write and uses exact context before invoking its handler", () =>
    {
        using (var journal = Open(Db("d1-write-not-allowlisted"), Owner(), new FakeLivenessProvider()))
        {
            PluginJobSubmission write = Write("d1-write-not-allowlisted");
            journal.Submit(write, Now);
            int verified = 0;
            int handled = 0;
            var queue = new PluginExecutionQueue(journal, [], ["another_write"], (_, _) =>
            {
                verified++;
                return true;
            }, _ => PluginReadOnlyJobResult.Success("{}"), _ =>
            {
                handled++;
                return PluginWriteJobResult.Committed("{}", PluginVerificationState.Passed, "{}");
            });

            PluginExecutionQueuePumpResult result = queue.Pump(Now);
            Equal(PluginExecutionQueuePumpDisposition.Failed, result.Disposition);
            Equal(0, verified);
            Equal(0, handled);
            Equal(PluginJobPhase.Failed, result.Job!.Phase);
            Equal(PluginEffectState.None, result.Job.Effects);
            Equal(true, result.Job.WriteFenceReleased);
            Equal(PluginNoEffectOutcome.RejectedBeforeTransaction, result.Job.NoEffectOutcome);
        }

        using (var journal = Open(Db("d1-write-context-mismatch"), Owner(), new FakeLivenessProvider()))
        {
            PluginJobSubmission write = Write("d1-write-context-mismatch");
            journal.Submit(write, Now);
            int handled = 0;
            var queue = new PluginExecutionQueue(journal, [], ["drawing_write"], (_, _) => false,
                _ => PluginReadOnlyJobResult.Success("{}"), _ =>
                {
                    handled++;
                    return PluginWriteJobResult.OutcomeUnknown("must_not_run", "context failed");
                });

            PluginExecutionQueuePumpResult result = queue.Pump(Now);
            Equal(PluginExecutionQueuePumpDisposition.Failed, result.Disposition);
            Equal(0, handled);
            Equal(PluginEffectState.None, result.Job!.Effects);
            Equal(PluginExecutionQueue.ExactContextMismatchErrorCode, result.Job.ErrorCode);
            Equal(true, result.Job.WriteFenceReleased);
        }
    });

    Run("D1 no-effect terminal binds claim identity and generic Unknown-to-None remains forbidden", () =>
    {
        string db = Db("d1-no-effect-identity");
        var liveness = new FakeLivenessProvider();
        using var journal = Open(db, Owner(), liveness);
        PluginJobSnapshot claimed = Claimed(journal, Write("d1-no-effect-identity"));
        PluginJobSubmission other = Write("d1-no-effect-other", @"C:\Road\Other.dwg");
        Equal(PluginSubmitDisposition.Created, journal.Submit(other, Now).Disposition);

        PluginNoEffectTerminalRequest valid = NoEffectRequest(claimed,
            PluginNoEffectOutcome.RolledBackBeforeCommit);
        Equal(PluginTransitionDisposition.Rejected, journal.RecordNoEffectTerminal(
            NoEffectRequest(claimed, PluginNoEffectOutcome.RolledBackBeforeCommit, contextId: "wrong-context"), Now).Disposition);
        Equal(PluginTransitionDisposition.Rejected, journal.RecordNoEffectTerminal(
            NoEffectRequest(claimed, PluginNoEffectOutcome.RolledBackBeforeCommit,
                claimSequence: claimed.PluginSequence + 1), Now).Disposition);
        Equal(PluginTransitionDisposition.Rejected, journal.RecordNoEffectTerminal(
            NoEffectRequest(claimed, PluginNoEffectOutcome.RolledBackBeforeCommit,
                payloadDigest: new string('0', 64)), Now).Disposition);
        Equal(PluginTransitionDisposition.Rejected, journal.RecordTerminal(claimed.RequestId,
            PluginJobPhase.Failed, PluginEffectState.None, null, "false-proof", "generic release forbidden", Now).Disposition);
        Equal(false, journal.GetSnapshot(claimed.RequestId)!.WriteFenceReleased);

        PluginTransitionResult released = journal.RecordNoEffectTerminal(valid, Now);
        Equal(PluginTransitionDisposition.Applied, released.Disposition);
        Equal(PluginJobPhase.Failed, released.Job!.Phase);
        Equal(PluginEffectState.None, released.Job.Effects);
        Equal(true, released.Job.WriteFenceReleased);
        Equal(claimed.PluginSequence, released.Job.NoEffectClaimSequence);
        Equal(PluginNoEffectOutcome.RolledBackBeforeCommit, released.Job.NoEffectOutcome);
        Equal(PluginTransitionDisposition.Duplicate, journal.RecordNoEffectTerminal(valid, Now).Disposition);
        using var contender = Open(db, Owner(), liveness);
        Equal(PluginSubmitDisposition.Created,
            contender.Submit(Write("d1-no-effect-released"), Now).Disposition);
    });

    Run("D1 committed handler uses second readback stage, retains fences, and finalizes only exact evidence", () =>
    {
        string db = Db("d1-staged-finalize");
        var liveness = new FakeLivenessProvider();
        PluginOwnerIdentity owner = Owner();
        using var journal = Open(db, owner, liveness);
        PluginJobSubmission write = Write("d1-staged-finalize");
        PluginJobSubmission secondDrawing = Write("d1-other-drawing", @"C:\Road\Other.dwg");
        Equal(PluginSubmitDisposition.Created, journal.Submit(write, Now).Disposition);
        Equal(PluginSubmitDisposition.Created, journal.Submit(secondDrawing, Now).Disposition);
        int handlerThreadId = 0;
        string committedJson = "{\"new_handles\":[\"ABC\"]}";
        string readbackJson = "{\"leader_type\":\"Leader\",\"annotation_handle\":\"ABD\",\"dot\":\"yellow\"}";
        var queue = new PluginExecutionQueue(journal, [], ["drawing_write"],
            (contextId, target) => contextId == write.ContextId && target == write.CompleteTargetJson,
            _ => PluginReadOnlyJobResult.Success("{}"), job =>
            {
                handlerThreadId = Environment.CurrentManagedThreadId;
                Equal(write.RequestId, job.RequestId);
                return PluginWriteJobResult.Committed(committedJson,
                    PluginVerificationState.Passed, readbackJson);
            });

        int pumpThreadId = Environment.CurrentManagedThreadId;
        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Completed, result.Disposition);
        Equal(pumpThreadId, handlerThreadId);
        PluginJobSnapshot staged = result.Job!;
        Equal(PluginJobPhase.Completed, staged.Phase);
        Equal(PluginEffectState.Committed, staged.Effects);
        Equal(PluginVerificationState.Passed, staged.Verification);
        Equal(readbackJson, staged.VerificationResultJson);
        Equal(false, staged.WriteFenceReleased);
        Equal(false, staged.WriteFenceFinalized);
        Equal(staged.PluginSequence, staged.VerificationStagedSequence);
        Equal(staged.PluginSequence, staged.ReadbackRevision);
        Equal(64, staged.CommittedResultDigest!.Length);
        Equal(64, staged.ReadbackDigest!.Length);
        Contains("PATH:C:\\ROAD\\A.DWG", staged.HeldRiskKeys);

        using var otherOwner = Open(db, Owner(), liveness);
        Equal(PluginSubmitDisposition.FenceConflict,
            otherOwner.Submit(Write("d1-staged-still-blocked"), Now).Disposition);
        Equal(PluginSubmitDisposition.FenceConflict,
            otherOwner.Submit(Write("d1-other-still-blocked", @"C:\Road\Other.dwg"), Now).Disposition);

        PluginFinalizeVerifiedWriteFenceRequest valid = FinalizeRequest(staged);
        PluginFinalizeVerifiedWriteFenceRequest[] invalid =
        [
            FinalizeRequest(staged, contextId: "wrong-context"),
            FinalizeRequest(staged, completeTargetJson: "{\"document\":\"C:/Road/Wrong.dwg\"}"),
            FinalizeRequest(staged, ownerId: Guid.NewGuid().ToString("D")),
            FinalizeRequest(staged, providerGeneration: "another-generation"),
            FinalizeRequest(staged, payloadDigest: new string('0', 64)),
            FinalizeRequest(staged, committedResultDigest: new string('0', 64)),
            FinalizeRequest(staged, stagedPluginSequence: staged.VerificationStagedSequence!.Value - 1),
            FinalizeRequest(staged, stagedPluginSequence: staged.VerificationStagedSequence!.Value + 1),
            FinalizeRequest(staged, readbackRevision: staged.ReadbackRevision!.Value - 1),
            FinalizeRequest(staged, readbackRevision: staged.ReadbackRevision!.Value + 1),
            FinalizeRequest(staged, readbackDigest: new string('0', 64)),
            FinalizeRequest(staged, requestId: secondDrawing.RequestId)
        ];
        foreach (PluginFinalizeVerifiedWriteFenceRequest request in invalid)
        {
            PluginTransitionResult rejected = journal.FinalizeVerifiedWriteFence(request, Now);
            True(rejected.Disposition is PluginTransitionDisposition.Rejected or PluginTransitionDisposition.StaleRevision
                or PluginTransitionDisposition.NotFound);
            Equal(false, journal.GetSnapshot(write.RequestId)!.WriteFenceReleased);
            Equal(false, journal.GetSnapshot(secondDrawing.RequestId)!.WriteFenceReleased);
        }

        PluginTransitionResult finalized = journal.FinalizeVerifiedWriteFence(valid, Now);
        Equal(PluginTransitionDisposition.Applied, finalized.Disposition);
        Equal(true, finalized.Job!.WriteFenceReleased);
        Equal(true, finalized.Job.WriteFenceFinalized);
        Equal(staged.VerificationStagedSequence!.Value + 1, finalized.Job.PluginSequence);
        Equal(0, finalized.Job.HeldRiskKeys.Count);
        Equal(PluginTransitionDisposition.Duplicate,
            journal.FinalizeVerifiedWriteFence(valid, Now.AddSeconds(1)).Disposition);
        Equal(PluginSubmitDisposition.FenceConflict,
            otherOwner.Submit(Write("d1-other-still-fenced", @"C:\Road\Other.dwg"), Now).Disposition);
        Equal(PluginSubmitDisposition.Created,
            otherOwner.Submit(Write("d1-original-risk-released"), Now).Disposition);
    });

    Run("D1 staged Passed readback survives journal reopen with its risk fence held", () =>
    {
        string db = Db("d1-stage-reopen");
        PluginOwnerIdentity owner = Owner();
        var liveness = new FakeLivenessProvider();
        PluginJobSnapshot beforeClose;
        using (var journal = Open(db, owner, liveness))
        {
            PluginJobSubmission write = Write("d1-stage-reopen");
            journal.Submit(write, Now);
            var queue = new PluginExecutionQueue(journal, [], ["drawing_write"], (_, _) => true,
                _ => PluginReadOnlyJobResult.Success("{}"), _ => PluginWriteJobResult.Committed(
                    "{\"ids\":[\"A\"]}", PluginVerificationState.Passed, "{\"readback\":true}"));
            Equal(PluginExecutionQueuePumpDisposition.Completed, queue.Pump(Now).Disposition);
            beforeClose = journal.GetSnapshot(write.RequestId)!;
            Equal(false, beforeClose.WriteFenceReleased);
            True(beforeClose.VerificationStagedSequence.HasValue);
        }

        using var reopened = Open(db, owner, liveness);
        PluginJobSnapshot afterOpen = reopened.GetSnapshot(beforeClose.RequestId)!;
        Equal(beforeClose.VerificationStagedSequence, afterOpen.VerificationStagedSequence);
        Equal(beforeClose.ReadbackDigest, afterOpen.ReadbackDigest);
        Equal(PluginVerificationState.Passed, afterOpen.Verification);
        Equal(false, afterOpen.WriteFenceReleased);
        Contains("PATH:C:\\ROAD\\A.DWG", afterOpen.HeldRiskKeys);
        PluginTransitionResult finalizedAfterRestart = reopened.FinalizeVerifiedWriteFence(
            FinalizeRequest(afterOpen), Now.AddSeconds(2));
        Equal(PluginTransitionDisposition.Applied, finalizedAfterRestart.Disposition);
        Equal(true, finalizedAfterRestart.Job!.WriteFenceFinalized);
        Equal(true, finalizedAfterRestart.Job.WriteFenceReleased);
        Equal(PluginTransitionDisposition.Duplicate,
            reopened.FinalizeVerifiedWriteFence(FinalizeRequest(afterOpen), Now.AddSeconds(3)).Disposition);
        using var otherOwner = Open(db, Owner(), liveness);
        Equal(PluginSubmitDisposition.Created,
            otherOwner.Submit(Write("d1-stage-reopen-conflict"), Now).Disposition);
    });

    Run("D1 default and non-passed readback states never release the write fence", () =>
    {
        foreach (PluginVerificationState state in new[]
            { PluginVerificationState.Unknown, PluginVerificationState.Failed })
        {
            string db = Db("d1-readback-" + state);
            var liveness = new FakeLivenessProvider();
            using var journal = Open(db, Owner(), liveness);
            PluginJobSubmission write = Write("d1-readback-" + state);
            journal.Submit(write, Now);
            var queue = new PluginExecutionQueue(journal, [], ["drawing_write"], (_, _) => true,
                _ => PluginReadOnlyJobResult.Success("{}"), _ => state == PluginVerificationState.Unknown
                    ? PluginWriteJobResult.Committed("{\"ids\":[\"A\"]}")
                    : PluginWriteJobResult.Committed("{\"ids\":[\"A\"]}", state,
                        "{\"checked\":false}", "readback_mismatch", "The new objects did not match.") );

            PluginExecutionQueuePumpResult result = queue.Pump(Now);
            Equal(PluginExecutionQueuePumpDisposition.Completed, result.Disposition);
            Equal(PluginJobPhase.Completed, result.Job!.Phase);
            Equal(PluginEffectState.Committed, result.Job.Effects);
            Equal(state, result.Job.Verification);
            Equal(false, result.Job.WriteFenceReleased);
            Equal(false, result.Job.WriteFenceFinalized);
            True(result.Job.VerificationStagedSequence.HasValue);
            using var contender = Open(db, Owner(), liveness);
            Equal(PluginSubmitDisposition.FenceConflict,
                contender.Submit(Write("d1-readback-conflict-" + state), Now).Disposition);
        }
    });

    Run("D1 write unknown or thrown handler outcome persists Unknown, retains fence, and halts FIFO", () =>
    {
        foreach (bool throws in new[] { false, true })
        {
            string db = Db("d1-unknown-" + throws);
            var liveness = new FakeLivenessProvider();
            using var journal = Open(db, Owner(), liveness);
            PluginJobSubmission write = Write("d1-unknown-" + throws);
            PluginJobSubmission later = Write("d1-after-unknown-" + throws, @"C:\Road\Other.dwg");
            journal.Submit(write, Now);
            journal.Submit(later, Now);
            var queue = new PluginExecutionQueue(journal, [], ["drawing_write"], (_, _) => true,
                _ => PluginReadOnlyJobResult.Success("{}"), _ =>
                {
                    if (throws) throw new InvalidOperationException("unknown after transaction start");
                    return PluginWriteJobResult.OutcomeUnknown("injected_unknown", "commit result unavailable");
                });

            PluginExecutionQueuePumpResult result = queue.Pump(Now);
            Equal(PluginExecutionQueuePumpDisposition.Halted, result.Disposition);
            True(queue.IsHalted);
            Equal(PluginJobPhase.Unknown, result.Job!.Phase);
            Equal(PluginEffectState.Unknown, result.Job.Effects);
            Equal(false, result.Job.WriteFenceReleased);
            True(result.Job.HeldRiskKeys.Count > 0);
            Equal(PluginExecutionQueuePumpDisposition.Halted, queue.Pump(Now.AddSeconds(1)).Disposition);
            Equal(PluginJobPhase.Queued, journal.GetSnapshot(later.RequestId)!.Phase);
            using var contender = Open(db, Owner(), liveness);
            Equal(PluginSubmitDisposition.FenceConflict,
                contender.Submit(Write("d1-unknown-still-fenced-" + throws), Now).Disposition);
        }
    });

    Run("D1 malformed committed result JSON is Unknown and never gets a no-effect release", () =>
    {
        string db = Db("d1-malformed-commit-result");
        var liveness = new FakeLivenessProvider();
        using var journal = Open(db, Owner(), liveness);
        PluginJobSubmission write = Write("d1-malformed-commit-result");
        journal.Submit(write, Now);
        var queue = new PluginExecutionQueue(journal, [], ["drawing_write"], (_, _) => true,
            _ => PluginReadOnlyJobResult.Success("{}"), _ => PluginWriteJobResult.Committed("not-json"));

        PluginExecutionQueuePumpResult result = queue.Pump(Now);
        Equal(PluginExecutionQueuePumpDisposition.Halted, result.Disposition);
        Equal(PluginJobPhase.Unknown, result.Job!.Phase);
        Equal(PluginEffectState.Unknown, result.Job.Effects);
        Equal(false, result.Job.WriteFenceReleased);
        Equal(null, result.Job.NoEffectOutcome);
        using var contender = Open(db, Owner(), liveness);
        Equal(PluginSubmitDisposition.FenceConflict,
            contender.Submit(Write("d1-malformed-result-still-fenced"), Now).Disposition);
    });

    Run("D1 uncertain committed, terminal, or stage journal acknowledgements halt and retain fences", () =>
    {
        foreach (UncertainWriteJournalFault fault in Enum.GetValues<UncertainWriteJournalFault>())
        {
            string db = Db("d1-journal-ack-" + fault);
            var liveness = new FakeLivenessProvider();
            using var durableJournal = Open(db, Owner(), liveness);
            PluginJobSubmission write = Write("d1-journal-ack-" + fault);
            durableJournal.Submit(write, Now);
            int handled = 0;
            var uncertainJournal = new UncertainWriteJournal(durableJournal, fault);
            var queue = new PluginExecutionQueue(uncertainJournal, [], ["drawing_write"], (_, _) => true,
                _ => PluginReadOnlyJobResult.Success("{}"), _ =>
                {
                    handled++;
                    return PluginWriteJobResult.Committed("{\"handles\":[\"A\"]}",
                        PluginVerificationState.Passed, "{\"readback\":true}");
                });

            PluginExecutionQueuePumpResult result = queue.Pump(Now);
            Equal(PluginExecutionQueuePumpDisposition.Halted, result.Disposition);
            Equal(true, queue.IsHalted);
            Equal(1, handled);
            Equal(PluginExecutionQueuePumpDisposition.Halted, queue.Pump(Now.AddSeconds(1)).Disposition);
            PluginJobSnapshot persisted = durableJournal.GetSnapshot(write.RequestId)!;
            True(persisted.Phase is PluginJobPhase.Running or PluginJobPhase.Completed);
            Equal(PluginEffectState.Committed, persisted.Effects);
            Equal(false, persisted.WriteFenceReleased);
            True(persisted.HeldRiskKeys.Count > 0);
            if (fault == UncertainWriteJournalFault.StageReadbackAfterCommit)
            {
                Equal(PluginJobPhase.Completed, persisted.Phase);
                Equal(PluginVerificationState.Passed, persisted.Verification);
                True(persisted.VerificationStagedSequence.HasValue);
            }
            using var contender = Open(db, Owner(), liveness);
            Equal(PluginSubmitDisposition.FenceConflict,
                contender.Submit(Write("d1-journal-ack-still-fenced-" + fault), Now).Disposition);
        }
    });

    Run("owner lock contention, lifecycle, Dispose wait, and post-dispose calls", () =>
    {
        string db = Db("owner-lock");
        PluginOwnerIdentity owner = Owner();
        var liveness = new FakeLivenessProvider();
        var first = Open(db, owner, liveness);
        var contender = new SqlitePluginIntentJournal(db, owner, liveness);
        Throws<InvalidOperationException>(() => contender.Initialize(Now));
        contender.Dispose();

        using SqliteConnection blocker = Sqlite(db);
        using SqliteTransaction blockedTransaction = blocker.BeginTransaction(deferred: false);
        Task submit = Task.Run(() => first.Submit(Read("dispose-blocked"), Now));
        Thread.Sleep(100);
        Task dispose = Task.Run(first.Dispose);
        Thread.Sleep(100);
        True(!dispose.IsCompleted);
        blockedTransaction.Commit();
        Task.WaitAll(submit, dispose);
        Throws<ObjectDisposedException>(() => first.GetSnapshot("dispose-blocked"));

        using var replacement = Open(db, owner, liveness);
        Equal(PluginJobPhase.Queued, replacement.GetSnapshot("dispose-blocked")!.Phase);
    });

    Run("DrawingRiskKey output matches the shared normalizer byte for byte", () =>
    {
        string normalized = DrawingRiskKey.Normalize("path:c:/Road/./A.dwg");
        Equal("PATH:C:\\ROAD\\A.DWG", normalized);
        using var journal = Open(Db("normalizer"), Owner(), new FakeLivenessProvider());
        PluginSubmitResult created = journal.Submit(new PluginJobSubmission("normalized", "ctx", "write", "{}",
            "{\"document\":\"c:/Road/A.dwg\"}", true,
            ["alias:legacy:road", "path:C:\\Road\\A.dwg", "C:/ROAD/A.DWG"], false), Now);
        Equal(PluginSubmitDisposition.Created, created.Disposition);
        Equal(2, created.Job!.HeldRiskKeys.Count);
        True(created.Job.HeldRiskKeys.Contains(normalized, StringComparer.Ordinal));
    });

    Run("process execution lease is exclusive by PID and exact start time across load contexts", () =>
    {
        string directory = Path.Combine(root, "process-leases");
        using Process process = Process.GetCurrentProcess();
        int processId = process.Id;
        DateTimeOffset startedAtUtc = process.StartTime.ToUniversalTime();

        using (ProcessExecutionLease held = ProcessExecutionLease.Acquire(directory, processId, startedAtUtc))
        {
            Equal(processId, held.ProcessId);
            Equal(startedAtUtc, held.ProcessStartedAtUtc);
            Throws<IOException>(() => ProcessExecutionLease.Acquire(directory, processId, startedAtUtc));

            var loadContext = new AssemblyLoadContext("lease-contract-" + Guid.NewGuid().ToString("N"), isCollectible: true);
            try
            {
                Assembly isolatedAssembly = loadContext.LoadFromAssemblyPath(typeof(ProcessExecutionLease).Assembly.Location);
                Type isolatedType = isolatedAssembly.GetType(typeof(ProcessExecutionLease).FullName!, throwOnError: true)!;
                MethodInfo acquire = isolatedType.GetMethod(nameof(ProcessExecutionLease.Acquire),
                    BindingFlags.Public | BindingFlags.Static)!;
                try
                {
                    acquire.Invoke(null, [directory, processId, startedAtUtc]);
                    throw new InvalidOperationException("A second load context unexpectedly acquired the process lease.");
                }
                catch (TargetInvocationException exception) when (exception.InnerException is IOException)
                {
                    // The OS file sharing lock, not a per-load-context static, rejected the contender.
                }
            }
            finally
            {
                loadContext.Unload();
            }

            using ProcessExecutionLease reusedPid = ProcessExecutionLease.Acquire(directory, processId,
                startedAtUtc.AddTicks(1));
            True(!string.Equals(held.LockPath, reusedPid.LockPath, StringComparison.Ordinal));
            using ProcessExecutionLease differentPid = ProcessExecutionLease.Acquire(directory, processId + 1, startedAtUtc);
            True(!string.Equals(held.LockPath, differentPid.LockPath, StringComparison.Ordinal));
        }

        using ProcessExecutionLease afterRelease = ProcessExecutionLease.Acquire(directory, processId, startedAtUtc);
    });

    Run("process execution lease rejects invalid identity and ambiguous directory paths", () =>
    {
        string directory = Path.Combine(root, "invalid-process-leases");
        using Process process = Process.GetCurrentProcess();
        int processId = process.Id;
        DateTimeOffset startedAtUtc = process.StartTime.ToUniversalTime();

        Throws<ArgumentOutOfRangeException>(() => ProcessExecutionLease.Acquire(directory, 0, startedAtUtc));
        Throws<ArgumentException>(() => ProcessExecutionLease.Acquire(directory, processId, default));
        Throws<ArgumentException>(() => ProcessExecutionLease.Acquire(directory, processId,
            startedAtUtc.ToOffset(TimeSpan.FromHours(1))));
        Throws<ArgumentException>(() => ProcessExecutionLease.Acquire("relative-lease-directory", processId, startedAtUtc));
        Throws<ArgumentException>(() => ProcessExecutionLease.Acquire(Path.GetPathRoot(directory)!, processId, startedAtUtc));
    });

    Run("abandoned lease survives GC and releases only when its process exits", () =>
    {
        string directory = Path.Combine(root, "process-lease-gc");
        using Process current = Process.GetCurrentProcess();
        DateTimeOffset startedAtUtc = current.StartTime.ToUniversalTime();
        using Process holder = Process.Start(LeaseProbeStartInfo("--lease-gc-holder", directory, current.Id, startedAtUtc))
            ?? throw new InvalidOperationException("Could not start the lease GC holder process.");
        try
        {
            Task<string?> ready = holder.StandardOutput.ReadLineAsync();
            if (!ready.Wait(15_000))
                throw new TimeoutException("The lease GC holder did not report readiness within 15 seconds.");
            Equal("LEASE_HELD_AFTER_GC", ready.Result);

            using Process contender = Process.Start(LeaseProbeStartInfo("--lease-contender", directory, current.Id, startedAtUtc))
                ?? throw new InvalidOperationException("Could not start the lease contender process.");
            if (!contender.WaitForExit(30_000))
            {
                contender.Kill(entireProcessTree: true);
                throw new TimeoutException("The lease contender process did not finish within 30 seconds.");
            }
            string contenderOutput = contender.StandardOutput.ReadToEnd();
            string contenderError = contender.StandardError.ReadToEnd();
            if (contender.ExitCode != 0)
                throw new InvalidOperationException("Lease contender failed: " + contenderOutput + contenderError);
            True(contenderOutput.Contains("PASS: cross-process contender rejected", StringComparison.Ordinal));

            holder.StandardInput.WriteLine("release");
            holder.StandardInput.Flush();
            if (!holder.WaitForExit(30_000))
                throw new TimeoutException("The lease GC holder did not exit after release.");
            string holderError = holder.StandardError.ReadToEnd();
            if (holder.ExitCode != 0)
                throw new InvalidOperationException("Lease GC holder failed: " + holderError);
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
                holder.WaitForExit();
            }
        }
        using ProcessExecutionLease afterExit = ProcessExecutionLease.Acquire(directory, current.Id, startedAtUtc);
    });

    Run("plugin schema version does not overwrite the database-global SQLite user_version", () =>
    {
        string db = Db("schema-namespace");
        using (SqliteConnection connection = Sqlite(db))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=606;";
            command.ExecuteNonQuery();
        }
        using var journal = Open(db, Owner(), new FakeLivenessProvider());
        using SqliteConnection verify = Sqlite(db);
        using SqliteCommand read = verify.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        Equal(606L, (long)read.ExecuteScalar()!);
    });

    Console.WriteLine($"PASS: {passed} plugin journal contract tests");
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch { }
}

void Run(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine($"PASS {passed:00}: {name}");
}

string Db(string name) => Path.Combine(root, name + ".sqlite");
void VerifyControlPipeRoundTrip(string command, string expectedResponse)
{
    string pipeName = "bimwright-control-roundtrip-" + Guid.NewGuid().ToString("N");
    const string authToken = "contract-control-token";
    Task<string> serverTask = Task.Run(async () =>
    {
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync();
        string? request = await PipeControlFrameProtocol.ReadFrameAsync(server);
        if (request == null || !request.Contains("\"auth\":\"" + authToken + "\"", StringComparison.Ordinal))
            throw new InvalidDataException("The control request did not carry the expected authentication token.");

        string requestedCommand = request.Contains("\"cmd\":\"job_get\"", StringComparison.Ordinal)
            ? "job_get"
            : request.Contains("\"cmd\":\"job_cancel\"", StringComparison.Ordinal)
                ? "job_cancel"
                : request.Contains("\"cmd\":\"bind_active_document\"", StringComparison.Ordinal)
                    ? "bind_active_document"
                    : request.Contains("\"cmd\":\"create_line\"", StringComparison.Ordinal)
                        ? "create_line"
                        : "unknown";
        string response = PipeControlFrameProtocol.IsAllowedControlCommand(requestedCommand)
            ? requestedCommand + ":accepted"
            : "command_not_allowed_on_control_pipe";
        await PipeControlFrameProtocol.WriteFrameAsync(server, response);
        return response;
    });

    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    client.Connect(3000);
    string requestFrame = "{\"id\":\"contract\",\"auth\":\"" + authToken
        + "\",\"cmd\":\"" + command + "\",\"params\":{}}";
    PipeControlFrameProtocol.WriteFrameAsync(client, requestFrame).GetAwaiter().GetResult();
    string? responseFrame = PipeControlFrameProtocol.ReadFrameAsync(client).GetAwaiter().GetResult();
    Equal(expectedResponse, responseFrame);
    Equal(expectedResponse, serverTask.GetAwaiter().GetResult());
}

string SourceFile(string relativePath)
{
    string[] startingPaths = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
    foreach (string startingPath in startingPaths)
    {
        for (DirectoryInfo? directory = new(startingPath); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
        }
    }

    throw new FileNotFoundException("Could not locate the source-contract file.", relativePath);
}

static PluginOwnerIdentity Owner(string generation = "acad26-test", Guid? id = null, int pid = 47321,
    DateTimeOffset? started = null) => new((id ?? Guid.NewGuid()).ToString("D"), pid,
    started ?? new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero), generation);

SqlitePluginIntentJournal Open(string path, PluginOwnerIdentity owner, IPluginOwnerLivenessProvider liveness)
{
    var journal = new SqlitePluginIntentJournal(path, owner, liveness);
    journal.Initialize(Now);
    return journal;
}

PluginJobSnapshot Claimed(SqlitePluginIntentJournal journal, PluginJobSubmission request)
{
    Equal(PluginSubmitDisposition.Created, journal.Submit(request, Now).Disposition);
    PluginClaimResult result = journal.TryClaimNext(Now);
    Equal(PluginClaimDisposition.Claimed, result.Disposition);
    return result.Job!;
}

static PluginJobSubmission Write(string id, string path = @"C:\Road\A.dwg", string parameters = "{\"x\":1}",
    DateTimeOffset? deadline = null)
{
    string alias = "alias:legacy:" + Path.GetFileNameWithoutExtension(path);
    return new PluginJobSubmission(id, "ctx", "drawing_write", parameters,
        "{\"document\":\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal) + "\",\"instance\":\"acad-a\"}",
        true, ["path:" + path, alias], false, deadline);
}

static PluginJobSubmission GlobalWrite(string id) => new(id, "ctx", "global_write", "{}",
    "{\"instance\":\"acad-a\"}", true, Array.Empty<string>(), true);

static PluginJobSubmission Read(string id) => new(id, "ctx", "drawing_read", "{}",
    "{\"document\":\"C:/Road/A.dwg\",\"instance\":\"acad-a\"}", false, Array.Empty<string>(), false);

static PluginJobSubmission QueueRead(string id, string toolName = "drawing_read", DateTimeOffset? deadline = null) =>
    new(id, "ctx", toolName, "{}", "{\"document\":\"C:/Road/A.dwg\",\"instance\":\"acad-a\"}",
        false, Array.Empty<string>(), false, deadline);

static PluginVerificationRequest Readback(PluginJobSnapshot job, long revision,
    PluginVerificationState state, string? result = null, string? error = null) => new(job.RequestId,
    job.ContextId, job.CompleteTargetJson, job.ProviderGeneration, revision, state, result,
    error is null ? null : "readback_error", error);

static PluginNoEffectTerminalRequest NoEffectRequest(PluginJobSnapshot job,
    PluginNoEffectOutcome outcome, string? contextId = null, string? completeTargetJson = null,
    string? ownerId = null, string? providerGeneration = null, string? payloadDigest = null,
    long? claimSequence = null) => new(job.RequestId, contextId ?? job.ContextId,
    completeTargetJson ?? job.CompleteTargetJson, ownerId ?? job.OwnerId,
    providerGeneration ?? job.ProviderGeneration, payloadDigest ?? job.PayloadDigest,
    claimSequence ?? job.PluginSequence, outcome, "write_rejected", "No database effects were committed.");

static PluginFinalizeVerifiedWriteFenceRequest FinalizeRequest(PluginJobSnapshot job,
    string? requestId = null, string? contextId = null, string? completeTargetJson = null,
    string? ownerId = null, string? providerGeneration = null, string? payloadDigest = null,
    string? committedResultDigest = null, long? stagedPluginSequence = null,
    long? readbackRevision = null, string? readbackDigest = null) => new(
    requestId ?? job.RequestId, contextId ?? job.ContextId,
    completeTargetJson ?? job.CompleteTargetJson, ownerId ?? job.OwnerId,
    providerGeneration ?? job.ProviderGeneration, payloadDigest ?? job.PayloadDigest,
    committedResultDigest ?? job.CommittedResultDigest!,
    stagedPluginSequence ?? job.VerificationStagedSequence!.Value,
    readbackRevision ?? job.ReadbackRevision!.Value, readbackDigest ?? job.ReadbackDigest!);

static SqliteConnection Sqlite(string path)
{
    var builder = new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 15
    };
    var connection = new SqliteConnection(builder.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000; PRAGMA synchronous=FULL;";
    command.ExecuteNonQuery();
    return connection;
}

static string PluginOwnerLockIdentityForTest(string db, PluginOwnerIdentity owner)
{
    string directory = Path.GetFullPath(db) + ".owners";
    string ownerId = Guid.Parse(owner.OwnerId).ToString("N");
    string material = string.Join("\n", ownerId, owner.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        owner.ProcessStartedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture), owner.PluginGeneration);
    string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    return Path.Combine(directory, ownerId + "." + key + ".lock");
}

static void Contains(string value, IEnumerable<string> values) => True(values.Contains(value, StringComparer.Ordinal));
static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: expected true.");
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Assertion failed: expected '{expected}', got '{actual}'.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Assertion failed: expected {typeof(T).Name}.");
}

static ProcessStartInfo LeaseProbeStartInfo(string mode, string directory, int processId, DateTimeOffset startedAtUtc)
{
    var start = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardInput = mode == "--lease-gc-holder",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(mode);
    start.ArgumentList.Add(directory);
    start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    start.ArgumentList.Add(startedAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return start;
}

static string RunDiscoveryMutexProbe(string target, int timeoutMilliseconds)
{
    var start = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--discovery-mutex-probe");
    start.ArgumentList.Add(target);
    start.ArgumentList.Add(timeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    using Process process = Process.Start(start)
        ?? throw new InvalidOperationException("Could not start the discovery mutex probe process.");
    try
    {
        if (!process.WaitForExit(10000))
            throw new TimeoutException("The discovery mutex probe process exceeded its 10-second deadline.");

        string standardOutput = process.StandardOutput.ReadToEnd().Trim();
        string standardError = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"The discovery mutex probe exited with code {process.ExitCode}. stdout: {standardOutput}; stderr: {standardError}");

        return standardOutput;
    }
    finally
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }
}

static int ProcessDiscoveryMutexProbe(string[] arguments)
{
    if (arguments.Length != 3)
    {
        Console.Error.WriteLine("Invalid discovery mutex probe arguments.");
        return 20;
    }

    try
    {
        string target = arguments[1];
        int timeoutMilliseconds = int.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
        using var mutex = new DiscoveryFileMutex(target, timeoutMilliseconds);
        Console.WriteLine("MUTEX_ACQUIRED");
        return 0;
    }
    catch (TimeoutException)
    {
        Console.WriteLine("MUTEX_TIMED_OUT");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 21;
    }
}

static int ProcessLeaseProbe(string[] arguments)
{
    if (arguments.Length != 4)
    {
        Console.Error.WriteLine("Invalid process lease probe arguments.");
        return 10;
    }

    try
    {
        string directory = arguments[1];
        int processId = int.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset startedAtUtc = new(long.Parse(arguments[3], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
        if (arguments[0] == "--lease-gc-holder")
        {
            WeakReference abandoned = AbandonLease(directory, processId, startedAtUtc);
            for (int attempt = 0; attempt < 3 && abandoned.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            if (abandoned.IsAlive)
            {
                Console.Error.WriteLine("Lease object remained rooted after forced collection.");
                return 11;
            }

            Console.WriteLine("LEASE_HELD_AFTER_GC");
            Console.Out.Flush();
            Console.ReadLine();
            return 0;
        }

        try
        {
            using ProcessExecutionLease unexpected = ProcessExecutionLease.Acquire(directory, processId, startedAtUtc);
            Console.Error.WriteLine("A second process acquired a lease while the holder was still alive.");
            return 12;
        }
        catch (IOException)
        {
            Console.WriteLine("PASS: cross-process contender rejected");
            return 0;
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 13;
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference AbandonLease(string directory, int processId, DateTimeOffset startedAtUtc)
{
    var lease = ProcessExecutionLease.Acquire(directory, processId, startedAtUtc);
    return new WeakReference(lease);
}

sealed class FakeLivenessProvider : IPluginOwnerLivenessProvider
{
    private readonly ConcurrentDictionary<string, PluginOwnerLiveness> _states = new(StringComparer.Ordinal);
    private readonly PluginOwnerLiveness _default;
    public FakeLivenessProvider(PluginOwnerLiveness defaultState = PluginOwnerLiveness.Alive) => _default = defaultState;
    public void Set(PluginOwnerIdentity owner, PluginOwnerLiveness state) => _states[Key(owner)] = state;
    public PluginOwnerLiveness GetLiveness(PluginOwnerIdentity owner) => _states.TryGetValue(Key(owner), out var state) ? state : _default;
    private static string Key(PluginOwnerIdentity owner) => Guid.Parse(owner.OwnerId).ToString("N") + ":" + owner.PluginGeneration;
}

sealed class FakeAcceptResource : IDisposable
{
    private readonly ManualResetEventSlim _closed = new();
    private int _disposed;

    public FakeAcceptResource(string name) => Name = name;

    public string Name { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public ManualResetEventSlim WaitStarted { get; } = new();

    public void WaitForConnection()
    {
        WaitStarted.Set();
        if (!_closed.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The test accept wait was not interrupted.");
        throw new ObjectDisposedException(Name);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _closed.Set();
    }
}

enum UncertainWriteJournalFault
{
    CommittedResultAfterCommit,
    CompletedTerminalAfterCommit,
    StageReadbackAfterCommit
}

sealed class UncertainWriteJournal : IPluginIntentJournal
{
    private readonly IPluginIntentJournal _inner;
    private readonly UncertainWriteJournalFault _fault;

    public UncertainWriteJournal(IPluginIntentJournal inner, UncertainWriteJournalFault fault)
    {
        _inner = inner;
        _fault = fault;
    }

    public PluginOwnerIdentity Owner => _inner.Owner;
    public string JournalScopeId => _inner.JournalScopeId;
    public string JournalPathDigest => _inner.JournalPathDigest;
    public int Initialize(DateTimeOffset nowUtc) => _inner.Initialize(nowUtc);
    public PluginSubmitResult Submit(PluginJobSubmission request, DateTimeOffset nowUtc) => _inner.Submit(request, nowUtc);
    public PluginJobSnapshot? GetSnapshot(string requestId) => _inner.GetSnapshot(requestId);
    public PluginHistoricalJobResult GetHistoricalSnapshot(string requestId) => _inner.GetHistoricalSnapshot(requestId);
    public PluginCancelResult CancelQueued(string requestId, DateTimeOffset nowUtc) => _inner.CancelQueued(requestId, nowUtc);
    public PluginClaimResult TryClaimNext(DateTimeOffset nowUtc) => _inner.TryClaimNext(nowUtc);
    public PluginTransitionResult RecordCommitted(string requestId, string resultJson, DateTimeOffset nowUtc)
    {
        PluginTransitionResult result = _inner.RecordCommitted(requestId, resultJson, nowUtc);
        if (_fault == UncertainWriteJournalFault.CommittedResultAfterCommit)
            throw new InvalidOperationException("injected lost committed-result acknowledgement");
        return result;
    }
    public PluginTransitionResult RecordTerminal(string requestId, PluginJobPhase phase, PluginEffectState effects,
        string? resultJson, string? errorCode, string? errorMessage, DateTimeOffset nowUtc)
    {
        PluginTransitionResult result = _inner.RecordTerminal(requestId, phase, effects,
            resultJson, errorCode, errorMessage, nowUtc);
        if (_fault == UncertainWriteJournalFault.CompletedTerminalAfterCommit
            && phase == PluginJobPhase.Completed && effects == PluginEffectState.Committed)
            throw new InvalidOperationException("injected lost completed-terminal acknowledgement");
        return result;
    }
    public PluginTransitionResult RecordNoEffectTerminal(PluginNoEffectTerminalRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordNoEffectTerminal(request, nowUtc);
    public PluginTransitionResult RecordVerification(PluginVerificationRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordVerification(request, nowUtc);
    public PluginTransitionResult RecordVerificationPendingRelease(PluginVerificationRequest request, DateTimeOffset nowUtc)
    {
        PluginTransitionResult result = _inner.RecordVerificationPendingRelease(request, nowUtc);
        if (_fault == UncertainWriteJournalFault.StageReadbackAfterCommit)
            throw new InvalidOperationException("injected lost readback-stage acknowledgement");
        return result;
    }
    public PluginTransitionResult FinalizeVerifiedWriteFence(
        PluginFinalizeVerifiedWriteFenceRequest request, DateTimeOffset nowUtc) =>
        _inner.FinalizeVerifiedWriteFence(request, nowUtc);
    public int RecoverLostOwners(DateTimeOffset nowUtc) => _inner.RecoverLostOwners(nowUtc);
    public void Dispose() { }
}

sealed class RejectTerminalJournal : IPluginIntentJournal
{
    private readonly IPluginIntentJournal _inner;

    public RejectTerminalJournal(IPluginIntentJournal inner) => _inner = inner;

    public int ClaimCount { get; private set; }
    public PluginOwnerIdentity Owner => _inner.Owner;
    public string JournalScopeId => _inner.JournalScopeId;
    public string JournalPathDigest => _inner.JournalPathDigest;
    public int Initialize(DateTimeOffset nowUtc) => _inner.Initialize(nowUtc);
    public PluginSubmitResult Submit(PluginJobSubmission request, DateTimeOffset nowUtc) => _inner.Submit(request, nowUtc);
    public PluginJobSnapshot? GetSnapshot(string requestId) => _inner.GetSnapshot(requestId);
    public PluginHistoricalJobResult GetHistoricalSnapshot(string requestId) => _inner.GetHistoricalSnapshot(requestId);
    public PluginCancelResult CancelQueued(string requestId, DateTimeOffset nowUtc) => _inner.CancelQueued(requestId, nowUtc);
    public PluginClaimResult TryClaimNext(DateTimeOffset nowUtc)
    {
        ClaimCount++;
        return _inner.TryClaimNext(nowUtc);
    }
    public PluginTransitionResult RecordCommitted(string requestId, string resultJson, DateTimeOffset nowUtc) =>
        _inner.RecordCommitted(requestId, resultJson, nowUtc);
    public PluginTransitionResult RecordTerminal(string requestId, PluginJobPhase phase, PluginEffectState effects,
        string? resultJson, string? errorCode, string? errorMessage, DateTimeOffset nowUtc) =>
        new(PluginTransitionDisposition.Rejected, _inner.GetSnapshot(requestId), "injected_terminal_rejection");
    public PluginTransitionResult RecordNoEffectTerminal(PluginNoEffectTerminalRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordNoEffectTerminal(request, nowUtc);
    public PluginTransitionResult RecordVerification(PluginVerificationRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordVerification(request, nowUtc);
    public PluginTransitionResult RecordVerificationPendingRelease(PluginVerificationRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordVerificationPendingRelease(request, nowUtc);
    public PluginTransitionResult FinalizeVerifiedWriteFence(PluginFinalizeVerifiedWriteFenceRequest request, DateTimeOffset nowUtc) =>
        _inner.FinalizeVerifiedWriteFence(request, nowUtc);
    public int RecoverLostOwners(DateTimeOffset nowUtc) => _inner.RecoverLostOwners(nowUtc);
    public void Dispose() { }
}

sealed class ThrowingClaimJournal : IPluginIntentJournal
{
    private readonly IPluginIntentJournal _inner;

    public ThrowingClaimJournal(IPluginIntentJournal inner) => _inner = inner;

    public int ClaimCount { get; private set; }
    public PluginOwnerIdentity Owner => _inner.Owner;
    public string JournalScopeId => _inner.JournalScopeId;
    public string JournalPathDigest => _inner.JournalPathDigest;
    public int Initialize(DateTimeOffset nowUtc) => _inner.Initialize(nowUtc);
    public PluginSubmitResult Submit(PluginJobSubmission request, DateTimeOffset nowUtc) => _inner.Submit(request, nowUtc);
    public PluginJobSnapshot? GetSnapshot(string requestId) => _inner.GetSnapshot(requestId);
    public PluginHistoricalJobResult GetHistoricalSnapshot(string requestId) => _inner.GetHistoricalSnapshot(requestId);
    public PluginCancelResult CancelQueued(string requestId, DateTimeOffset nowUtc) => _inner.CancelQueued(requestId, nowUtc);
    public PluginClaimResult TryClaimNext(DateTimeOffset nowUtc)
    {
        ClaimCount++;
        _ = _inner.TryClaimNext(nowUtc);
        throw new InvalidOperationException("injected uncertain claim acknowledgement");
    }
    public PluginTransitionResult RecordCommitted(string requestId, string resultJson, DateTimeOffset nowUtc) =>
        _inner.RecordCommitted(requestId, resultJson, nowUtc);
    public PluginTransitionResult RecordTerminal(string requestId, PluginJobPhase phase, PluginEffectState effects,
        string? resultJson, string? errorCode, string? errorMessage, DateTimeOffset nowUtc) =>
        _inner.RecordTerminal(requestId, phase, effects, resultJson, errorCode, errorMessage, nowUtc);
    public PluginTransitionResult RecordNoEffectTerminal(PluginNoEffectTerminalRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordNoEffectTerminal(request, nowUtc);
    public PluginTransitionResult RecordVerification(PluginVerificationRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordVerification(request, nowUtc);
    public PluginTransitionResult RecordVerificationPendingRelease(PluginVerificationRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordVerificationPendingRelease(request, nowUtc);
    public PluginTransitionResult FinalizeVerifiedWriteFence(PluginFinalizeVerifiedWriteFenceRequest request, DateTimeOffset nowUtc) =>
        _inner.FinalizeVerifiedWriteFence(request, nowUtc);
    public int RecoverLostOwners(DateTimeOffset nowUtc) => _inner.RecoverLostOwners(nowUtc);
    public void Dispose() { }
}

sealed class ThrowAfterTerminalJournal : IPluginIntentJournal
{
    private readonly IPluginIntentJournal _inner;

    public ThrowAfterTerminalJournal(IPluginIntentJournal inner) => _inner = inner;

    public int ClaimCount { get; private set; }
    public int TerminalWriteCount { get; private set; }
    public PluginOwnerIdentity Owner => _inner.Owner;
    public string JournalScopeId => _inner.JournalScopeId;
    public string JournalPathDigest => _inner.JournalPathDigest;
    public int Initialize(DateTimeOffset nowUtc) => _inner.Initialize(nowUtc);
    public PluginSubmitResult Submit(PluginJobSubmission request, DateTimeOffset nowUtc) => _inner.Submit(request, nowUtc);
    public PluginJobSnapshot? GetSnapshot(string requestId) => _inner.GetSnapshot(requestId);
    public PluginHistoricalJobResult GetHistoricalSnapshot(string requestId) => _inner.GetHistoricalSnapshot(requestId);
    public PluginCancelResult CancelQueued(string requestId, DateTimeOffset nowUtc) => _inner.CancelQueued(requestId, nowUtc);
    public PluginClaimResult TryClaimNext(DateTimeOffset nowUtc)
    {
        ClaimCount++;
        return _inner.TryClaimNext(nowUtc);
    }
    public PluginTransitionResult RecordCommitted(string requestId, string resultJson, DateTimeOffset nowUtc) =>
        _inner.RecordCommitted(requestId, resultJson, nowUtc);
    public PluginTransitionResult RecordTerminal(string requestId, PluginJobPhase phase, PluginEffectState effects,
        string? resultJson, string? errorCode, string? errorMessage, DateTimeOffset nowUtc)
    {
        TerminalWriteCount++;
        PluginTransitionResult transition = _inner.RecordTerminal(requestId, phase, effects,
            resultJson, errorCode, errorMessage, nowUtc);
        if (transition.Disposition is PluginTransitionDisposition.Applied or PluginTransitionDisposition.Duplicate)
            throw new InvalidOperationException("injected terminal acknowledgement failure after commit");
        return transition;
    }
    public PluginTransitionResult RecordNoEffectTerminal(PluginNoEffectTerminalRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordNoEffectTerminal(request, nowUtc);
    public PluginTransitionResult RecordVerification(PluginVerificationRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordVerification(request, nowUtc);
    public PluginTransitionResult RecordVerificationPendingRelease(PluginVerificationRequest request, DateTimeOffset nowUtc) =>
        _inner.RecordVerificationPendingRelease(request, nowUtc);
    public PluginTransitionResult FinalizeVerifiedWriteFence(PluginFinalizeVerifiedWriteFenceRequest request, DateTimeOffset nowUtc) =>
        _inner.FinalizeVerifiedWriteFence(request, nowUtc);
    public int RecoverLostOwners(DateTimeOffset nowUtc) => _inner.RecoverLostOwners(nowUtc);
    public void Dispose() { }
}
