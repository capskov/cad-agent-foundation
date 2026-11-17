// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using Autodesk.AutoCAD.ApplicationServices;
using Bimwright.Dwg.Plugin.Cad;
using Bimwright.Dwg.Plugin.View;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin.Handlers
{
    public class QueryEntitiesHandler : IAcadCommand
    {
        public string Name => "query_entities";
        public string Description => "Query model-space entities with at least one type, layer, or color filter and a bounded optional limit.";
        public CommandSchema Schema => CommandSchemas.QueryEntities;

        public CommandResult Execute(Document doc, JToken parameters)
        {
            if (!TryValidateQueryScope(parameters, out string scopeError))
            {
                return CommandResult.Fail(scopeError);
            }

            var includeGeometry = parameters?["include_geometry"]?.Value<bool>() ?? false;
            if (!QueryHandlerInput.TryCreateOptions(
                parameters,
                includeGeometry,
                readLimit: true,
                out var options,
                out var error))
            {
                return CommandResult.Fail(error);
            }

            try
            {
                DrawingRevision drawingRevision = DrawingRevision.For(doc);
                long revision = drawingRevision.Revision;
                string fingerprint = Guid.Parse(doc.Database.FingerprintGuid).ToString("D");
                CadQueryBounds bounds = options.Bounds;
                string filterDigest = CadQueryPageToken.ComputeFilterDigest(
                    options.EntityType,
                    options.Layer,
                    options.ColorIndex,
                    options.IncludeGeometry,
                    bounds?.MinX,
                    bounds?.MinY,
                    bounds?.MaxX,
                    bounds?.MaxY);
                string pageToken = QueryHandlerInput.ReadOptionalString(parameters, "page_token");
                int offset = 0;
                if (!string.IsNullOrWhiteSpace(pageToken)
                    && !CadQueryPageToken.TryRead(pageToken, drawingRevision.SessionId, fingerprint,
                        revision, filterDigest, out offset, out error))
                {
                    return CommandResult.Fail(error == "page_token_stale"
                        ? "page_token_stale: the bound drawing session or revision changed; restart from the first page"
                        : error == "page_token_filter_mismatch"
                            ? "page_token_filter_mismatch: continue with the same query filters"
                            : "page_token_invalid: use the next_cursor returned by the preceding page");
                }

                options = new CadEntityQueryOptions(
                    options.EntityType,
                    options.Layer,
                    options.ColorIndex,
                    options.Limit,
                    options.IncludeGeometry,
                    options.Bounds,
                    offset);
                var result = CadQueryService.QueryEntities(doc.Database, options);
                if (drawingRevision.Revision != revision)
                {
                    return CommandResult.Fail("drawing_changed_during_query: restart from the first page");
                }

                string nextCursor = null;
                if (result.HasMore)
                {
                    int nextOffset;
                    try { nextOffset = checked(offset + result.Entities.Count); }
                    catch (OverflowException) { return CommandResult.Fail("page_offset_overflow"); }
                    nextCursor = CadQueryPageToken.Create(drawingRevision.SessionId, fingerprint,
                        revision, filterDigest, nextOffset);
                }

                return CommandResult.Success(new
                {
                    count = result.Entities.Count,
                    has_more = result.HasMore,
                    truncated = result.HasMore,
                    next_cursor = nextCursor,
                    entities = result.Entities
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("failed to query entities: " + ex.Message);
            }
        }

        private static bool TryValidateQueryScope(JToken parameters, out string error)
        {
            error = null;
            if (parameters is not JObject query)
            {
                error = "query_entities parameters must be an object with at least one filter";
                return false;
            }

            bool hasFilter = false;
            foreach (string propertyName in new[] { "entity_type", "layer" })
            {
                JToken value = query[propertyName];
                if (value == null || value.Type == JTokenType.Null)
                    continue;
                if (value.Type != JTokenType.String)
                {
                    error = propertyName + " must be a string";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(value.Value<string>()))
                    hasFilter = true;
            }

            JToken colorToken = query["color_index"];
            if (colorToken != null && colorToken.Type != JTokenType.Null)
            {
                if (!QueryHandlerInput.TryReadOptionalColorIndex(parameters, out int? colorIndex, out error))
                    return false;
                hasFilter |= colorIndex.HasValue;
            }

            if (!QueryHandlerInput.TryReadOptionalBounds(parameters, out CadQueryBounds bounds, out error))
                return false;
            hasFilter |= bounds != null;

            if (!QueryHandlerInput.TryReadOptionalLimit(parameters, readLimit: true, out int? limit, out error))
                return false;
            if (limit.HasValue && (limit.Value < 1 || limit.Value > 5000))
            {
                error = "limit must be an integer from 1 through 5000";
                return false;
            }

            JToken geometry = query["include_geometry"];
            if (geometry != null && geometry.Type != JTokenType.Boolean)
            {
                error = "include_geometry must be a boolean";
                return false;
            }

            JToken pageToken = query["page_token"];
            if (pageToken != null && pageToken.Type != JTokenType.Null
                && (pageToken.Type != JTokenType.String
                    || string.IsNullOrWhiteSpace(pageToken.Value<string>())
                    || pageToken.Value<string>().Length > 4096))
            {
                error = "page_token must be a non-empty string no longer than 4096 characters";
                return false;
            }

            if (!hasFilter)
            {
                error = "query_entities requires at least one filter: entity_type, layer, color_index, or spatial bounds";
                return false;
            }

            return true;
        }
    }

    internal static class QueryHandlerInput
    {
        internal static bool TryCreateOptions(
            JToken parameters,
            bool includeGeometry,
            bool readLimit,
            out CadEntityQueryOptions options,
            out string error)
        {
            options = null;
            error = null;

            if (!TryReadOptionalColorIndex(parameters, out var colorIndex, out error) ||
                !TryReadOptionalLimit(parameters, readLimit, out var limit, out error) ||
                !TryReadOptionalBounds(parameters, out var bounds, out error))
            {
                return false;
            }

            options = new CadEntityQueryOptions(
                ReadOptionalString(parameters, "entity_type"),
                ReadOptionalString(parameters, "layer"),
                colorIndex,
                limit,
                includeGeometry,
                bounds);
            return true;
        }

        internal static string ReadOptionalString(JToken parameters, string fieldName)
        {
            var obj = parameters as JObject;
            return obj?[fieldName]?.Value<string>();
        }

        internal static bool TryReadOptionalColorIndex(JToken parameters, out int? colorIndex, out string error)
        {
            colorIndex = null;
            error = null;

            var obj = parameters as JObject;
            var token = obj?["color_index"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (!CadWire.TryReadAciColor(parameters, "color_index", 7, out var value, out error))
            {
                return false;
            }

            colorIndex = value;
            return true;
        }

        internal static bool TryReadOptionalLimit(
            JToken parameters,
            bool readLimit,
            out int? limit,
            out string error)
        {
            limit = null;
            error = null;
            if (!readLimit)
            {
                return true;
            }

            var obj = parameters as JObject;
            var token = obj?["limit"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.Integer)
            {
                error = "limit must be an integer";
                return false;
            }

            long value;
            try
            {
                value = token.Value<long>();
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                error = "limit must be an integer";
                return false;
            }

            if (value < int.MinValue)
            {
                limit = int.MinValue;
            }
            else if (value > int.MaxValue)
            {
                limit = int.MaxValue;
            }
            else
            {
                limit = (int)value;
            }

            return true;
        }

        internal static bool TryReadOptionalBounds(JToken parameters, out CadQueryBounds bounds, out string error)
        {
            bounds = null;
            error = null;
            var obj = parameters as JObject;
            string[] fields = { "min_x", "min_y", "max_x", "max_y" };
            var values = new double[fields.Length];
            int present = 0;

            for (int i = 0; i < fields.Length; i++)
            {
                JToken token = obj?[fields[i]];
                if (token == null || token.Type == JTokenType.Null)
                    continue;
                if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                {
                    error = fields[i] + " must be a finite number";
                    return false;
                }

                try { values[i] = token.Value<double>(); }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
                {
                    error = fields[i] + " must be a finite number";
                    return false;
                }
                if (double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                {
                    error = fields[i] + " must be a finite number";
                    return false;
                }
                present++;
            }

            if (present == 0)
                return true;
            if (present != fields.Length)
            {
                error = "min_x, min_y, max_x, and max_y must be supplied together";
                return false;
            }
            if (values[0] >= values[2] || values[1] >= values[3])
            {
                error = "rectangle bounds require min_x < max_x and min_y < max_y";
                return false;
            }

            bounds = new CadQueryBounds(values[0], values[1], values[2], values[3]);
            return true;
        }
    }
}
