// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server.Tools
{
    [McpServerToolType]
    public class QueryTools
    {
        [McpServerTool(Name = "dwg_get_drawing_info", ReadOnly = true, Idempotent = true), Description(
            "Return small JSON-safe metadata for the current AutoCAD drawing: " +
            "drawing name when available, current layer, current space/layout, " +
            "and database unit scalars.")]
        public static Task<string> GetDrawingInfo()
        {
            var request = new { };
            return ToolGateway.LoggedCall("get_drawing_info", request, request);
        }

        [McpServerTool(Name = "dwg_get_entity_properties", ReadOnly = true, Idempotent = true), Description(
            "Return properties for AutoCAD entities identified by handle. " +
            "Input handles is a JSON array of 1-64 hex handles, e.g. [\"7F5AD\"]. " +
            "Returns one result record per handle; bad handles do not abort siblings. entity_digest is attempted independently of include_geometry; digest_error explains unavailable digests. Never submit a write with an empty digest. " +
            "AutoCAD 2026 include_geometry=true also returns geometry_read: read-only native transient decomposition of proxies, blocks and compound entities into WCS primitives, with paths, status and issues. " +
            "Proxy results are saved display graphics, not proprietary design data. Collection order does not establish connectivity; partial/unavailable must be checked. Query candidates first, then read small handle batches. " +
            "With include_geometry=true, ordinary Leader records include ordered vertices and an annotation handle; " +
            "the linked annotation text is not expanded and must be read separately by that handle. " +
            "Polyline area is AutoCAD's native value; an open polyline's area is not a region area, so check is_closed. " +
            "Large results are explicitly marked truncated.")]
        public static Task<string> GetEntityProperties(
            [Description("JSON array of AutoCAD handles, e.g. [\"7F5AD\",\"2A4F\"].")] string handles,
            [Description("When true, include lightweight geometry such as extents, points, lengths, and text positions where available.")] bool include_geometry = false,
            [Description("Optional geometry page offset, integer 0..4096; requires one handle and include_geometry=true. Use geometry_read.next_offset with the same entity_digest, document_session_id and drawing_revision; restart if any changes. Always check geometry_read issues and has_more.")] int? geometry_offset = null)
        {
            if (string.IsNullOrWhiteSpace(handles))
            {
                return ToolInputError("handles must be a JSON array");
            }

            JArray parsed;
            try
            {
                parsed = JArray.Parse(handles);
            }
            catch (JsonException ex)
            {
                return ToolInputError($"handles must be a JSON array: {ex.Message}");
            }

            var request = new JObject { ["handles"]=parsed, ["include_geometry"]=include_geometry };
            if(geometry_offset.HasValue)request["geometry_offset"]=geometry_offset.Value;
            return ToolGateway.LoggedCall("get_entity_properties", request, request);
        }

        [McpServerTool(Name = "dwg_list_layers", ReadOnly = true, Idempotent = true), Description(
            "List layers in the current AutoCAD drawing with small scalar properties " +
            "such as color index, locked/frozen/off state.")]
        public static Task<string> ListLayers()
        {
            var request = new { };
            return ToolGateway.LoggedCall("list_layers", request, request);
        }

        [McpServerTool(Name = "dwg_query_entities", ReadOnly = true, Idempotent = true), Description(
            "Query top-level model-space AutoCAD entities. At least one filter is required: entity_type, layer, color_index, or all four WCS XY bounds. " +
            "A rectangle matches entity geometric extents that overlap it, including touching edges; this is a coarse candidate filter, not exact geometry intersection. " +
            "Returns count, has_more, truncated, entity records, and a next_cursor when another page exists; it does not provide total_count. " +
            "Pass next_cursor as page_token with the same filters to continue. The cursor is bound to the current drawing session and revision and becomes stale after drawing-object changes. " +
            "limit defaults to 500 and, when supplied, must be an integer from 1 through 5000; invalid limits are rejected.")]
        public static Task<string> QueryEntities(
            [Description("Optional entity type filter such as Line, Circle, Polyline, DBText, MText, BlockReference, Hatch, Arc, or Ellipse. Case-insensitive.")] string entity_type = null,
            [Description("Optional layer name filter. Case-insensitive.")] string layer = null,
            [Description("Optional ACI color index filter; a supplied value must be an integer from 1 through 256.")] int? color_index = null,
            [Description("Optional WCS XY lower X bound in drawing units. Supply all four bounds together.")] double? min_x = null,
            [Description("Optional WCS XY lower Y bound in drawing units. Supply all four bounds together.")] double? min_y = null,
            [Description("Optional WCS XY upper X bound in drawing units. Supply all four bounds together.")] double? max_x = null,
            [Description("Optional WCS XY upper Y bound in drawing units. Supply all four bounds together.")] double? max_y = null,
            [Description("Optional maximum number of returned entities. Defaults to 500; a supplied value must be an integer from 1 through 5000.")] int? limit = null,
            [Description("Optional continuation token returned as next_cursor. It is valid only for the same bound drawing session, revision, and query filters.")] string page_token = null,
            [Description("When true, include lightweight geometry such as extents, points, lengths, and text positions where available.")] bool include_geometry = false)
        {
            var request = BuildEntityQueryRequest(entity_type, layer, color_index, limit);
            if (min_x.HasValue) request["min_x"] = min_x.Value;
            if (min_y.HasValue) request["min_y"] = min_y.Value;
            if (max_x.HasValue) request["max_x"] = max_x.Value;
            if (max_y.HasValue) request["max_y"] = max_y.Value;
            if (page_token != null) request["page_token"] = page_token;
            request["include_geometry"] = include_geometry;
            return ToolGateway.LoggedCall("query_entities", request, request);
        }

        [McpServerTool(Name = "dwg_count_entities", ReadOnly = true, Idempotent = true), Description(
            "Count model-space AutoCAD entities matching optional entity type, layer, and ACI color index filters.")]
        public static Task<string> CountEntities(
            [Description("Optional entity type filter such as Line, Circle, Polyline, DBText, MText, BlockReference, Hatch, Arc, or Ellipse. Case-insensitive.")] string entity_type = null,
            [Description("Optional layer name filter. Case-insensitive.")] string layer = null,
            [Description("Optional ACI color index filter. Valid range: 1-256.")] int? color_index = null)
        {
            var request = BuildEntityQueryRequest(entity_type, layer, color_index, limit: null);
            return ToolGateway.LoggedCall("count_entities", request, request);
        }

        [McpServerTool(Name = "dwg_select_by_layer", ReadOnly = true, Idempotent = true), Description(
            "Return handles for model-space entities on a required layer, with optional type/color filters. " +
            "This does not change AutoCAD pickfirst selection.")]
        public static Task<string> SelectByLayer(
            [Description("Required layer name filter. Case-insensitive.")] string layer,
            [Description("Optional entity type filter. Case-insensitive.")] string entity_type = null,
            [Description("Optional ACI color index filter. Valid range: 1-256.")] int? color_index = null,
            [Description("Optional maximum number of returned handles. The plugin clamps this to 1-5000.")] int? limit = null)
        {
            var request = BuildEntityQueryRequest(entity_type, layer, color_index, limit);
            return ToolGateway.LoggedCall("select_by_layer", request, request);
        }

        [McpServerTool(Name = "dwg_select_by_type", ReadOnly = true, Idempotent = true), Description(
            "Return handles for model-space entities of a required type, with optional layer/color filters. " +
            "This does not change AutoCAD pickfirst selection.")]
        public static Task<string> SelectByType(
            [Description("Required entity type filter such as Line, Circle, Polyline, DBText, MText, BlockReference, Hatch, Arc, or Ellipse. Case-insensitive.")] string entity_type,
            [Description("Optional layer name filter. Case-insensitive.")] string layer = null,
            [Description("Optional ACI color index filter. Valid range: 1-256.")] int? color_index = null,
            [Description("Optional maximum number of returned handles. The plugin clamps this to 1-5000.")] int? limit = null)
        {
            var request = BuildEntityQueryRequest(entity_type, layer, color_index, limit);
            return ToolGateway.LoggedCall("select_by_type", request, request);
        }

        [McpServerTool(Name = "dwg_get_selected_texts", ReadOnly = true, Idempotent = true), Description(
            "Read and cluster text entities currently selected in AutoCAD. " +
            "Returns pre-clustered groups with combined text in reading order. " +
            "Each cluster has id, text, entity_count, in_block (bool) and " +
            "rewrite_mode (one of: update, collapse, rewrite_in_block) - the " +
            "mode that dwg_translate_and_rewrite will apply if given a translation " +
            "for this cluster. Use this BEFORE dwg_translate_and_rewrite. The user " +
            "must select entities in AutoCAD BEFORE calling this tool.")]
        public static Task<string> GetSelectedTexts(
            [Description("Optional grouping strength: weak, normal, or strong. Use weak when an image/layout reference indicates nearby labels should stay separate.")] string groupingStrength = "normal",
            [Description("When true, include each child text entity's handle/text/position so callers can recover from over-grouped clusters.")] bool includeEntities = false)
        {
            var request = new { grouping_strength = groupingStrength, include_entities = includeEntities };
            return ToolGateway.LoggedCall("get_selected_texts", request, request);
        }

        [McpServerTool(Name = "dwg_inspect_lisp", ReadOnly = true, Idempotent = true), Description(
            "Statically inspect AutoLISP for known suspicious patterns without executing it — " +
            "downloaded .lsp files can embed malware (process exec via startapp/shell, " +
            "COM automation like WScript.Shell/XMLHTTP, persistence via acad.lsp/acaddoc.lsp " +
            "or registry writes, file deletion, staged (load ...) payloads, obfuscation via " +
            "eval/read). Pure local analysis: does not execute anything and needs no AutoCAD " +
            "round-trip, so it works under --read-only too. Params: 'file' (absolute path) " +
            "and/or 'code' (inline source). Returns {ok, verdict: clean|caution|dangerous, " +
            "opaque, summary, findings[]}. Compiled .fas/.vlx cannot be inspected and always " +
            "verdict 'dangerous'. This is not a sandbox or an antivirus guarantee: clean means no known " +
            "pattern matched, not safe. execution_authorized and safety_assured are always false. " +
            "dwg_run_lisp is blocked in this build; do not bypass that refusal through other execution tools.")]
        public static Task<string> InspectLisp(
            [Description("Absolute path to a .lsp/.fas/.vlx file to inspect")] string file = null,
            [Description("Inline AutoLISP source to inspect")] string code = null)
        {
            if (string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(code))
            {
                return ToolInputError("at least one of file or code is required");
            }

            var merged = new LispSecurityScanner.Report();
            var scanned = new List<string>();

            if (!string.IsNullOrWhiteSpace(file))
            {
                var rep = LispSecurityScanner.ScanFile(file, out var err);
                if (rep == null)
                {
                    return ToolInputError(err);
                }
                LispSecurityScanner.AbsorbInto(merged, rep);
                scanned.Add("file: " + file);
            }
            if (!string.IsNullOrWhiteSpace(code))
            {
                LispSecurityScanner.AbsorbInto(merged, LispSecurityScanner.ScanText(code));
                scanned.Add("code: " + code.Length + " chars");
            }

            return Task.FromResult(LispSecurityScanner.ToJson(merged, string.Join("; ", scanned)).ToString(Newtonsoft.Json.Formatting.None));
        }

        private static JObject BuildEntityQueryRequest(
            string entityType,
            string layer,
            int? colorIndex,
            int? limit)
        {
            var request = new JObject();
            if (!string.IsNullOrWhiteSpace(entityType))
            {
                request["entity_type"] = entityType;
            }

            if (!string.IsNullOrWhiteSpace(layer))
            {
                request["layer"] = layer;
            }

            if (colorIndex.HasValue)
            {
                request["color_index"] = colorIndex.Value;
            }

            if (limit.HasValue)
            {
                request["limit"] = limit.Value;
            }

            return request;
        }

        private static Task<string> ToolInputError(string error)
        {
            return Task.FromResult(JsonConvert.SerializeObject(new { ok = false, error }));
        }
    }
}
