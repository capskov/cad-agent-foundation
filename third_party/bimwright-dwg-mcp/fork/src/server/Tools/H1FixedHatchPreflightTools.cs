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
    [McpServerToolType]
    public static class H1FixedHatchPreflightTools
    {
        [McpServerTool(Name = H1FixedHatchPreflightContract.McpToolName, ReadOnly = true, Idempotent = true),
         Description("Inspect only the frozen H1 rectangle/SOLID fixture in its exact AutoCAD 2026 drawing/journal scope. Reports undo recording, locks, stored geometry and reactor identities. Candidate-only: never authorizes or performs repair; accepts no handles, paths, geometry or code.")]
        public static Task<string> InspectFixedHatch()
        {
            ToolInvocationEnvelope invocation = ToolInvocationScope.Current;
            if (invocation == null || invocation.ToolName != H1FixedHatchPreflightContract.McpToolName)
                return Task.FromResult(Newtonsoft.Json.JsonConvert.SerializeObject(new
                { ok = false, result = (object)null, error = "H1 preflight requires its governed invocation scope" }));
            return BoundPluginExecutionGateway.Shared.SubmitH1PreflightAsync(invocation);
        }

        public static IReadOnlyList<McpServerTool> CreateToolInstances() => typeof(H1FixedHatchPreflightTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
            .Select(method => (McpServerTool)new CommonArgumentMcpServerTool(
                McpServerTool.Create(method, (object)null, new McpServerToolCreateOptions()), true, true)).ToArray();
    }
}
