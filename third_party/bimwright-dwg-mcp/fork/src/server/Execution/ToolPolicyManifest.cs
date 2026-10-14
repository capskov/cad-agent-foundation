using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimwright.Dwg.Server.Execution
{
    public enum ToolEffectClass
    {
        DrawingRead,
        DrawingWrite,
        ViewWrite,
        FileWrite,
        TargetControl,
        LocalRead,
        LocalWrite,
        Composite,
        Refused
    }

    public sealed class ToolPolicy
    {
        public ToolPolicy(ToolEffectClass effect, bool requiresContext, bool requiresRequestId, bool enabledByDefault)
        {
            Effect = effect;
            RequiresContext = requiresContext;
            RequiresRequestId = requiresRequestId;
            EnabledByDefault = enabledByDefault;
        }

        public ToolEffectClass Effect { get; }
        public bool RequiresContext { get; }
        public bool RequiresRequestId { get; }
        public bool EnabledByDefault { get; }
    }

    /// <summary>
    /// Explicit safety classification for the pinned Bimwright 2.0.1 command
    /// catalog. MCP ReadOnly annotations are descriptive and are not used to
    /// infer whether a tool has side effects.
    /// </summary>
    public static class ToolPolicyManifest
    {
        private static readonly IReadOnlyDictionary<string, ToolPolicy> Policies = Build();

        public const int ExpectedUpstreamToolCount = 65;

        public static IReadOnlyDictionary<string, ToolPolicy> All => Policies;

        public static ToolPolicy Require(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !Policies.TryGetValue(name, out var policy))
                throw new InvalidOperationException("Tool is missing from the pinned safety manifest: " + (name ?? "<null>"));
            return policy;
        }

        public static void ValidateExactCatalog(IEnumerable<string> toolNames)
        {
            var names = (toolNames ?? Array.Empty<string>()).ToArray();
            var actual = new HashSet<string>(names, StringComparer.Ordinal);
            var expected = new HashSet<string>(Policies.Keys, StringComparer.Ordinal);
            if (actual.Count != names.Length)
                throw new InvalidOperationException("The Bimwright tool catalog contains duplicate names.");

            var missing = expected.Except(actual, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var unexpected = actual.Except(expected, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (missing.Length != 0 || unexpected.Length != 0)
            {
                throw new InvalidOperationException(
                    "The pinned tool policy manifest does not match this catalog. Missing: ["
                    + string.Join(",", missing) + "]; unexpected: [" + string.Join(",", unexpected) + "].");
            }
        }

        private static IReadOnlyDictionary<string, ToolPolicy> Build()
        {
            var result = new Dictionary<string, ToolPolicy>(StringComparer.Ordinal);

            // Only explicitly bound, request-tracked reads are enabled. Each
            // addition is still allowlisted by the execution gateway.
            Add(result, ToolEffectClass.DrawingRead, context: true, request: false, enabled: false,
                "dwg_get_drawing_info",
                "dwg_count_entities", "dwg_select_by_layer", "dwg_select_by_type", "dwg_get_selected_texts",
                "dwg_list_blocks", "dwg_get_block_attributes", "dwg_get_variables");

            Add(result, ToolEffectClass.DrawingRead, context: true, request: true, enabled: true,
                "dwg_list_layers", "dwg_get_entity_properties", "dwg_query_entities");

            Add(result, ToolEffectClass.DrawingWrite, context: true, request: true, enabled: false,
                "dwg_update_texts", "dwg_create_layer", "dwg_create_line", "dwg_create_circle",
                "dwg_create_point", "dwg_create_polyline", "dwg_create_rectangle", "dwg_create_arc",
                "dwg_create_ellipse", "dwg_change_layer", "dwg_change_color", "dwg_move_entities",
                "dwg_rotate_entities", "dwg_scale_entities", "dwg_copy_entities", "dwg_erase_entities",
                "dwg_offset_entities", "dwg_translate_and_rewrite", "dwg_apply_unicode_style",
                "dwg_collapse_and_rewrite", "dwg_create_text", "dwg_create_mtext", "dwg_create_leader",
                "dwg_create_table", "dwg_insert_block", "dwg_set_block_attributes", "dwg_explode_block",
                "dwg_create_linear_dimension", "dwg_create_aligned_dimension", "dwg_create_radial_dimension",
                "dwg_create_diameter_dimension", "dwg_set_system_variable", "dwg_save_drawing",
                "dwg_purge_drawing");

            Add(result, ToolEffectClass.ViewWrite, context: true, request: true, enabled: false,
                "dwg_zoom_extents", "dwg_zoom_window", "dwg_zoom_to_entity", "dwg_inspect_view_region", "dwg_restore_view");

            Add(result, ToolEffectClass.FileWrite, context: true, request: true, enabled: false,
                "dwg_export_dxf", "dwg_capture_view_image");

            Add(result, ToolEffectClass.TargetControl, context: false, request: false, enabled: true,
                "dwg_list_available_targets", "dwg_get_current_target", "dwg_switch_target");

            Add(result, ToolEffectClass.LocalRead, context: false, request: false, enabled: true,
                "dwg_inspect_lisp", "dwg_list_baked_tools", "dwg_list_bake_suggestions");

            Add(result, ToolEffectClass.LocalWrite, context: false, request: true, enabled: false,
                "dwg_create_bake_issue_draft", "dwg_accept_bake_suggestion", "dwg_dismiss_bake_suggestion");

            Add(result, ToolEffectClass.Composite, context: true, request: true, enabled: false,
                "dwg_batch_execute", "dwg_run_baked_tool");

            Add(result, ToolEffectClass.Refused, context: true, request: true, enabled: false,
                "dwg_send_code", "dwg_run_lisp");

            if (result.Count != ExpectedUpstreamToolCount)
                throw new InvalidOperationException("Pinned Bimwright safety manifest expected " + ExpectedUpstreamToolCount + " tools but classified " + result.Count + ".");

            return result;
        }

        private static void Add(
            IDictionary<string, ToolPolicy> policies,
            ToolEffectClass effect,
            bool context,
            bool request,
            bool enabled,
            params string[] names)
        {
            foreach (var name in names)
            {
                if (!policies.TryAdd(name, new ToolPolicy(effect, context, request, enabled)))
                    throw new InvalidOperationException("Duplicate tool policy: " + name);
            }
        }
    }
}
