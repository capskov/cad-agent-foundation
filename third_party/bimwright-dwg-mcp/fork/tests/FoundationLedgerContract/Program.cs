using System.Collections.Concurrent;
using System.Reflection;
using Bimwright.Dwg.Server.Execution;
using Microsoft.Data.Sqlite;

int passed = 0;
string tempRoot = Path.Combine(Path.GetTempPath(), "foundation-ledger-contract-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);

try
{
    Run("canonical digest and idempotent request identity", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "dedupe.sqlite"));
        ReserveResult first = ledger.Value.Reserve(Write("same-id", "{\"b\":2,\"a\":{\"y\":true,\"x\":1}}",
            target: "{\"document\":\"C:/Road/A.dwg\",\"instance_guid\":\"cad-a\"}",
            riskKeys: [@"path:C:\Road\A.dwg", "alias:legacy:road-a"]));
        ReserveResult repeat = ledger.Value.Reserve(Write("same-id", " { \"a\": { \"x\": 1, \"y\": true }, \"b\": 2 } ",
            target: "{\"instance_guid\":\"cad-a\",\"document\":\"C:/Road/A.dwg\"}",
            riskKeys: ["alias:LEGACY:ROAD-A", "C:/ROAD/A.DWG"]));
        Equal(ReserveDisposition.Created, first.Disposition);
        Equal(ReserveDisposition.Existing, repeat.Disposition);
        Equal(first.Job!.PayloadDigest, repeat.Job!.PayloadDigest);
        Equal(1L, CountJobs(ledger.Value.DatabasePath));
    });

    Run("request id digest covers context, tool, target, and provider generation", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "id-conflict.sqlite"));
        Equal(ReserveDisposition.Created, ledger.Value.Reserve(Read("global")).Disposition);
        Equal(ReserveDisposition.RequestIdConflict, ledger.Value.Reserve(Read("global", "{\"x\":2}")).Disposition);
        Equal(ReserveDisposition.RequestIdConflict, ledger.Value.Reserve(Read("global", context: "other")).Disposition);
        Equal(ReserveDisposition.RequestIdConflict, ledger.Value.Reserve(Read("global", tool: "different_tool")).Disposition);
        Equal(ReserveDisposition.RequestIdConflict, ledger.Value.Reserve(Read("global", target: "{\"pid\":99,\"path\":\"C:/other.dwg\"}")).Disposition);
        Equal(ReserveDisposition.RequestIdConflict, ledger.Value.Reserve(Read("global", generation: "plugin-2")).Disposition);
        Equal(1L, CountJobs(ledger.Value.DatabasePath));
    });

    Run("concurrent writes conflict while independent reads remain available", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "fence-race.sqlite"));
        using var start = new Barrier(3);
        var results = new ConcurrentBag<ReserveResult>();
        Task a = Task.Run(() => { start.SignalAndWait(); results.Add(ledger.Value.Reserve(Write("write-a"))); });
        Task b = Task.Run(() => { start.SignalAndWait(); results.Add(ledger.Value.Reserve(Write("write-b"))); });
        start.SignalAndWait();
        Task.WaitAll(a, b);
        Equal(1, results.Count(r => r.Disposition == ReserveDisposition.Created));
        Equal(1, results.Count(r => r.Disposition == ReserveDisposition.FenceConflict));
        Equal(ReserveDisposition.Created, ledger.Value.Reserve(Read("read-during-write")).Disposition);
    });

    Run("authoritative queued cancel releases fence and rejects a late run", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "queued-cancel.sqlite"));
        ledger.Value.Reserve(Write("cancel-queued"));
        AdvanceResult cancelled = ledger.Value.Advance(Snapshot("cancel-queued", 1, JobPhase.Cancelled, EffectState.None,
            observation: ObservationState.AuthoritativePluginSnapshot, result: "{\"cancelled\":true}"));
        Equal(AdvanceDisposition.Applied, cancelled.Disposition);
        Equal(true, cancelled.Job!.WriteFenceReleased);
        Equal(0, cancelled.Job.HeldRiskKeys.Count);
        Equal(AdvanceDisposition.RejectedTransition,
            ledger.Value.Advance(Snapshot("cancel-queued", 2, JobPhase.Running, EffectState.Unknown)).Disposition);
        Equal(ReserveDisposition.Created, ledger.Value.Reserve(Write("after-cancel")).Disposition);
    });

    Run("running cancellation remains a request until plugin confirms a terminal result", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "running-cancel.sqlite"));
        ledger.Value.Reserve(Write("cancel-running"));
        Equal(AdvanceDisposition.Applied, ledger.Value.Advance(Snapshot("cancel-running", 1, JobPhase.Running, EffectState.Unknown)).Disposition);
        LedgerJob requested = ledger.Value.Advance(Snapshot("cancel-running", 2, JobPhase.CancellationRequested, EffectState.Unknown)).Job!;
        Equal(JobPhase.CancellationRequested, requested.Phase);
        Equal(false, requested.WriteFenceReleased);
        Equal(AdvanceDisposition.RejectedTransition,
            ledger.Value.Advance(Snapshot("cancel-running", 3, JobPhase.Cancelled, EffectState.None,
                observation: ObservationState.AuthoritativePluginSnapshot)).Disposition);
    });

    Run("server recovery preserves plugin state, and exact same-generation snapshot restores availability", () =>
    {
        string database = Path.Combine(tempRoot, "recovery.sqlite");
        using (var first = new TestLedger(database))
        {
            first.Value.Reserve(Write("recover-running"));
            first.Value.Advance(Snapshot("recover-running", 4, JobPhase.Running, EffectState.Unknown));
        }
        using var reopened = new TestLedger(database);
        Equal(1, reopened.Value.RecoverUnfinished());
        LedgerJob recovered = reopened.Value.Get("recover-running")!;
        Equal(JobPhase.Running, recovered.Phase);
        Equal(EffectState.Unknown, recovered.Effects);
        Equal(4L, recovered.PluginSequence);
        Equal(ObservationState.Recovery, recovered.Observation);
        Equal(true, recovered.ServerUnavailable);
        Equal(ReserveDisposition.FenceConflict, reopened.Value.Reserve(Write("still-fenced")).Disposition);
        Equal(AdvanceDisposition.Duplicate, reopened.Value.Advance(Snapshot("recover-running", 4, JobPhase.Running, EffectState.Unknown)).Disposition);
        Equal(false, reopened.Value.Get("recover-running")!.ServerUnavailable);
        Equal(ObservationState.PluginSnapshot, reopened.Value.Get("recover-running")!.Observation);
        Equal(AdvanceDisposition.RejectedTransition,
            reopened.Value.Advance(Snapshot("recover-running", 4, JobPhase.Completed, EffectState.Committed)).Disposition);
        Equal(0, reopened.Value.RecoverUnfinished());
    });

    Run("separate readback revision verifies committed completion without moving plugin sequence", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "verified-release.sqlite"));
        ledger.Value.Reserve(Write("verified"));
        ledger.Value.Advance(Snapshot("verified", 1, JobPhase.Running, EffectState.Unknown));
        LedgerJob committed = ledger.Value.Advance(Snapshot("verified", 2, JobPhase.Completed, EffectState.Committed)).Job!;
        Equal(false, committed.WriteFenceReleased);
        Equal(ReserveDisposition.FenceConflict, ledger.Value.Reserve(Write("blocked-before-readback")).Disposition);
        VerificationResult result = ledger.Value.RecordVerification(Readback("verified", 1, VerificationState.Passed,
            result: "{\"saved\":true}"));
        Equal(VerificationDisposition.Applied, result.Disposition);
        LedgerJob verified = result.Job!;
        Equal(2L, verified.PluginSequence);
        Equal(1L, verified.ReadbackRevision);
        Equal(JobPhase.Completed, verified.Phase);
        Equal(EffectState.Committed, verified.Effects);
        Equal(VerificationState.Passed, verified.Verification);
        Equal(true, verified.WriteFenceReleased);
        Equal(ReserveDisposition.Created, ledger.Value.Reserve(Write("after-readback")).Disposition);
        Equal(VerificationDisposition.Duplicate,
            ledger.Value.RecordVerification(Readback("verified", 1, VerificationState.Passed, result: "{\"saved\":true}")).Disposition);
        Equal(VerificationDisposition.StaleRevision,
            ledger.Value.RecordVerification(Readback("verified", 0, VerificationState.Passed)).Disposition);
    });

    Run("unknown writes cannot be cleared by readback or another provider generation", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "unknown.sqlite"));
        ledger.Value.Reserve(Write("unknown-write"));
        AdvanceResult unknown = ledger.Value.Advance(Snapshot("unknown-write", 1, JobPhase.Unknown, EffectState.Unknown,
            observation: ObservationState.AuthoritativePluginSnapshot));
        Equal(AdvanceDisposition.Applied, unknown.Disposition);
        Equal(VerificationDisposition.RejectedState,
            ledger.Value.RecordVerification(Readback("unknown-write", 1, VerificationState.Passed)).Disposition);
        Equal(AdvanceDisposition.RejectedTransition,
            ledger.Value.Advance(Snapshot("unknown-write", 2, JobPhase.Completed, EffectState.Committed, generation: "plugin-2",
                observation: ObservationState.AuthoritativePluginSnapshot)).Disposition);
        Equal(false, ledger.Value.Get("unknown-write")!.WriteFenceReleased);
        Equal(2, ledger.Value.Get("unknown-write")!.HeldRiskKeys.Count);
        Equal(AdvanceDisposition.Applied,
            ledger.Value.Advance(Snapshot("unknown-write", 2, JobPhase.Completed, EffectState.Committed,
                observation: ObservationState.AuthoritativePluginSnapshot)).Disposition);
        Equal(true, ledger.Value.RecordVerification(Readback("unknown-write", 1, VerificationState.Passed)).Job!.WriteFenceReleased);
    });

    Run("plugin sequence equal accepts only byte-equivalent snapshot content", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "sequence.sqlite"));
        ledger.Value.Reserve(Write("sequence"));
        AdvanceRequest first = Snapshot("sequence", 4, JobPhase.Running, EffectState.Unknown);
        Equal(AdvanceDisposition.Applied, ledger.Value.Advance(first).Disposition);
        Equal(AdvanceDisposition.Duplicate, ledger.Value.Advance(first).Disposition);
        Equal(AdvanceDisposition.RejectedTransition,
            ledger.Value.Advance(Snapshot("sequence", 4, JobPhase.Running, EffectState.Unknown, result: "{\"changed\":true}")).Disposition);
        Equal(AdvanceDisposition.StaleSequence,
            ledger.Value.Advance(Snapshot("sequence", 3, JobPhase.Running, EffectState.Unknown)).Disposition);
        Equal(JobPhase.Running, ledger.Value.Get("sequence")!.Phase);
        Throws<ArgumentException>(() => ledger.Value.Advance(Snapshot("sequence", 5, JobPhase.Running, EffectState.Unknown,
            observation: ObservationState.IndependentReadback)));
        Throws<ArgumentException>(() => ledger.Value.Advance(Snapshot("sequence", 5, JobPhase.Completed, EffectState.Committed,
            verification: VerificationState.Passed)));
    });

    Run("higher plugin sequence cannot rewrite a terminal completed write", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "terminal-immutable.sqlite"));
        ledger.Value.Reserve(Write("terminal-write"));
        ledger.Value.Advance(Snapshot("terminal-write", 1, JobPhase.Running, EffectState.Unknown));
        AdvanceRequest completed = Snapshot("terminal-write", 2, JobPhase.Completed, EffectState.Committed,
            result: "{\"save_id\":\"original\"}");
        Equal(AdvanceDisposition.Applied, ledger.Value.Advance(completed).Disposition);
        Equal(AdvanceDisposition.Duplicate, ledger.Value.Advance(completed).Disposition);
        AdvanceResult rewrite = ledger.Value.Advance(Snapshot("terminal-write", 3, JobPhase.Completed, EffectState.Committed,
            result: "{\"save_id\":\"rewritten\"}"));
        Equal(AdvanceDisposition.RejectedTransition, rewrite.Disposition);
        Equal("{\"save_id\":\"original\"}", rewrite.Job!.ResultJson);
        Equal(false, rewrite.Job.WriteFenceReleased);
        Equal(2, rewrite.Job.HeldRiskKeys.Count);
    });

    Run("failed committed effects keep fence; terminal authoritative no-effect may release", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "failed-write.sqlite"));
        ledger.Value.Reserve(Write("failed-committed"));
        ledger.Value.Advance(Snapshot("failed-committed", 1, JobPhase.Running, EffectState.Unknown));
        LedgerJob failed = ledger.Value.Advance(Snapshot("failed-committed", 2, JobPhase.Failed, EffectState.Committed)).Job!;
        Equal(false, failed.WriteFenceReleased);
        Equal(ReserveDisposition.FenceConflict, ledger.Value.Reserve(Write("blocked-after-failure")).Disposition);

        ledger.Value.Reserve(Write("failed-no-effects", target: "{\"document\":\"C:/Road/C.dwg\"}", riskKeys: [@"path:C:\Road\C.dwg"]));
        AdvanceResult noEffects = ledger.Value.Advance(Snapshot("failed-no-effects", 1, JobPhase.Failed, EffectState.None,
            observation: ObservationState.AuthoritativePluginSnapshot, error: "command_failed"));
        Equal(true, noEffects.Job!.WriteFenceReleased);
    });

    Run("failed independent readback keeps the committed write fence", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "failed-readback.sqlite"));
        ledger.Value.Reserve(Write("failed-readback"));
        ledger.Value.Advance(Snapshot("failed-readback", 1, JobPhase.Running, EffectState.Unknown));
        LedgerJob completed = ledger.Value.Advance(Snapshot("failed-readback", 2, JobPhase.Completed, EffectState.Committed)).Job!;
        VerificationResult failed = ledger.Value.RecordVerification(Readback("failed-readback", 1, VerificationState.Failed,
            result: "{\"matched\":false}"));
        Equal(VerificationDisposition.Applied, failed.Disposition);
        Equal(completed.PluginSequence, failed.Job!.PluginSequence);
        Equal(completed.Phase, failed.Job.Phase);
        Equal(completed.Effects, failed.Job.Effects);
        Equal(false, failed.Job.WriteFenceReleased);
        Equal(ReserveDisposition.FenceConflict, ledger.Value.Reserve(Write("still-blocked")).Disposition);
    });

    Run("verification uses an optional plugin-sequence compare-and-set", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "verification-sequence-cas.sqlite"));
        ledger.Value.Reserve(Write("verification-sequence"));
        ledger.Value.Advance(Snapshot("verification-sequence", 1, JobPhase.Running, EffectState.Unknown));
        ledger.Value.Advance(Snapshot("verification-sequence", 2, JobPhase.Completed, EffectState.Committed));

        VerificationResult stale = ledger.Value.RecordVerification(Readback("verification-sequence", 1,
            VerificationState.Passed, result: "{\"matched\":true}", expectedPluginSequence: 3));
        Equal(VerificationDisposition.RejectedState, stale.Disposition);
        Equal("expected_plugin_sequence_mismatch", stale.Reason);
        Equal(null, stale.Job!.ReadbackRevision);
        Equal(false, stale.Job.WriteFenceReleased);

        VerificationRequest stagedFailure = Readback("verification-sequence", 1,
            VerificationState.Failed, result: "{\"matched\":false}", expectedPluginSequence: 2);
        Equal(VerificationDisposition.Applied, ledger.Value.RecordVerification(stagedFailure).Disposition);
        Equal(false, ledger.Value.Get("verification-sequence")!.WriteFenceReleased);

        VerificationResult changedCas = ledger.Value.RecordVerification(Readback("verification-sequence", 1,
            VerificationState.Failed, result: "{\"matched\":false}"));
        Equal(VerificationDisposition.RejectedState, changedCas.Disposition);
        Equal("same_revision_snapshot_conflict", changedCas.Reason);
        Equal(VerificationDisposition.Duplicate, ledger.Value.RecordVerification(stagedFailure).Disposition);

        VerificationResult passed = ledger.Value.RecordVerification(Readback("verification-sequence", 2,
            VerificationState.Passed, result: "{\"matched\":true}", expectedPluginSequence: 2));
        Equal(VerificationDisposition.Applied, passed.Disposition);
        Equal(true, passed.Job!.WriteFenceReleased);
    });

    Run("pending-release verification keeps the risk fence until exact plugin finalization", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "two-party-finalize.sqlite"));
        ledger.Value.Reserve(Write("two-party"));
        ledger.Value.Advance(Snapshot("two-party", 1, JobPhase.Running, EffectState.Unknown));
        LedgerJob committed = ledger.Value.Advance(Snapshot("two-party", 2, JobPhase.Completed,
            EffectState.Committed, result: "{\"created_handles\":[\"A1\"]}")).Job!;

        VerificationRequest readback = Readback("two-party", 4, VerificationState.Passed,
            result: "{\"matched\":true}", expectedPluginSequence: 2);
        VerificationResult staged = ledger.Value.RecordVerificationPendingRelease(readback);
        Equal(VerificationDisposition.Applied, staged.Disposition);
        Equal(false, staged.Job!.WriteFenceReleased);
        Equal(2L, staged.Job.VerificationStagedPluginSequence);
        Equal(ReserveDisposition.FenceConflict, ledger.Value.Reserve(Write("blocked-before-finalize")).Disposition);

        string committedDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(committed.ResultJson!)));
        var wrongReadback = new FinalizeVerificationRequest("two-party", "road08-context",
            "{\"app_pid\":11096,\"acad_year\":2026,\"document\":\"C:/Road/08.dwg\",\"instance_guid\":\"cad-a\"}", "plugin-1",
            StagedPluginSequence: 2, FinalizedPluginSequence: 3, ReadbackRevision: 4,
            ReadbackDigest: new string('A', 64), CommittedResultDigest: committedDigest);
        Equal(VerificationDisposition.RejectedState,
            ledger.Value.FinalizeVerifiedWriteFence(wrongReadback).Disposition);
        Equal(false, ledger.Value.Get("two-party")!.WriteFenceReleased);

        var finalize = wrongReadback with { ReadbackDigest = staged.Job.ReadbackDigest! };
        VerificationResult finalized = ledger.Value.FinalizeVerifiedWriteFence(finalize);
        Equal(VerificationDisposition.Applied, finalized.Disposition);
        Equal(true, finalized.Job!.WriteFenceReleased);
        Equal(3L, finalized.Job.PluginSequence);
        Equal(0, finalized.Job.HeldRiskKeys.Count);
        Equal(ReserveDisposition.Created, ledger.Value.Reserve(Write("after-finalize")).Disposition);
        Equal(VerificationDisposition.Duplicate, ledger.Value.FinalizeVerifiedWriteFence(finalize).Disposition);
        Throws<ArgumentOutOfRangeException>(() =>
            ledger.Value.FinalizeVerifiedWriteFence(finalize with { FinalizedPluginSequence = 4 }));
    });

    Run("global fence conflicts in both directions with every drawing write", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "global-fence.sqlite"));
        ledger.Value.Reserve(Write("ordinary-first"));
        Equal(ReserveDisposition.FenceConflict, ledger.Value.Reserve(Write("global-blocked", globalFence: true)).Disposition);

        using var other = new TestLedger(Path.Combine(tempRoot, "global-only.sqlite"));
        ReserveResult global = other.Value.Reserve(Write("global", globalFence: true));
        Equal(ReserveDisposition.Created, global.Disposition);
        Equal(true, global.Job!.GlobalFence);
        Equal(ReserveDisposition.FenceConflict, other.Value.Reserve(Write("ordinary-blocked",
            target: "{\"document\":\"C:/Elsewhere/B.dwg\"}", riskKeys: [@"path:C:\Elsewhere\B.dwg"])).Disposition);
        Equal(ReserveDisposition.Created, other.Value.Reserve(Read("global-read")).Disposition);
    });

    Run("database reopen retains task result and stable enum strings", () =>
    {
        string database = Path.Combine(tempRoot, "persist.sqlite");
        using (var first = new TestLedger(database))
        {
            first.Value.Reserve(Read("persist"));
            first.Value.Advance(Snapshot("persist", 11, JobPhase.Failed, EffectState.None,
                verification: VerificationState.Failed, observation: ObservationState.AuthoritativePluginSnapshot,
                result: "{\"partial\":false}", error: "E_TEST", errorMessage: "known failure"));
        }
        using var reopened = new TestLedger(database);
        LedgerJob result = reopened.Value.Get("persist")!;
        Equal(JobPhase.Failed, result.Phase);
        Equal(EffectState.None, result.Effects);
        Equal(11L, result.PluginSequence);
        Equal("{\"partial\":false}", result.ResultJson);
        Equal("failed", ReadStoredValue(database, "phase", "persist"));
        Equal("authoritative_plugin_snapshot", ReadStoredValue(database, "observation", "persist"));
        Equal("\"CancellationRequested\"", System.Text.Json.JsonSerializer.Serialize(JobPhase.CancellationRequested));
    });

    Run("read-only jobs can complete with no drawing effects", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "read-complete.sqlite"));
        ledger.Value.Reserve(Read("read-complete"));
        LedgerJob completed = ledger.Value.Advance(Snapshot("read-complete", 1, JobPhase.Completed, EffectState.None,
            observation: ObservationState.AuthoritativePluginSnapshot)).Job!;
        Equal(JobPhase.Completed, completed.Phase);
        Equal(EffectState.None, completed.Effects);
        Equal(false, completed.IsWrite);
    });

    Run("Windows paths normalize safely and ambiguous aliases fail closed", () =>
    {
        Equal(RequestLedger.NormalizeRiskKey(@"path:C:\Road\x\..\A.dwg"), RequestLedger.NormalizeRiskKey("C:/ROAD/A.DWG"));
        Equal(RequestLedger.NormalizeRiskKey("alias:old-road"), RequestLedger.NormalizeRiskKey("ALIAS:OLD-ROAD"));
        Throws<ArgumentException>(() => RequestLedger.NormalizeRiskKey("legacy:road"));
        Throws<ArgumentException>(() => RequestLedger.NormalizeRiskKey(@"\\?\C:\Road\A.dwg"));
        Throws<ArgumentException>(() => RequestLedger.NormalizeRiskKey(@"\\.\C:\Road\A.dwg"));
        Throws<ArgumentException>(() => RequestLedger.NormalizeRiskKey(@"C:\Road\A.dwg "));
        Throws<ArgumentException>(() => RequestLedger.NormalizeRiskKey(@"C:\Road.\A.dwg"));
        Throws<ArgumentException>(() => RequestLedger.NormalizeRiskKey(@"C:\Road\file.dwg:stream"));
        Throws<ArgumentException>(() => RequestLedger.NormalizeRiskKey(@"C:\CON\file.dwg"));
        Equal("ALIAS:ONLY-THIS", RequestLedger.NormalizeRiskKey("alias:only-this"));
        using var ledger = new TestLedger(Path.Combine(tempRoot, "alias-only.sqlite"));
        Throws<ArgumentException>(() => ledger.Value.Reserve(Write("alias-only", riskKeys: ["alias:only-this"])));
        Throws<ArgumentException>(() => ledger.Value.Reserve(Write("decoy-path", target: "{\"document\":\"C:/Road/Actual.dwg\"}",
            riskKeys: [@"path:C:\Road\Decoy.dwg", "alias:Actual"])));
    });

    Run("identifier and provider-generation contract is consistent and opaque", () =>
    {
        using var ledger = new TestLedger(Path.Combine(tempRoot, "identifiers.sqlite"));
        string max = new string('a', 128);
        Equal(ReserveDisposition.Created, ledger.Value.Reserve(Read(max)).Disposition);
        Throws<ArgumentException>(() => ledger.Value.Reserve(Read(new string('a', 129))));
        Throws<ArgumentException>(() => ledger.Value.Reserve(Read(" leading")));
        Throws<ArgumentException>(() => ledger.Value.Reserve(Read("trailing ")));
        Throws<ArgumentException>(() => ledger.Value.Reserve(Read("bad\nid")));
        Throws<ArgumentException>(() => ledger.Value.Reserve(Read("x", context: new string('c', 129))));
        Throws<ArgumentException>(() => ledger.Value.Reserve(Read("x", tool: new string('t', 129))));
        Throws<ArgumentException>(() => ledger.Value.Reserve(Read("x", generation: "bad generation")));
        Throws<ArgumentException>(() => ledger.Value.Reserve(Read("x", generation: "")));
        Throws<ArgumentException>(() => ledger.Value.Get(" trailing"));
        Throws<ArgumentException>(() => ledger.Value.Advance(Snapshot("x", 1, JobPhase.Running, EffectState.Unknown, generation: "")));
    });

    Run("one ledger owner per database and FULL synchronous on every opened connection", () =>
    {
        string database = Path.Combine(tempRoot, "owner.sqlite");
        using var first = new TestLedger(database);
        Throws<InvalidOperationException>(() => new RequestLedger(database));
        var method = typeof(RequestLedger).GetMethod("OpenConnection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var connection = (SqliteConnection)method.Invoke(first.Value, null)!;
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous;";
        Equal(2L, (long)command.ExecuteScalar()!);
        first.Value.Reserve(Read("owner-read"));
        Throws<InvalidOperationException>(() => first.Value.RecoverUnfinished());
        first.Dispose();
        Throws<ObjectDisposedException>(() => first.Value.Get("owner-read"));
        using var nextOwner = new TestLedger(database);
        Equal(JobPhase.Queued, nextOwner.Value.Get("owner-read")!.Phase);
    });

    Console.WriteLine($"CONTRACT_RESULT: PASS ({passed} scenarios)");
}
finally
{
    string fullTemp = Path.GetFullPath(tempRoot);
    string fullSystemTemp = Path.GetFullPath(Path.GetTempPath());
    if (fullTemp.StartsWith(fullSystemTemp, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(fullTemp).StartsWith("foundation-ledger-contract-", StringComparison.Ordinal))
        Directory.Delete(fullTemp, recursive: true);
}

void Run(string name, Action scenario)
{
    scenario();
    passed++;
    Console.WriteLine("PASS " + name);
}

static ReserveRequest Write(string id, string parameters = "{\"value\":1}", string context = "road08-context",
    string tool = "dwg_create_mtext", string target = "{\"app_pid\":11096,\"acad_year\":2026,\"document\":\"C:/Road/08.dwg\",\"instance_guid\":\"cad-a\"}",
    IReadOnlyCollection<string>? riskKeys = null, bool globalFence = false, string generation = "plugin-1") =>
    new(id, context, tool, parameters, target, true, generation,
        globalFence ? null : riskKeys ?? [@"path:C:\Road\08.dwg", "alias:legacy:road08"], globalFence);

static ReserveRequest Read(string id, string parameters = "{\"value\":1}", string context = "road08-context",
    string tool = "dwg_get_entity_properties",
    string target = "{\"app_pid\":11096,\"acad_year\":2026,\"document\":\"C:/Road/08.dwg\",\"instance_guid\":\"cad-a\"}",
    string generation = "plugin-1") =>
    new(id, context, tool, parameters, target, false, generation);

static AdvanceRequest Snapshot(string id, long sequence, JobPhase phase, EffectState effects,
    VerificationState verification = VerificationState.NotChecked,
    ObservationState observation = ObservationState.PluginSnapshot,
    string? result = null, string? error = null, string? errorMessage = null, string generation = "plugin-1") =>
    new(id, generation, sequence, phase, effects, verification, observation, result, error, errorMessage, DateTimeOffset.UtcNow);

static VerificationRequest Readback(string id, long revision, VerificationState state, string? result = null,
    string context = "road08-context",
    string target = "{\"app_pid\":11096,\"acad_year\":2026,\"document\":\"C:/Road/08.dwg\",\"instance_guid\":\"cad-a\"}",
    string generation = "plugin-1", long? expectedPluginSequence = null) =>
    new(id, context, target, generation, revision, state, result, ObservedAt: DateTimeOffset.UtcNow,
        ExpectedPluginSequence: expectedPluginSequence);

static long CountJobs(string path)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM request_jobs;";
    return (long)command.ExecuteScalar()!;
}

static string ReadStoredValue(string path, string column, string id)
{
    if (column is not ("phase" or "observation" or "effects" or "verification")) throw new ArgumentOutOfRangeException(nameof(column));
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = $"SELECT {column} FROM request_jobs WHERE request_id=$id;";
    command.Parameters.AddWithValue("$id", id);
    return (string)command.ExecuteScalar()!;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class TestLedger : IDisposable
{
    public TestLedger(string path) => Value = new RequestLedger(path);
    public RequestLedger Value { get; }
    public void Dispose() => Value.Dispose();
}
