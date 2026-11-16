using System;
using System.Linq;
using Bimwright.Dwg.Shared.Execution;

namespace Bimwright.Dwg.Server.Execution
{
    /// <summary>
    /// Exact local drawing and plugin-journal scope for the isolated Road08
    /// D1 pilot. It is configured explicitly and never inferred from discovery.
    /// </summary>
    public sealed class D1WritePilotScope
    {
        public D1WritePilotScope(string expectedDrawingPath, string journalScopeId,
            string journalPathDigest)
        {
            if (!Bimwright.Dwg.Shared.Execution.DrawingRiskKey.TryNormalizePath(expectedDrawingPath, out string riskKey)
                || !IsLocalDrivePath(riskKey))
                throw new ArgumentException("D1 writes require one explicit local saved-DWG path.", nameof(expectedDrawingPath));
            if (!Guid.TryParseExact(journalScopeId, "N", out Guid scopeGuid)
                || scopeGuid == Guid.Empty
                || !string.Equals(journalScopeId, scopeGuid.ToString("N"), StringComparison.Ordinal))
                throw new ArgumentException("D1 writes require the exact configured plugin journal scope ID.", nameof(journalScopeId));
            if (journalPathDigest == null || journalPathDigest.Length != 64
                || journalPathDigest.Any(character => !(character >= '0' && character <= '9')
                    && !(character >= 'A' && character <= 'F')))
                throw new ArgumentException("D1 writes require the exact uppercase SHA-256 journal path digest.", nameof(journalPathDigest));

            DrawingRiskKey = riskKey;
            JournalScopeId = journalScopeId;
            JournalPathDigest = journalPathDigest;
        }

        public string DrawingRiskKey { get; }
        public string JournalScopeId { get; }
        public string JournalPathDigest { get; }

        public bool Matches(BoundPluginContext context)
        {
            if (context == null
                || !HasAuthenticatedControlChannel(context.Discovery)
                || !IsSupportedWriteHost(context.Discovery)
                || !context.TryGetJournalScopeIdentity(required: true, out string scopeId,
                    out string pathDigest, out _)
                || !string.Equals(scopeId, JournalScopeId, StringComparison.Ordinal)
                || !string.Equals(pathDigest, JournalPathDigest, StringComparison.Ordinal)
                || !context.TryGetDrawingPathRiskKey(out string riskKey, out _))
                return false;

            return string.Equals(riskKey, DrawingRiskKey, StringComparison.Ordinal);
        }

        // A governed write needs a channel the caller cannot forge: the plugin's
        // own pipe transport, or an independent control pipe published beside a
        // TCP data channel (the migrated AutoCAD 2022 host). Tokens alone are not
        // treated as an authenticating channel.
        internal static bool HasAuthenticatedControlChannel(DiscoveryInfo discovery)
        {
            if (discovery == null) return false;
            if (string.Equals(discovery.Transport, "pipe", StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrWhiteSpace(discovery.ControlPipeName);
        }

        // Host release year is a capability fact, not the authority gate: the exact
        // drawing path, journal scope and digest above remain the authority. A host
        // whose declared target/year disagree, or that is outside the reviewed set,
        // is refused.
        internal static bool IsSupportedWriteHost(DiscoveryInfo discovery)
        {
            if (discovery == null) return false;
            int year = discovery.AcadYear;
            if (year != 2022 && year != 2026) return false;
            return string.Equals(discovery.Target, year.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private static bool IsLocalDrivePath(string normalizedRiskKey)
        {
            if (normalizedRiskKey == null || !normalizedRiskKey.StartsWith("PATH:", StringComparison.Ordinal))
                return false;
            string path = normalizedRiskKey.Substring(5);
            return path.Length >= 3
                && (path[0] >= 'A' && path[0] <= 'Z')
                && path[1] == ':' && path[2] == '\\';
        }
    }
}
