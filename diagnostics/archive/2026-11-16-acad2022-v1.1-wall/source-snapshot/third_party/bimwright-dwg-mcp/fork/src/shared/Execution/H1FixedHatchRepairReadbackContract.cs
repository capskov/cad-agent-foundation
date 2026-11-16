using System;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Shared.Execution
{
    // Native repair readback, distinct from journal verification/fence release.
    public static class H1FixedHatchRepairReadbackContract
    {
        public const string PlanId = "h1-fixed-solid-repair-v1";
        public static bool Matches(JObject before, JObject after)
        {
            try
            {
                if (H1FixedHatchPreflightContract.BuildEvidence(before).Value<string>("candidate_status") != "candidate" || after == null)
                    return false;
                foreach (string field in new[] { "database_undo_recording", "document_read_only", "boundary_layer_locked", "hatch_layer_locked", "boundary_in_model_space", "hatch_in_model_space", "reactor_handles_truncated", "read_errors" })
                    if (!JToken.DeepEquals(before[field], after[field])) return false;
                if (!JToken.DeepEquals(before["boundary"], after["boundary"])
                    || !JToken.DeepEquals(after["boundary_reactor_handles"], new JArray("7B"))
                    || !JToken.DeepEquals(after["hatch_reactor_handles"], new JArray())
                    || !H1FixedHatchPreflightContract.MatchesRepairedHatch(after["hatch"] as JObject)) return false;
                foreach (string field in new[] { "handle", "type", "layer", "color_index", "pattern_name", "pattern_scale", "pattern_angle", "area", "area_status", "normal", "elevation" })
                    if (!JToken.DeepEquals(before["hatch"]?[field], after["hatch"]?[field])) return false;
                return true;
            }
            catch { return false; }
        }
    }
}
