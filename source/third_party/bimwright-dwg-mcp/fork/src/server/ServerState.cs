// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.Linq;
using ModelContextProtocol.Server;
using Bimwright.Dwg.Server.Execution;
using Newtonsoft.Json;

namespace Bimwright.Dwg.Server
{
    public static class ServerState
    {
        public static DwgMcpConfig Config { get; set; } = new DwgMcpConfig();

        /// <summary>The exact governed MCP tool instances registered with the SDK.</summary>
        public static IReadOnlyList<McpServerTool> RegisteredTools { get; set; } = Array.Empty<McpServerTool>();

        /// <summary>Registered drawing tools that the plugin may accept from this server.</summary>
        public static IReadOnlyList<McpServerTool> PluginCallableDrawingTools { get; set; } = Array.Empty<McpServerTool>();

        public static bool IsToolRegistered(string name) => RegisteredTools.Any(tool =>
            string.Equals(tool?.ProtocolTool?.Name, name, StringComparison.Ordinal));

        public static bool IsReadOnly => Config?.ReadOnlyOrDefault ?? false;

        public static string ReadOnlyError(string toolName)
            => JsonConvert.SerializeObject(new
            {
                ok = false,
                error = $"Tool '{toolName}' is disabled because BIMWRIGHT_DWG_READ_ONLY is enabled."
            });
    }
}
