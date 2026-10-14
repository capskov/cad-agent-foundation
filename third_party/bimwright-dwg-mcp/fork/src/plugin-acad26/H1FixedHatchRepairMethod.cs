using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Bimwright.Dwg.Shared.Execution;
using Bimwright.Dwg.Plugin.Cad;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
    // No MCP/CommandMethod registration. The future governed H1 writer must supply its own exact binding/journal gates.
    internal static class H1FixedHatchRepairMethod
    {
        private static JObject ReadSceneFacts(Document document, Transaction tx)
        {
            JObject facts = H1FixedHatchPreflightHandler.ReadFacts(document, tx);
            var handles = new JArray(); var unrelated = new JObject();
            var space = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                if (handles.Count == 5) break; // Five entries already reject the frozen four-entity scene.
                var entity = (Entity)tx.GetObject(id, OpenMode.ForRead); string handle = entity.Handle.ToString(); handles.Add(handle);
                if (handle != "7A" && handle != "7B") unrelated[handle] = JObject.FromObject(CadEntityProperties.Describe(entity, tx, true));
            }
            facts["model_handles"] = new JArray(handles.Values<string>().OrderBy(value => Convert.ToUInt64(value, 16)));
            facts["unrelated_entities"] = unrelated;
            return facts;
        }

        private static bool MatchesScene(JObject before, JObject after) => H1FixedHatchWriteContract.MatchesCompleteScene(before, after);

        internal static JObject Execute(Document document, Action<string> checkpoint = null)
        {
            JObject before = null, after = null; Transaction tx = null;
            bool writeOpened = false, commitInvoked = false, commitReturned = false, objectRecordingRestored = false;
            string phase = "document_lock", error = null, outcome = "unknown", verification = "not_attempted";
            using (document.LockDocument())
            {
                try
                {
                    phase = "preflight"; tx = document.Database.TransactionManager.StartTransaction();
                    before = ReadSceneFacts(document, tx);
                    JObject preflight = H1FixedHatchPreflightContract.BuildEvidence(before);
                    if (preflight.Value<string>("candidate_status") != "candidate" || !JToken.DeepEquals(before["model_handles"], new JArray("78", "79", "7A", "7B")))
                    { tx.Abort(); outcome = "rejected_before_write"; error = "fixed_preflight_blocked"; }
                    else
                    {
                        if (!CadHandleResolver.TryResolve(document.Database, "7A", out ObjectId boundaryId, out _)
                            || !CadHandleResolver.TryResolve(document.Database, "7B", out ObjectId hatchId, out _)) throw new InvalidOperationException("fixed_handles_unavailable");
                        var boundary = (Polyline)tx.GetObject(boundaryId, OpenMode.ForRead);
                        var hatch = (Hatch)tx.GetObject(hatchId, OpenMode.ForRead);
                        phase = "restore_object_recording"; writeOpened = true;
                        boundary.UpgradeOpen(); hatch.UpgradeOpen(); boundary.DisableUndoRecording(false); hatch.DisableUndoRecording(false); objectRecordingRestored = true;
                        phase = "replace_loop"; hatch.RemoveLoopAt(0); hatch.Associative = true;
                        hatch.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection { boundaryId });
                        phase = "after_association"; checkpoint?.Invoke(phase);
                        phase = "evaluate"; hatch.EvaluateHatch(true);
                        phase = "precommit_readback"; after = ReadSceneFacts(document, tx);
                        if (!MatchesScene(before, after)) throw new InvalidOperationException("fixed_precommit_readback_failed");
                        phase = "commit"; commitInvoked = true; tx.Commit(); commitReturned = true;
                        tx.Dispose(); tx = null; phase = "after_commit"; checkpoint?.Invoke(phase);
                        phase = "postcommit_readback";
                        using (var read = document.Database.TransactionManager.StartTransaction())
                        { after = ReadSceneFacts(document, read); read.Abort(); }
                        verification = MatchesScene(before, after) ? "passed" : "failed";
                        outcome = verification == "passed" ? "committed_verified" : "committed_readback_failed";
                    }
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name;
                    if (commitReturned) { outcome = "committed_readback_unknown"; verification = "unknown"; }
                    else if (commitInvoked) { outcome = "commit_outcome_unknown"; verification = "unknown"; }
                    else
                    {
                        bool aborted = false;
                        try { tx?.Abort(); aborted = true; } catch { }
                        try { tx?.Dispose(); } catch { } tx = null;
                        try
                        {
                            using var read = document.Database.TransactionManager.StartTransaction();
                            after = ReadSceneFacts(document, read); read.Abort();
                            bool equal = aborted && before != null && JToken.DeepEquals(before, after)
                                && after["read_errors"] is JArray errors && errors.Count == 0;
                            outcome = equal ? "aborted_verified" : "rollback_outcome_unknown"; verification = equal ? "passed" : "unknown";
                        }
                        catch { outcome = "rollback_outcome_unknown"; verification = "unknown"; }
                    }
                }
                finally { try { tx?.Dispose(); } catch { } }
            }
            return new JObject
            {
                ["schema_version"] = 1, ["plan_id"] = H1FixedHatchRepairReadbackContract.PlanId,
                ["boundary_handle"] = "7A", ["hatch_handle"] = "7B", ["outcome"] = outcome,
                ["write_opened"] = writeOpened, ["commit_invoked"] = commitInvoked, ["commit_returned"] = commitReturned,
                ["object_undo_recording_restored"] = objectRecordingRestored, ["verification"] = verification,
                ["failure_phase"] = error == null ? JValue.CreateNull() : new JValue(phase), ["error_code"] = error == null ? JValue.CreateNull() : new JValue(error),
                ["effects"] = commitReturned ? "committed" : outcome == "aborted_verified" || outcome == "rejected_before_write" ? "none" : "unknown",
                ["journal_finalized"] = false, ["fence_release_authorized"] = false,
                ["before"] = before, ["after"] = after
            };
        }
    }
}
