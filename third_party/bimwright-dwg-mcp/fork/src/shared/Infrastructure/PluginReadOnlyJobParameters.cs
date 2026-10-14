using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#if NET8_0
using System.Text.Json;
#endif
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#if NET8_0
namespace Bimwright.Dwg.Plugin
{
    /// <summary>
    /// Validates the canonical query payload again at the plugin boundary so a
    /// durable queue submission cannot widen the server's read-only query.
    /// </summary>
    internal static class PluginReadOnlyJobParameters
    {
        private static readonly HashSet<string> QueryEntitiesFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "entity_type",
            "layer",
            "color_index",
            "min_x",
            "min_y",
            "max_x",
            "max_y",
            "limit",
            "page_token",
            "include_geometry"
        };

        internal static bool TryParseCanonicalQueryEntities(
            string canonicalJson,
            out JToken parameters,
            out string error)
        {
            parameters = null;
            error = "invalid_tool_params";
            if (string.IsNullOrWhiteSpace(canonicalJson))
                return false;

            try
            {
                JToken parsed = JToken.Parse(canonicalJson);
                if (!(parsed is JObject query))
                    return false;

                foreach (JProperty property in query.Properties())
                {
                    if (!QueryEntitiesFields.Contains(property.Name))
                        return false;
                }

                SchemaValidationResult schema = SchemaValidator.Validate(
                    "query_entities", parsed, CommandSchemas.QueryEntities);
                if (!schema.Ok)
                    return false;

                if (!TryValidateStringFilter(query, "entity_type")
                    || !TryValidateStringFilter(query, "layer"))
                {
                    return false;
                }

                bool hasFilter = HasNonEmptyStringFilter(query, "entity_type")
                    || HasNonEmptyStringFilter(query, "layer");

                JToken colorToken = query["color_index"];
                if (colorToken != null)
                {
                    long colorIndex = colorToken.Value<long>();
                    if (colorIndex < 1 || colorIndex > 256)
                        return false;
                    hasFilter = true;
                }

                if (!TryValidateBounds(query, out bool hasBounds))
                    return false;
                hasFilter |= hasBounds;

                JToken limitToken = query["limit"];
                if (limitToken != null)
                {
                    long limit = limitToken.Value<long>();
                    if (limit < 1 || limit > 5000)
                        return false;
                }

                if (!hasFilter)
                    return false;

                JToken geometryToken = query["include_geometry"];
                if (geometryToken == null || geometryToken.Type != JTokenType.Boolean)
                    return false;

                JToken pageToken = query["page_token"];
                if (pageToken != null && (pageToken.Type != JTokenType.String
                    || string.IsNullOrWhiteSpace(pageToken.Value<string>())
                    || pageToken.Value<string>().Length > 4096))
                    return false;

                if (!string.Equals(Canonicalize(canonicalJson), canonicalJson, StringComparison.Ordinal))
                    return false;

                parameters = parsed;
                error = null;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasNonEmptyStringFilter(JObject query, string fieldName)
        {
            JToken value = query[fieldName];
            return value != null && !string.IsNullOrWhiteSpace(value.Value<string>());
        }

        private static bool TryValidateStringFilter(JObject query, string fieldName)
        {
            JToken value = query[fieldName];
            return value == null || !string.IsNullOrWhiteSpace(value.Value<string>());
        }

        private static bool TryValidateBounds(JObject query, out bool hasBounds)
        {
            hasBounds = false;
            string[] fields = { "min_x", "min_y", "max_x", "max_y" };
            int present = 0;
            var values = new double[fields.Length];

            for (int i = 0; i < fields.Length; i++)
            {
                JToken token = query[fields[i]];
                if (token == null || token.Type == JTokenType.Null)
                    continue;
                if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                    return false;

                double value;
                try { value = token.Value<double>(); }
                catch { return false; }
                if (double.IsNaN(value) || double.IsInfinity(value))
                    return false;
                values[i] = value;
                present++;
            }

            if (present == 0) return true;
            if (present != fields.Length || values[0] >= values[2] || values[1] >= values[3])
                return false;

            hasBounds = true;
            return true;
        }

        private static string Canonicalize(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            using (var buffer = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    WriteCanonical(writer, document.RootElement);
                }

                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    JsonProperty[] properties = value.EnumerateObject()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                        .ToArray();
                    for (int i = 1; i < properties.Length; i++)
                    {
                        if (string.Equals(properties[i - 1].Name, properties[i].Name, StringComparison.Ordinal))
                            throw new InvalidOperationException("The query contains duplicate parameter names.");
                    }

                    writer.WriteStartObject();
                    foreach (JsonProperty property in properties)
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (JsonElement item in value.EnumerateArray())
                        WriteCanonical(writer, item);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(value.GetString());
                    break;
                case JsonValueKind.Number:
                    // Keep the exact JSON number token. RequestLedger's
                    // canonicalizer sorts properties but preserves numeric
                    // spellings, so rewriting 99.0 as 99 here would
                    // reject valid integral WCS bounds at the plugin boundary.
                    writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                    break;
                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;
                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;
                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;
                default:
                    throw new InvalidOperationException("The query contains a non-canonical value.");
            }
        }
    }
}
#endif
