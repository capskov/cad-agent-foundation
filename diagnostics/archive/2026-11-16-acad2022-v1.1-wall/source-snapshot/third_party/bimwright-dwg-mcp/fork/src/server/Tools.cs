// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Bimwright.Dwg.Server.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server
{
    /// <summary>
    /// Compatibility bridge for the pinned upstream tool handlers. It is
    /// intentionally fail-closed: only explicitly governed drawing-read
    /// invocation scopes can reach the queued plugin read endpoint.
    /// </summary>
    internal static class ToolGateway
    {
        internal static async Task<string> LoggedCall(string toolName, object inputParams, object pluginParams)
        {
            var invocation = ToolInvocationScope.Current;
            if (invocation == null
                || !BoundPluginExecutionGateway.IsGovernedReadToolName(invocation.ToolName)
                || !ServerState.IsToolRegistered(invocation.ToolName)
                || !ToolNamesMatch(invocation.ToolName, toolName))
            {
                return Denied(toolName, "no matching governed tool invocation scope");
            }

            var stopwatch = Stopwatch.StartNew();
            ServerLogger.LogStart(invocation.RequestId, invocation.ToolName, SafeSerialize(inputParams));
            try
            {
                BoundPluginExecutionGateway gateway = BoundPluginExecutionGateway.Shared;
                string response;
                if (string.Equals(invocation.ToolName, BoundPluginExecutionGateway.PilotMcpToolName, StringComparison.Ordinal))
                    response = await gateway.SubmitListLayersAsync(invocation, inputParams, pluginParams).ConfigureAwait(false);
                else if (string.Equals(invocation.ToolName, BoundPluginExecutionGateway.EntityPropertiesMcpToolName, StringComparison.Ordinal))
                    response = await gateway.SubmitEntityPropertiesAsync(invocation, inputParams, pluginParams).ConfigureAwait(false);
                else if (string.Equals(invocation.ToolName, BoundPluginExecutionGateway.QueryEntitiesMcpToolName, StringComparison.Ordinal))
                    response = await gateway.SubmitQueryEntitiesAsync(invocation, inputParams, pluginParams).ConfigureAwait(false);
                else
                    return Denied(toolName, "governed tool has no explicit handler");
                stopwatch.Stop();
                bool ok = false;
                string error = null;
                try
                {
                    var envelope = JObject.Parse(response);
                    ok = envelope.Value<bool?>("ok") == true;
                    error = envelope.Value<string>("error");
                }
                catch
                {
                    error = "governed tool returned an invalid response envelope";
                }
                ServerLogger.LogFinish(invocation.RequestId, invocation.ToolName, ok, stopwatch.ElapsedMilliseconds, error);
                return response;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                ServerLogger.LogFinish(invocation.RequestId, invocation.ToolName, false, stopwatch.ElapsedMilliseconds, exception.Message);
                return Denied(toolName, "governed plugin job submission failed closed");
            }
        }

        /// <summary>
        /// The legacy raw-command path has no governed job semantics and is
        /// therefore never allowed to send a plugin command.
        /// </summary>
        internal static Task<McpResponse> SendRaw(string toolName, object pluginParams, string requestId = null)
        {
            return Task.FromResult(new McpResponse
            {
                Id = Guid.NewGuid().ToString("N"),
                Ok = false,
                Result = null,
                Error = "raw plugin commands are disabled; use an explicitly governed tool"
            });
        }

        private static string Denied(string toolName, string reason) => JsonConvert.SerializeObject(new
        {
            id = Guid.NewGuid().ToString("N"),
            ok = false,
            result = (object)null,
            error = "Tool '" + (toolName ?? "<null>") + "' was denied: " + reason
        });

        private static string SafeSerialize(object value)
        {
            try { return JsonConvert.SerializeObject(value); }
            catch { return "<unserializable>"; }
        }

        private static bool ToolNamesMatch(string mcpToolName, string pluginToolName) =>
            string.Equals(mcpToolName, BoundPluginExecutionGateway.PilotMcpToolName, StringComparison.Ordinal)
                ? string.Equals(pluginToolName, BoundPluginExecutionGateway.PilotPluginToolName, StringComparison.Ordinal)
                : string.Equals(mcpToolName, BoundPluginExecutionGateway.EntityPropertiesMcpToolName, StringComparison.Ordinal)
                    ? string.Equals(pluginToolName, BoundPluginExecutionGateway.EntityPropertiesPluginToolName, StringComparison.Ordinal)
                    : string.Equals(mcpToolName, BoundPluginExecutionGateway.QueryEntitiesMcpToolName, StringComparison.Ordinal)
                        && string.Equals(pluginToolName, BoundPluginExecutionGateway.QueryEntitiesPluginToolName, StringComparison.Ordinal);
    }
}
