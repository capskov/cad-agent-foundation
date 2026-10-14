using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;

namespace Bimwright.Dwg.Server.Execution
{
    /// <summary>
    /// Validates the complete pinned upstream tool catalog before selecting
    /// the small set of tool instances that this server is allowed to expose.
    /// Catalog validation and user configuration are deliberately separate.
    /// </summary>
    public static class GovernedToolFactory
    {
        public static IReadOnlyList<McpServerTool> Create(
            IEnumerable<Type> pinnedToolTypes,
            IEnumerable<string> enabledToolsets,
            bool readOnly)
        {
            var methods = ValidateAndGetMethods(pinnedToolTypes);
            var enabled = new HashSet<string>(enabledToolsets ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var result = new List<McpServerTool>();

            foreach (var item in methods)
            {
                var attribute = item.Attribute;
                var policy = ToolPolicyManifest.Require(attribute.Name);
                if (!IsSelected(attribute.Name, policy, enabled, readOnly))
                    continue;

                if (!item.Method.IsStatic)
                    throw new InvalidOperationException("Pinned Bimwright tool method must be static: " + attribute.Name);

                var innerTool = McpServerTool.Create(item.Method, (object)null, new McpServerToolCreateOptions());
                result.Add(new CommonArgumentMcpServerTool(
                    innerTool,
                    policy.RequiresContext,
                    policy.RequiresRequestId));
            }

            return result;
        }

        /// <summary>
        /// Checks exact upstream catalog identity independently of the selected
        /// toolsets. The returned methods can then be filtered safely.
        /// </summary>
        public static void ValidateExactCatalog(IEnumerable<Type> pinnedToolTypes)
        {
            _ = ValidateAndGetMethods(pinnedToolTypes);
        }

        private static IReadOnlyList<(MethodInfo Method, McpServerToolAttribute Attribute)> ValidateAndGetMethods(
            IEnumerable<Type> pinnedToolTypes)
        {
            var types = (pinnedToolTypes ?? Array.Empty<Type>()).ToArray();
            var methods = types
                .Where(type => type != null)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
                .Where(item => item.Attribute != null && !string.IsNullOrWhiteSpace(item.Attribute.Name))
                .Select(item => (item.Method, Attribute: item.Attribute!))
                .ToArray();

            var names = methods.Select(item => item.Attribute.Name).ToArray();
            ToolPolicyManifest.ValidateExactCatalog(names);

            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in methods)
            {
                if (!seenNames.Add(item.Attribute.Name))
                    throw new InvalidOperationException("Duplicate MCP tool name: " + item.Attribute.Name);
                _ = ToolPolicyManifest.Require(item.Attribute.Name);
            }

            return methods;
        }

        private static bool IsSelected(string name, ToolPolicy policy, HashSet<string> enabled, bool readOnly)
        {
            if (policy.Effect == ToolEffectClass.Refused
                || policy.Effect == ToolEffectClass.DrawingWrite
                || policy.Effect == ToolEffectClass.ViewWrite
                || policy.Effect == ToolEffectClass.FileWrite
                || policy.Effect == ToolEffectClass.LocalWrite
                || policy.Effect == ToolEffectClass.Composite
                || readOnly && policy.Effect != ToolEffectClass.DrawingRead
                    && policy.Effect != ToolEffectClass.TargetControl
                    && policy.Effect != ToolEffectClass.LocalRead)
            {
                return false;
            }

            if (!policy.EnabledByDefault)
                return false;

            switch (policy.Effect)
            {
                case ToolEffectClass.DrawingRead:
                    return BoundPluginExecutionGateway.IsGovernedReadToolName(name)
                        && enabled.Contains("query");
                case ToolEffectClass.TargetControl:
                    return enabled.Contains("meta");
                case ToolEffectClass.LocalRead:
                    if (string.Equals(name, "dwg_inspect_lisp", StringComparison.Ordinal))
                        return enabled.Contains("query");
                    if (string.Equals(name, "dwg_list_baked_tools", StringComparison.Ordinal)
                        || string.Equals(name, "dwg_list_bake_suggestions", StringComparison.Ordinal))
                        return enabled.Contains("toolbaker");
                    return false;
                default:
                    return false;
            }
        }
    }
}
