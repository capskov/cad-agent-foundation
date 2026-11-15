using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Bimwright.Dwg.Server.Execution;
using Bimwright.Dwg.Shared.Execution;
using ModelContextProtocol.Server;

namespace Bimwright.Dwg.Server.Tools
{
    /// <summary>Isolated, fixed-input Road08 R01 write acceptance tool.</summary>
    [McpServerToolType]
    public static class D1Road08WriteTools
    {
        [McpServerTool(Name = Road08R01D1WriteContract.McpToolName, ReadOnly = false, Idempotent = false),
         Description("On the explicitly bound, configured Road08 test copy only, add the fixed R01 ordinary LEADER and MText callout. The operation checks source handles and style/block prerequisites before writing, then verifies the committed entities in a separate read transaction. No drawing path, geometry, style, or release evidence is accepted from the caller.")]
        public static Task<string> WriteR01()
        {
            ToolInvocationEnvelope invocation = ToolInvocationScope.Current;
            if (invocation == null || invocation.ToolName != Road08R01D1WriteContract.McpToolName)
            {
                return Task.FromResult(Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    id = System.Guid.NewGuid().ToString("N"),
                    ok = false,
                    result = (object)null,
                    error = "dwg_d1_write_r01 requires its governed invocation scope"
                }));
            }

            return BoundPluginExecutionGateway.Shared.SubmitD1WriteAsync(invocation);
        }

        public static IReadOnlyList<McpServerTool> CreateToolInstances()
        {
            var methods = typeof(D1Road08WriteTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null);
            return methods.Select(method =>
            {
                var inner = McpServerTool.Create(method, (object)null, new McpServerToolCreateOptions());
                return (McpServerTool)new CommonArgumentMcpServerTool(inner, true, true);
            }).ToArray();
        }
    }
}
