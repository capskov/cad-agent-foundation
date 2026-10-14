using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
    /// <summary>Read-only candidate inspection for the fixed Road08 R01 write.</summary>
    internal sealed class D1Road08PreflightHandler : IAcadCommand
    {
        public string Name => Road08R01D1WriteContract.PreflightPluginToolName;
        public string Description => "Inspect the fixed Road08 D1 prerequisites without authorizing a write.";
        public CommandSchema Schema => CommandSchema.Empty;

        public CommandResult Execute(Document document, JToken parameters)
        {
            if (!App.D1PreflightPilotEnabled)
                return CommandResult.Fail("d1_preflight_pilot_disabled");
            if (document == null || document.Database == null)
                return CommandResult.Fail("d1_preflight_document_unavailable");
            if (parameters is not JObject obj || obj.Count != 0)
                return CommandResult.Fail("d1_preflight_parameters_invalid");

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    JObject evidence = Road08R01WriteHandler.InspectCandidatePreflight(
                        document.Database, transaction);
                    transaction.Abort();
                    return CommandResult.Success(evidence);
                }
            }
            catch
            {
                return CommandResult.Fail("d1_preflight_inspection_failed");
            }
        }
    }
}
