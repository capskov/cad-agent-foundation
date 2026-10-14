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
    /// <summary>Fixed-input, read-only candidate inspection for the Road08 R01 D1 pilot.</summary>
    [McpServerToolType]
    public static class D1Road08PreflightTools
    {
        [McpServerTool(Name = Road08R01D1WriteContract.PreflightMcpToolName, ReadOnly = true, Idempotent = true),
         Description("Read the fixed Road08 R01 D1 prerequisites and candidate arrow-block geometry from the exact bound AutoCAD 2026 test copy. This inspection is read-only and never approves a write baseline. No drawing path, block name, hash, or geometry is accepted from the caller.")]
        public static Task<string> InspectR01()
        {
            ToolInvocationEnvelope invocation = ToolInvocationScope.Current;
            if (invocation == null || invocation.ToolName != Road08R01D1WriteContract.PreflightMcpToolName)
            {
                return Task.FromResult(Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    id = System.Guid.NewGuid().ToString("N"),
                    ok = false,
                    result = (object)null,
                    error = "dwg_d1_preflight_r01 requires its governed invocation scope"
                }));
            }

            return BoundPluginExecutionGateway.Shared.SubmitD1PreflightAsync(invocation);
        }

        public static IReadOnlyList<McpServerTool> CreateToolInstances()
        {
            var methods = typeof(D1Road08PreflightTools)
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
