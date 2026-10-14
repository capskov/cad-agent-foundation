using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Bimwright.Dwg.Server.Execution;
using ModelContextProtocol.Server;

namespace Bimwright.Dwg.Server.Tools
{
    /// <summary>
    /// Foundation control plane for explicit document binding and durable job
    /// snapshots. These controls are not part of the pinned upstream catalog.
    /// </summary>
    [McpServerToolType]
    public static class FoundationExecutionTools
    {
        [McpServerTool(Name = "dwg_bind_active_document", ReadOnly = true, Idempotent = false), Description(
            "Explicitly bind the selected AutoCAD plugin endpoint and active document. Returns the existing opaque context_id and an allowlisted binding_snapshot of its captured process/document identity. " +
            "Check owner_pid, owner_started_at_utc and drawing_path against your expected target before reading. This is a bind-time snapshot, not live CAD readiness; unavailable layout, space, document read-only state and plugin build are null and listed in unknown_fields.")]
        public static Task<string> BindActiveDocument()
            => BoundPluginExecutionGateway.Shared.BindActiveDocumentAsync();

        [McpServerTool(Name = "dwg_job_get", ReadOnly = true, Idempotent = true), Description(
            "Return one immediate snapshot for a job owned by the supplied context_id and request_id. This pilot accepts only wait_ms 0.")]
        public static Task<string> GetJob(int wait_ms = 0)
        {
            var invocation = ToolInvocationScope.Current;
            if (invocation == null || invocation.ToolName != "dwg_job_get")
                return Task.FromResult(Rejected("dwg_job_get requires its governed invocation scope"));
            return BoundPluginExecutionGateway.Shared.GetJobAsync(invocation.ContextId, invocation.RequestId, wait_ms);
        }

        [McpServerTool(Name = "dwg_job_get_historical", ReadOnly = true, Idempotent = true), Description(
            "Read a persisted dwg_list_layers request from the plugin's shared history through another explicitly bound pipe observer. This performs one query only and never resubmits, cancels, or takes ownership of the old request.")]
        public static Task<string> GetHistoricalJob(
            [Description("A different context_id returned by dwg_bind_active_document for the endpoint that will inspect the old plugin journal.")] string observer_context_id)
        {
            var invocation = ToolInvocationScope.Current;
            if (invocation == null || invocation.ToolName != "dwg_job_get_historical")
                return Task.FromResult(Rejected("dwg_job_get_historical requires its governed invocation scope"));
            return BoundPluginExecutionGateway.Shared.GetHistoricalJobAsync(
                invocation.ContextId, invocation.RequestId, observer_context_id);
        }

        [McpServerTool(Name = "dwg_job_cancel", ReadOnly = true, Idempotent = false), Description(
            "Cancel a job only while it remains queued, after validating that request_id belongs to context_id.")]
        public static Task<string> CancelJob()
        {
            var invocation = ToolInvocationScope.Current;
            if (invocation == null || invocation.ToolName != "dwg_job_cancel")
                return Task.FromResult(Rejected("dwg_job_cancel requires its governed invocation scope"));
            return BoundPluginExecutionGateway.Shared.CancelJobAsync(invocation.ContextId, invocation.RequestId);
        }

        public static IReadOnlyList<McpServerTool> CreateToolInstances()
        {
            var methods = typeof(FoundationExecutionTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null);
            return methods.Select(method =>
            {
                var policy = method.GetCustomAttribute<McpServerToolAttribute>().Name switch
                {
                    "dwg_bind_active_document" => (Context: false, Request: false),
                    "dwg_job_get" => (Context: true, Request: true),
                    "dwg_job_get_historical" => (Context: true, Request: true),
                    "dwg_job_cancel" => (Context: true, Request: true),
                    _ => throw new System.InvalidOperationException("Unknown foundation control tool: " + method.Name)
                };
                var inner = McpServerTool.Create(method, (object)null, new McpServerToolCreateOptions());
                return (McpServerTool)new CommonArgumentMcpServerTool(inner, policy.Context, policy.Request);
            }).ToArray();
        }

        private static string Rejected(string message) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                id = System.Guid.NewGuid().ToString("N"),
                ok = false,
                result = (object)null,
                error = message
            });
    }
}
