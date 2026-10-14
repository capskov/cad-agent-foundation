using System;
using System.Threading.Tasks;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server.Execution
{
    // Reuse the already validated path/journal/pipe scope; no new ledger or fence.
    public sealed class H1PreflightPilotScope
    {
        private readonly D1PreflightPilotScope _readScope;
        public H1PreflightPilotScope(string path, string scopeId, string digest) =>
            _readScope = new D1PreflightPilotScope(path, scopeId, digest);
        public bool Matches(BoundPluginContext context) => _readScope.Matches(context);
    }

    public sealed partial class BoundPluginExecutionGateway
    {
        public Task<string> SubmitH1PreflightAsync(ToolInvocationEnvelope invocation)
        {
            if (invocation == null || invocation.ToolName != H1FixedHatchPreflightContract.McpToolName
                || invocation.BusinessArguments == null || invocation.BusinessArguments.Count != 0
                || !IsValidIdentifier(invocation.ContextId) || !IsValidIdentifier(invocation.RequestId)
                || (_h1PreflightPilotScope == null && _h1WritePilotScope == null) || _ledger == null
                || !ServerState.IsToolRegistered(H1FixedHatchPreflightContract.McpToolName))
                return Task.FromResult(Envelope(Guid.NewGuid().ToString("N"), false, null, "the fixed H1 read-only preflight is not enabled or the invocation is invalid"));
            return SubmitGovernedReadAsync(invocation, new JObject(), new JObject());
        }

        private bool MatchesH1PreflightScope(BoundPluginContext bound) =>
            (_h1PreflightPilotScope != null && _h1PreflightPilotScope.Matches(bound) || _h1WritePilotScope != null && _h1WritePilotScope.Matches(bound))
            && bound.TryGetOwnerIdentity(out _, out _);
    }
}
