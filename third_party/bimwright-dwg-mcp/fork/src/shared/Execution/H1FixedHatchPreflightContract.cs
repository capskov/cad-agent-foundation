using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Shared.Execution
{
    // Candidate inspection only. This contract has no write parameters or release evidence.
    public static class H1FixedHatchPreflightContract
    {
        public const string McpToolName = "dwg_h1_preflight_fixed";
        public const string PluginToolName = "h1_preflight_fixed";
        public const string CanonicalParametersJson = "{}";
        public const string PlanId = "h1-fixed-solid-v1";
        public const string BoundaryHandle = "7A";
        public const string HatchHandle = "7B";
        public const int EvidenceSchemaVersion = 1;

        public static JObject BuildEvidence(JObject facts)
        {
            facts ??= new JObject();
            var blockers = new List<string>();
            RequireBool(facts, "database_undo_recording", true, blockers);
            RequireBool(facts, "document_read_only", false, blockers);
            RequireBool(facts, "boundary_layer_locked", false, blockers);
            RequireBool(facts, "hatch_layer_locked", false, blockers);
            RequireBool(facts, "boundary_in_model_space", true, blockers);
            RequireBool(facts, "hatch_in_model_space", true, blockers);
            if (!SafeMatch(() => MatchesBoundary(facts["boundary"] as JObject))) blockers.Add("boundary_fixed_baseline_mismatch");
            if (!SafeMatch(() => MatchesHatch(facts["hatch"] as JObject))) blockers.Add("hatch_fixed_baseline_mismatch");
            foreach (string field in new[] { "boundary_reactor_handles", "hatch_reactor_handles" })
                if (facts[field] is not JArray array || array.Count != 0) blockers.Add(field + "_not_known_empty");
            if (facts["read_errors"] is not JArray errors || errors.Count != 0) blockers.Add("read_evidence_incomplete");
            var result = new JObject
            {
                ["schema_version"] = EvidenceSchemaVersion,
                ["plan_id"] = PlanId,
                ["candidate_only"] = true,
                ["write_authorized"] = false,
                ["candidate_status"] = blockers.Count == 0 ? "candidate" : "blocked",
                ["blockers"] = new JArray(blockers),
                ["boundary_handle"] = BoundaryHandle,
                ["hatch_handle"] = HatchHandle,
                ["unverified_write_guarantees"] = new JArray("per_object_undo_override", "durable_write_and_finalize")
            };
            foreach (string field in new[] { "database_undo_recording", "document_read_only", "boundary_layer_locked",
                "hatch_layer_locked", "boundary_in_model_space", "hatch_in_model_space", "boundary", "hatch",
                "boundary_reactor_handles", "hatch_reactor_handles", "reactor_handles_truncated", "read_errors" })
                result[field] = facts[field]?.DeepClone() ?? JValue.CreateNull();
            if (facts["reactor_handles_truncated"]?.Type != JTokenType.Boolean
                || facts.Value<bool>("reactor_handles_truncated"))
            {
                ((JArray)result["blockers"]).Add("reactor_handles_incomplete");
                result["candidate_status"] = "blocked";
            }
            return result;
        }

        private static void RequireBool(JObject facts, string field, bool expected, List<string> blockers)
        {
            if (facts[field]?.Type != JTokenType.Boolean) blockers.Add(field + "_unknown");
            else if (facts.Value<bool>(field) != expected) blockers.Add(field + "_blocked");
        }

        private static bool SafeMatch(Func<bool> match) { try { return match(); } catch { return false; } }

        private static bool Near(JToken value, double expected) => value != null
            && value.Type is JTokenType.Float or JTokenType.Integer
            && !double.IsNaN(value.Value<double>()) && !double.IsInfinity(value.Value<double>())
            && Math.Abs(value.Value<double>() - expected) <= 1e-8;

        private static bool Basic(JObject entity, string handle, string type) => entity != null
            && entity.Value<string>("handle") == handle && entity.Value<string>("type") == type
            && entity.Value<string>("layer") == "0" && Near(entity["color_index"], 256);

        private static bool Point(JToken point, double x, double y) => point is JObject obj
            && Near(obj["x"], x) && Near(obj["y"], y) && Near(obj["z"], 0);

        private static bool MatchesBoundary(JObject boundary)
        {
            if (!Basic(boundary, BoundaryHandle, "Polyline") || boundary["is_closed"]?.Type != JTokenType.Boolean
                || !boundary.Value<bool>("is_closed") || !Near(boundary["vertex_count"], 4)
                || !Near(boundary["area"], 100) || boundary["vertices"] is not JArray points || points.Count != 4)
                return false;
            double[,] expected = { { 100, 30 }, { 110, 30 }, { 110, 40 }, { 100, 40 } };
            for (int index = 0; index < 4; index++)
                if (!Point(points[index], expected[index, 0], expected[index, 1]) || !Near(points[index]["bulge"], 0)) return false;
            return true;
        }

        public static bool MatchesRepairedHatch(JObject hatch) => SafeMatch(() => MatchesHatch(hatch, 100, true));

        private static bool MatchesHatch(JObject hatch, double minX = 0, bool associative = false)
        {
            if (!Basic(hatch, HatchHandle, "Hatch") || hatch.Value<string>("pattern_name") != "SOLID"
                || !Near(hatch["pattern_scale"], 1) || !Near(hatch["pattern_angle"], 0)
                || hatch.Value<string>("area_status") != "available" || !Near(hatch["area"], 100)
                || hatch["is_associative"]?.Type != JTokenType.Boolean || hatch.Value<bool>("is_associative") != associative
                || !Near(hatch["loop_count"], 1) || hatch.Value<string>("loop_read_status") != "complete"
                || hatch.Value<string>("loop_geometry_space") != "wcs" || hatch["normal"] is not JObject normal
                || !Near(normal["x"], 0) || !Near(normal["y"], 0) || !Near(normal["z"], 1) || !Near(hatch["elevation"], 0)
                || hatch["loops_truncated"]?.Type != JTokenType.Boolean || hatch.Value<bool>("loops_truncated")
                || hatch["loops"] is not JArray loops || loops.Count != 1 || loops[0] is not JObject loop
                || loop.Value<string>("geometry_status") != "available"
                || loop.Value<string>("association_status") != (associative ? "available" : "not_associative")
                || loop["associated_handles"] is not JArray associated
                || (associative ? associated.Count != 1 || associated[0]?.Type != JTokenType.String || associated[0].Value<string>() != BoundaryHandle
                    || !Near(loop["associated_handle_count"], 1) || loop["associated_handles_truncated"]?.Type != JTokenType.Boolean || loop.Value<bool>("associated_handles_truncated")
                    : associated.Count != 0))
                return false;
            var starts = new List<JToken>();
            if (loop.Value<string>("geometry_kind") == "curve_edges")
            {
                if (!Near(loop["edge_count"], 4) || loop["edges_truncated"]?.Type != JTokenType.Boolean
                    || loop.Value<bool>("edges_truncated") || loop["edges_wcs"] is not JArray edges || edges.Count != 4)
                    return false;
                for (int index = 0; index < 4; index++)
                {
                    if (edges[index] is not JObject edge || edge.Value<string>("kind") != "line"
                        || !JToken.DeepEquals(edge["end"], edges[(index + 1) % 4]["start"])) return false;
                    starts.Add(edge["start"]);
                }
            }
            else if (loop.Value<string>("geometry_kind") == "polyline")
            {
                if (!Near(loop["vertex_count"], 4) || loop["vertices_truncated"]?.Type != JTokenType.Boolean
                    || loop.Value<bool>("vertices_truncated") || loop["vertices_wcs"] is not JArray points || points.Count != 4)
                    return false;
                foreach (JToken vertex in points) { if (!Near(vertex["bulge"], 0)) return false; starts.Add(vertex); }
            }
            else return false;
            double[,] corners = { { minX, 30 }, { minX + 10, 30 }, { minX + 10, 40 }, { minX, 40 } };
            foreach (int direction in new[] { 1, -1 })
                for (int offset = 0; offset < 4; offset++)
                    if (Enumerable.Range(0, 4).All(index => Point(starts[index],
                        corners[(offset + direction * index + 4) % 4, 0], corners[(offset + direction * index + 4) % 4, 1]))) return true;
            return false;
        }
    }
}
