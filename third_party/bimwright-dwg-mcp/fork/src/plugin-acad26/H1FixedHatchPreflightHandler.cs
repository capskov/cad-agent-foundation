using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Bimwright.Dwg.Plugin.Cad;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
    internal sealed class H1FixedHatchPreflightHandler : IAcadCommand
    {
        public string Name => H1FixedHatchPreflightContract.PluginToolName;
        public string Description => "Read the frozen H1 fixture prerequisites; never authorize or perform repair.";
        public CommandSchema Schema => CommandSchema.Empty;

        public CommandResult Execute(Document document, JToken parameters)
        {
            if (!App.H1PreflightPilotEnabled) return CommandResult.Fail("h1_preflight_disabled");
            if (document?.Database == null || parameters is not JObject input || input.Count != 0)
                return CommandResult.Fail("h1_preflight_document_or_parameters_invalid");
            JObject facts;
            using (Transaction tx = document.Database.TransactionManager.StartTransaction())
            { facts = ReadFacts(document, tx); tx.Abort(); }
            JObject evidence = H1FixedHatchPreflightContract.BuildEvidence(facts);
            evidence["observation"] = "preflight_read_time";
            evidence["reported_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
            evidence["database_fingerprint"] = document.Database.FingerprintGuid.ToString();
            return CommandResult.Success(evidence);
        }

        internal static JObject ReadFacts(Document document, Transaction tx)
        {
            var facts = new JObject { ["read_errors"] = new JArray(), ["reactor_handles_truncated"] = false };
            var errors = (JArray)facts["read_errors"];
            try { facts["database_undo_recording"] = document.Database.UndoRecording; }
            catch { errors.Add("database_undo_recording_unavailable"); }
            try { facts["document_read_only"] = document.IsReadOnly; }
            catch { errors.Add("document_read_only_unavailable"); }
            Inspect(document.Database, tx, H1FixedHatchPreflightContract.BoundaryHandle, "boundary", facts);
            Inspect(document.Database, tx, H1FixedHatchPreflightContract.HatchHandle, "hatch", facts);
            return facts;
        }

        private static void Inspect(Database db, Transaction tx, string handle, string role, JObject facts)
        {
            var errors = (JArray)facts["read_errors"];
            if (!CadHandleResolver.TryResolve(db, handle, out ObjectId id, out _))
            { errors.Add(role + "_not_found"); return; }
            try
            {
                if (tx.GetObject(id, OpenMode.ForRead) is not Entity entity)
                { errors.Add(role + "_not_entity"); return; }
                facts[role + "_in_model_space"] = entity.OwnerId == SymbolUtilityServices.GetBlockModelSpaceId(db);
                facts[role + "_layer_locked"] = ((LayerTableRecord)tx.GetObject(entity.LayerId, OpenMode.ForRead)).IsLocked;
                bool geometry = role == "boundary" ? entity is Polyline polyline && polyline.NumberOfVertices == 4
                    : entity is Hatch hatch && hatch.NumberOfLoops == 1;
                facts[role] = JObject.FromObject(CadEntityProperties.Describe(entity, tx, geometry));
                ObjectIdCollection reactors = entity.GetPersistentReactorIds();
                var handles = new JArray();
                for (int index = 0; index < Math.Min(reactors.Count, 64); index++)
                {
                    if (reactors[index].IsNull || reactors[index].IsErased) throw new InvalidOperationException();
                    handles.Add(reactors[index].Handle.ToString());
                }
                facts[role + "_reactor_handles"] = handles;
                if (reactors.Count > 64) facts["reactor_handles_truncated"] = true;
            }
            catch { errors.Add(role + "_properties_unavailable"); }
        }
    }
}
