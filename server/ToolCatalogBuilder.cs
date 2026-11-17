// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Bimwright.Dwg.Server.Execution;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server
{
    /// <summary>
    /// Builds the plugin catalog from the actual governed drawing-tool
    /// instances registered by the MCP server. Foundation controls and hidden
    /// methods on upstream tool classes are not included.
    /// </summary>
    public static class ToolCatalogBuilder
    {
        public const int TimeoutSeconds = 30;

        public static JObject Build(IEnumerable<McpServerTool> registeredDrawingTools)
        {
            var tools = new List<JObject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in registeredDrawingTools ?? Array.Empty<McpServerTool>())
            {
                string name = tool?.ProtocolTool?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (!seen.Add(name))
                    throw new InvalidOperationException("Duplicate registered plugin drawing tool: " + name);

                tools.Add(new JObject
                {
                    ["name"] = name,
                    ["description"] = tool.ProtocolTool.Description ?? string.Empty,
                    ["source"] = "built-in",
                    ["timeout_seconds"] = TimeoutSeconds
                });
            }

            tools.Sort((a, b) => string.Compare(a.Value<string>("name"), b.Value<string>("name"), StringComparison.OrdinalIgnoreCase));
            return new JObject
            {
                ["schema_version"] = 1,
                ["tools"] = new JArray(tools)
            };
        }

        /// <summary>
        /// Kept for source compatibility with the old standalone catalog unit
        /// test. Production registration and catalog synchronization use the
        /// governed-instance overload above.
        /// </summary>
        [Obsolete("Production catalogs must be built from registered governed tool instances.")]
        public static JObject Build(IEnumerable<Type> toolTypes)
        {
            var tools = new List<JObject>();
            foreach (var type in toolTypes ?? Array.Empty<Type>())
            {
                if (type == null)
                    continue;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var tool = method.GetCustomAttribute<McpServerToolAttribute>();
                    if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
                        continue;
                    var description = method.GetCustomAttribute<DescriptionAttribute>();
                    tools.Add(new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = description == null ? string.Empty : description.Description ?? string.Empty,
                        ["source"] = "built-in",
                        ["timeout_seconds"] = TimeoutSeconds
                    });
                }
            }

            tools.Sort((a, b) => string.Compare(a.Value<string>("name"), b.Value<string>("name"), StringComparison.OrdinalIgnoreCase));
            return new JObject
            {
                ["schema_version"] = 1,
                ["tools"] = new JArray(tools)
            };
        }
    }
}
