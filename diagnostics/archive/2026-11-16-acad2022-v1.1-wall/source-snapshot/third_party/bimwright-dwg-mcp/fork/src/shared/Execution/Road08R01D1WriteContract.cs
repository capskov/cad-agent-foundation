// Public export: historical pilot coordinates replaced by illustrative local coordinates.
// Original engineering fixtures/receipts are excluded. This is not a supported public pilot or native acceptance evidence.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Shared.Execution
{
    /// <summary>
    /// Frozen, one-group write contract for the Road08 D1 acceptance loop.
    /// The callout is deliberately fixed: callers cannot supply geometry,
    /// layers, styles, risk keys, or release evidence.
    /// </summary>
    public static class Road08R01D1WriteContract
    {
        public const string McpToolName = "dwg_d1_write_r01";
        public const string PluginToolName = "drawing_write";
        public const string PreflightMcpToolName = "dwg_d1_preflight_r01";
        public const string PreflightPluginToolName = "d1_preflight_r01";
        public const string PreflightCanonicalParametersJson = "{}";
        public const string PlanId = "road08-r01-v4";
        public const string ReadbackValidatorVersion = "road08-r01-readback-v4";
        public const int EvidenceSchemaVersion = 4;
        public const string ResourceCreationPolicy = "create_missing_exact_v1";

        // Captured identity only: this is not proof of entity type or readback.
        public static string NormalizeCapturedEntityHandle(string value)
        {
            if (value is not { Length: > 0 and <= 16 }
                || !value.All(character => character is >= '0' and <= '9'
                    or >= 'A' and <= 'F' or >= 'a' and <= 'f')
                || !ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong handle)
                || handle == 0)
                return null;
            return handle.ToString("X", CultureInfo.InvariantCulture);
        }

        // Used only after Commit returned. Keep this independent of the richer
        // evidence serializer that may have failed; invalid identities are null.
        public static string BuildCommittedUnknownEvidenceJson(string leaderHandle, string mtextHandle)
        {
            string leader = NormalizeCapturedEntityHandle(leaderHandle);
            string text = NormalizeCapturedEntityHandle(mtextHandle);
            return "{\"schema_version\":" + EvidenceSchemaVersion
                + ",\"plan_id\":\"" + PlanId
                + "\",\"leader_handle\":" + (leader == null ? "null" : "\"" + leader + "\"")
                + ",\"mtext_handle\":" + (text == null ? "null" : "\"" + text + "\"")
                + ",\"preflight\":{}}";
        }
        public const string TextContents = @"\A1;R01 拆除结构1 A=27.9m²";
        public const string DuplicateMarkerText = "R01 拆除结构1 A=27.9m²";
        public const string ExpectedSourceLayer = "ROAD";
        public const string TextLayer = "MCP08_TEXT";
        public const string TextStyle = "_KCYD_DIM";
        public const string TextFontFile = "gbenor.shx";
        public const string TextBigFontFile = "gbcbig.shx";
        // Verified from the Road08 STYLE records and Autodesk DXF group codes:
        // the primary/small-font slot is FileName; the Asian big-font slot is
        // BigFontFileName. Keep the exact fallback pair frozen here.
        public const string FallbackTextFontFile = "hztxt_e.shx";
        public const string FallbackBigFontFile = "hztxt.shx";
        public const string FallbackTextStyle = "_KCYD_DIM_D1_HZTXT";
        public const string FallbackTextFontSha256 = "15C9DB5DF16C7B8BE32DC29A02C7E16ED7D889CB2A19244AC75C2E35EE15D381";
        public const string FallbackBigFontSha256 = "17D24E06CADD2FF166465AF02ABA013DFF370A410E0AFF42A91CCBA468B0AA88";
        public const string LeaderLayer = "MCP08_LEADER";
        public const string LeaderStyle = "HyDim";
        public const string ArrowBlockName = "MCP08_YELLOW_DOT";
        public static readonly string[] ArrowBlockReferenceNames = { "_Dot", "Dot" };
        // Stable shape baseline captured from the independently inspected _Dot
        // and MCP08_YELLOW_DOT definitions. It omits handles and block name, but
        // includes origin, static/local flags, and every frozen member property.
        public const string ArrowBlockShapeSha256 = "AB6514C803D72F8FD268D6E32C19A0BCA3749AF53E83F4DF7408D250C53FFCD2";
        // Legacy compatibility placeholder. Run-specific geometry digests are
        // recomputed from inspected definitions; this constant is not a gate.
        // Cross-drawing approval uses ArrowBlockShapeSha256.
        public const string ArrowBlockGeometrySha256 = "";
        public const short TextLayerColorIndex = 7;
        public const short LeaderLayerColorIndex = 7;
        public const string TargetLayerColorMethod = "ByAci";
        public const string TargetLayerLinetypeName = "Continuous";
        public const bool TargetLayerIsOff = false;
        public const bool TargetLayerIsFrozen = false;
        public const bool TargetLayerIsLocked = false;
        public const bool TargetLayerIsPlottable = true;

        public static bool LayerLinetypeNameMatches(string actualName) =>
            !string.IsNullOrWhiteSpace(actualName)
            && string.Equals(actualName, TargetLayerLinetypeName, StringComparison.OrdinalIgnoreCase);

        public static bool IsTargetLayerLinetypeEvidenceValid(JObject evidence) => evidence != null
            && evidence.Value<string>("expected_name") == TargetLayerLinetypeName
            && evidence.Value<bool?>("exists") == true
            && evidence.Value<bool?>("name_matches") == true
            && LayerLinetypeNameMatches(evidence.Value<string>("actual_name"))
            && IsCanonicalHandle(evidence.Value<string>("handle"));

        public static bool SameTargetLayerLinetypeIdentity(JObject left, JObject right) =>
            IsTargetLayerLinetypeEvidenceValid(left)
            && IsTargetLayerLinetypeEvidenceValid(right)
            && string.Equals(left.Value<string>("handle"), right.Value<string>("handle"),
                StringComparison.Ordinal);

        private static bool IsCanonicalHandle(string value) => value is { Length: > 0 and <= 16 }
            && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

        // JSON property names are ordinal-sorted to match the canonical JSON
        // digest used by both Server and plugin journal. Include every frozen
        // baseline that can change whether this exact write is accepted, so a
        // future contract update cannot replay under the same request digest.
        public const string CanonicalParametersJson =
            "{\"arrow_block_name\":\"MCP08_YELLOW_DOT\","
            + "\"arrow_block_shape_sha256\":\"AB6514C803D72F8FD268D6E32C19A0BCA3749AF53E83F4DF7408D250C53FFCD2\","
            + "\"duplicate_marker_text\":\"R01 拆除结构1 A=27.9m²\","
            + "\"evidence_schema_version\":4,"
            + "\"fallback_big_font_file\":\"hztxt.shx\","
            + "\"fallback_big_font_sha256\":\"17D24E06CADD2FF166465AF02ABA013DFF370A410E0AFF42A91CCBA468B0AA88\","
            + "\"fallback_text_style\":\"_KCYD_DIM_D1_HZTXT\","
            + "\"fallback_text_font_file\":\"hztxt_e.shx\","
            + "\"fallback_text_font_sha256\":\"15C9DB5DF16C7B8BE32DC29A02C7E16ED7D889CB2A19244AC75C2E35EE15D381\","
            + "\"leader_arrow_size\":0.375,"
            + "\"leader_color_index\":2,"
            + "\"leader_gap\":0.375,"
            + "\"leader_layer\":\"MCP08_LEADER\","
            + "\"leader_scale\":1,"
            + "\"leader_style\":\"HyDim\","
            + "\"leader_text_above\":0,"
            + "\"leader_text_height\":1.5,"
            + "\"leader_vertices_wcs\":["
            + "{\"x\":74.88270796597,\"y\":76.7816643141,\"z\":0},"
            + "{\"x\":78.4109973181,\"y\":87.39067813475,\"z\":0},"
            + "{\"x\":93.2560090111,\"y\":89.900584593,\"z\":0}],"
            + "\"mtext_attachment\":\"BottomRight\","
            + "\"mtext_color_index\":7,"
            + "\"mtext_height\":1.5,"
            + "\"mtext_layer\":\"MCP08_TEXT\","
            + "\"mtext_location_wcs\":{\"x\":93.627203766,\"y\":89.95387111977,\"z\":0},"
            + "\"mtext_rotation\":0.14258,"
            + "\"mtext_style\":\"_KCYD_DIM\","
            + "\"mtext_width\":0,"
            + "\"plan_id\":\"road08-r01-v4\","
            + "\"readback_validator_version\":\"road08-r01-readback-v4\","
            + "\"resource_creation_policy\":\"create_missing_exact_v1\","
            + "\"source_geometry_sha256\":{"
            + "\"8530\":\"E01DC7C7D4CD20C1AFCB3D83845930879EDB40D1EBF61CB3C0C3C7BEA1C800B4\","
            + "\"8531\":\"48EDA9633A21210630656D12D31740464076F7414B8607F08F17BCF78CF1AE9E\","
            + "\"8532\":\"F20978E369FEA65C600308C9FE6E9BFA7BA3B24DD0550495407C3565911DD3D4\"},"
            + "\"source_handles\":[\"8530\",\"8531\",\"8532\"],"
            + "\"source_layer\":\"ROAD\","
            + "\"target_layer_color_index\":7,"
            + "\"target_layer_color_method\":\"ByAci\","
            + "\"target_layer_is_frozen\":false,"
            + "\"target_layer_is_locked\":false,"
            + "\"target_layer_is_off\":false,"
            + "\"target_layer_is_plottable\":true,"
            + "\"target_layer_linetype\":\"Continuous\","
            + "\"text\":\"\\\\A1;R01 拆除结构1 A=27.9m²\","
            + "\"text_big_font_file\":\"gbcbig.shx\","
            + "\"text_font_file\":\"gbenor.shx\"}";

        public static readonly string[] SourceHandles = { "8530", "8531", "8532" };

        // The final vertex is the native AutoCAD endpoint after the LEADER is
        // associated with this rotated MText and its configured gap.
        public static readonly double[][] LeaderVerticesWcs =
        {
            new[] { 74.88270796597, 76.7816643141, 0d },
            new[] { 78.4109973181, 87.39067813475, 0d },
            new[] { 93.2560090111, 89.900584593, 0d }
        };

        public static readonly double[] TextLocationWcs =
        {
            93.627203766, 89.95387111977, 0d
        };

        public const double TextHeight = 1.5d;
        public const double TextWidth = 0d;
        public const double TextRotation = 0.14258d;
        public const short TextColorIndex = 7;
        public const short LeaderColorIndex = 2;
        public const string Attachment = "BottomRight";
        public const double LeaderArrowSize = 0.375d;
        public const double LeaderScale = 1d;
        public const double LeaderGap = 0.375d;
        public const short LeaderTextAbove = 0;
        public const double LeaderTextHeight = 1.5d;

        // Frozen from the Road08 D1 input copy's read-only source-inventory
        // capture. The digest covers exact vertices, bulges, per-segment widths,
        // closed state, type, layer, and ACI color method/index.
        public const string Source8530GeometrySha256 = "E01DC7C7D4CD20C1AFCB3D83845930879EDB40D1EBF61CB3C0C3C7BEA1C800B4";
        public const string Source8531GeometrySha256 = "48EDA9633A21210630656D12D31740464076F7414B8607F08F17BCF78CF1AE9E";
        public const string Source8532GeometrySha256 = "F20978E369FEA65C600308C9FE6E9BFA7BA3B24DD0550495407C3565911DD3D4";

        public static string ExpectedSourceGeometrySha256(string handle) => handle switch
        {
            "8530" => Source8530GeometrySha256,
            "8531" => Source8531GeometrySha256,
            "8532" => Source8532GeometrySha256,
            _ => null
        };

        /// <summary>Hashes the exact source identity/geometry fields used by the frozen D1 baseline.</summary>
        public static string ComputeSourceGeometrySha256(JObject source)
        {
            if (source == null || source["color"] is not JObject color
                || source["closed"]?.Type != JTokenType.Boolean
                || source["vertices"] is not JArray vertices
                || source["bulges"] is not JArray bulges
                || source["start_widths"] is not JArray startWidths
                || source["end_widths"] is not JArray endWidths
                || vertices.Count < 2 || bulges.Count != vertices.Count
                || startWidths.Count != vertices.Count || endWidths.Count != vertices.Count
                || source["handle"]?.Type != JTokenType.String
                || source["type"]?.Type != JTokenType.String
                || source["layer"]?.Type != JTokenType.String
                || color["method"]?.Type != JTokenType.String
                || color["aci"]?.Type != JTokenType.Integer)
                return null;

            var signature = new JObject
            {
                ["handle"] = source["handle"].DeepClone(),
                ["type"] = source["type"].DeepClone(),
                ["layer"] = source["layer"].DeepClone(),
                ["color_method"] = color["method"].DeepClone(),
                ["color_aci"] = color["aci"].DeepClone(),
                ["closed"] = source["closed"].DeepClone(),
                ["vertices"] = vertices.DeepClone(),
                ["bulges"] = bulges.DeepClone(),
                ["start_widths"] = startWidths.DeepClone(),
                ["end_widths"] = endWidths.DeepClone()
            };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(signature, Formatting.None));
            using (SHA256 algorithm = SHA256.Create())
                return ToUpperHex(algorithm.ComputeHash(bytes));
        }

        /// <summary>
        /// Recomputes the stable shape digest from independent block-definition
        /// evidence and members, excluding only target-assigned identifiers.
        /// Names and handles are intentionally excluded; geometry and all other
        /// observed member properties remain part of the digest.
        /// </summary>
        public static string ComputeArrowBlockShapeSha256(JObject definition, JArray members)
        {
            if (definition == null || members == null || members.Count == 0
                || definition["origin"] is not JObject origin
                || definition["is_layout"]?.Type != JTokenType.Boolean
                || definition["is_anonymous"]?.Type != JTokenType.Boolean
                || definition["is_external_reference"]?.Type != JTokenType.Boolean
                || definition["is_overlay_reference"]?.Type != JTokenType.Boolean
                || definition["is_dynamic"]?.Type != JTokenType.Boolean)
                return null;

            var normalizedMembers = new List<JObject>();
            foreach (JObject member in members.OfType<JObject>())
            {
                JObject normalized = (JObject)member.DeepClone();
                RemoveArrowShapeIdentifiers(normalized);
                normalizedMembers.Add(normalized);
            }
            if (normalizedMembers.Count != members.Count)
                return null;
            normalizedMembers.Sort((left, right) => string.CompareOrdinal(
                JsonConvert.SerializeObject(left, Formatting.None),
                JsonConvert.SerializeObject(right, Formatting.None)));

            var signature = new JObject
            {
                ["origin"] = origin.DeepClone(),
                ["is_layout"] = definition["is_layout"].DeepClone(),
                ["is_anonymous"] = definition["is_anonymous"].DeepClone(),
                ["is_external_reference"] = definition["is_external_reference"].DeepClone(),
                ["is_overlay_reference"] = definition["is_overlay_reference"].DeepClone(),
                ["is_dynamic"] = definition["is_dynamic"].DeepClone(),
                ["members"] = new JArray(normalizedMembers)
            };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(signature, Formatting.None));
            using (SHA256 algorithm = SHA256.Create())
                return ToUpperHex(algorithm.ComputeHash(bytes));
        }

        /// <summary>
        /// Recomputes the per-drawing definition digest. Unlike the approved
        /// stable shape digest, this intentionally includes the fixed block name
        /// and AutoCAD-assigned member handles so preflight and post-commit
        /// evidence can prove the exact definition stayed unchanged.
        /// </summary>
        public static string ComputeArrowBlockGeometrySha256(JObject definition, JArray members)
        {
            if (definition == null || members == null
                || definition["name"]?.Type != JTokenType.String
                || definition["origin"] is not JObject origin
                || definition["is_layout"]?.Type != JTokenType.Boolean
                || definition["is_anonymous"]?.Type != JTokenType.Boolean
                || definition["is_external_reference"]?.Type != JTokenType.Boolean
                || definition["is_overlay_reference"]?.Type != JTokenType.Boolean
                || definition["is_dynamic"]?.Type != JTokenType.Boolean)
                return null;

            var signature = new JObject
            {
                ["name"] = definition["name"].DeepClone(),
                ["origin"] = origin.DeepClone(),
                ["is_layout"] = definition["is_layout"].DeepClone(),
                ["is_anonymous"] = definition["is_anonymous"].DeepClone(),
                ["is_external_reference"] = definition["is_external_reference"].DeepClone(),
                ["is_overlay_reference"] = definition["is_overlay_reference"].DeepClone(),
                ["is_dynamic"] = definition["is_dynamic"].DeepClone(),
                ["members"] = members.DeepClone()
            };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(signature, Formatting.None));
            using (SHA256 algorithm = SHA256.Create())
                return ToUpperHex(algorithm.ComputeHash(bytes));
        }

        private static void RemoveArrowShapeIdentifiers(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties().ToArray())
                {
                    if (string.Equals(property.Name, "handle", StringComparison.OrdinalIgnoreCase)
                        || property.Name.EndsWith("_handle", StringComparison.OrdinalIgnoreCase))
                    {
                        property.Remove();
                        continue;
                    }
                    RemoveArrowShapeIdentifiers(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken child in array)
                    RemoveArrowShapeIdentifiers(child);
            }
        }

        // This contract is linked into AutoCAD 2022–2025 net48 plugins as well
        // as the net8 server. Keep hashing independent of newer BCL helpers.
        private static string ToUpperHex(byte[] bytes)
        {
            const string alphabet = "0123456789ABCDEF";
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = alphabet[bytes[i] >> 4];
                chars[i * 2 + 1] = alphabet[bytes[i] & 0x0F];
            }
            return new string(chars);
        }
    }
}
