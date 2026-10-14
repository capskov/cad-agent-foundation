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
                || !string.Equals(context.Discovery.Transport, "pipe", StringComparison.OrdinalIgnoreCase)
                || context.Discovery.AcadYear != 2026
                || !string.Equals(context.Discovery.Target, "2026", StringComparison.Ordinal)
                || !context.TryGetJournalScopeIdentity(required: true, out string scopeId,
                    out string pathDigest, out _)
                || !string.Equals(scopeId, JournalScopeId, StringComparison.Ordinal)
                || !string.Equals(pathDigest, JournalPathDigest, StringComparison.Ordinal)
                || !context.TryGetDrawingPathRiskKey(out string riskKey, out _))
                return false;

            return string.Equals(riskKey, DrawingRiskKey, StringComparison.Ordinal);
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
