using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bimwright.Dwg.Server.Execution
{
    /// <summary>
    /// Adds the foundation's common call envelope to an existing Bimwright MCP
    /// tool while delegating its original schema and implementation unchanged.
    /// This adapter is registered only by the governed server path.
    /// </summary>
    public sealed class CommonArgumentMcpServerTool : DelegatingMcpServerTool
    {
        public const string ContextIdArgument = "context_id";
        public const string RequestIdArgument = "request_id";
        public const int MaxRequestIdLength = 128;
        public const int MaxContextIdLength = 128;

        private readonly Tool _protocolTool;
        private readonly bool _requiresContextId;
        private readonly bool _requiresRequestId;

        public CommonArgumentMcpServerTool(
            McpServerTool innerTool,
            bool requiresContextId,
            bool requiresRequestId)
            : base(innerTool ?? throw new ArgumentNullException(nameof(innerTool)))
        {
            _requiresContextId = requiresContextId;
            _requiresRequestId = requiresRequestId;
            _protocolTool = AddCommonArguments(innerTool.ProtocolTool, requiresContextId, requiresRequestId);
        }

        public override Tool ProtocolTool => _protocolTool;

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            if (request?.Params == null)
                return InputError("tool arguments are required");
            if (!string.Equals(request.Params.Name, _protocolTool.Name, StringComparison.Ordinal))
                return InputError("tool name does not match the governed instance");

            // MCP allows tools/call to omit arguments for a no-argument tool.
            // Treat that as an empty business argument object; required common
            // fields are still rejected by ReadString below.
            var originalArguments = request.Params.Arguments;
            var arguments = originalArguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var contextId = ReadString(arguments, ContextIdArgument, _requiresContextId, MaxContextIdLength, out var error);
            if (error != null)
                return InputError(error);

            var requestId = ReadString(arguments, RequestIdArgument, _requiresRequestId, MaxRequestIdLength, out error);
            if (error != null)
                return InputError(error);

            var businessArguments = arguments
                .Where(pair => !IsReservedArgument(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            // The SDK's generated method binder sees only the original Bimwright
            // business arguments. The stable envelope is retained in AsyncLocal
            // for the shared ToolGateway/queue integration performed in the next
            // migration slice.
            request.Params.Arguments = businessArguments;
            using (ToolInvocationScope.Push(new ToolInvocationEnvelope(
                _protocolTool.Name,
                contextId,
                requestId,
                CloneArguments(businessArguments))))
            {
                try
                {
                    return await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    request.Params.Arguments = originalArguments;
                }
            }
        }

        private static Tool AddCommonArguments(Tool original, bool requiresContextId, bool requiresRequestId)
        {
            if (original == null)
                throw new ArgumentNullException(nameof(original));

            var schema = JsonNode.Parse(original.InputSchema.GetRawText()) as JsonObject
                ?? throw new InvalidOperationException("MCP input schema must be a JSON object.");
            var properties = schema["properties"] as JsonObject;
            if (properties == null)
            {
                properties = new JsonObject();
                schema["properties"] = properties;
            }

            var required = schema["required"] as JsonArray;
            if (schema["required"] != null && required == null)
                throw new InvalidOperationException("MCP input schema required must be an array.");
            if (required == null)
            {
                required = new JsonArray();
                schema["required"] = required;
            }

            if (properties.ContainsKey(ContextIdArgument) || properties.ContainsKey(RequestIdArgument))
                throw new InvalidOperationException("A Bimwright tool schema already uses a reserved foundation argument.");

            if (requiresContextId)
            {
                properties[ContextIdArgument] = new JsonObject
                {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["maxLength"] = MaxContextIdLength,
                    ["description"] = "Opaque binding token returned by the explicit CAD document bind operation."
                };
                AddRequired(required, ContextIdArgument);
            }

            if (requiresRequestId)
            {
                properties[RequestIdArgument] = new JsonObject
                {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["maxLength"] = MaxRequestIdLength,
                    ["description"] = "Stable caller-generated ID for this job; reuse only to inspect or cancel this exact request."
                };
                AddRequired(required, RequestIdArgument);
            }

            // The pinned Bimwright catalog does not advertise MCP task execution.
            // Keep the SDK's preview-only Tool.Execution API out of this adapter.
            return new Tool
            {
                Name = original.Name,
                Title = original.Title,
                Description = original.Description,
                InputSchema = JsonSerializer.SerializeToElement(schema),
                OutputSchema = original.OutputSchema,
                Annotations = original.Annotations,
                Icons = original.Icons,
                Meta = original.Meta
            };
        }

        private static string ReadString(
            IDictionary<string, JsonElement> arguments,
            string name,
            bool required,
            int maxLength,
            out string error)
        {
            error = null;
            if (!arguments.TryGetValue(name, out var value))
            {
                if (required)
                    error = name + " is required";
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                error = name + " must be a string";
                return null;
            }

            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                if (required)
                    error = name + " must not be empty";
                return null;
            }

            if (text.Length > maxLength)
                error = name + " must be at most " + maxLength + " characters";
            else if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]) || text.Any(char.IsControl))
                error = name + " contains invalid whitespace or control characters";
            return text;
        }

        private static bool IsReservedArgument(string name) =>
            string.Equals(name, ContextIdArgument, StringComparison.Ordinal)
            || string.Equals(name, RequestIdArgument, StringComparison.Ordinal);

        private static void AddRequired(JsonArray required, string name)
        {
            if (!required.Any(node => string.Equals(node?.GetValue<string>(), name, StringComparison.Ordinal)))
                required.Add(name);
        }

        private static Dictionary<string, JsonElement> CloneArguments(IDictionary<string, JsonElement> args) =>
            args.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);

        private static CallToolResult InputError(string message) => new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = message }
            }
        };
    }

    public sealed class ToolInvocationEnvelope
    {
        public ToolInvocationEnvelope(
            string toolName,
            string contextId,
            string requestId,
            IReadOnlyDictionary<string, JsonElement> businessArguments)
        {
            ToolName = toolName;
            ContextId = contextId;
            RequestId = requestId;
            BusinessArguments = businessArguments;
        }

        public string ToolName { get; }
        public string ContextId { get; }
        public string RequestId { get; }
        public IReadOnlyDictionary<string, JsonElement> BusinessArguments { get; }
    }

    public static class ToolInvocationScope
    {
        private static readonly AsyncLocal<ToolInvocationEnvelope> CurrentValue = new AsyncLocal<ToolInvocationEnvelope>();

        public static ToolInvocationEnvelope Current => CurrentValue.Value;

        public static IDisposable Push(ToolInvocationEnvelope envelope)
        {
            var previous = CurrentValue.Value;
            CurrentValue.Value = envelope;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly ToolInvocationEnvelope _previous;
            private bool _disposed;

            public Scope(ToolInvocationEnvelope previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                CurrentValue.Value = _previous;
            }
        }
    }
}
