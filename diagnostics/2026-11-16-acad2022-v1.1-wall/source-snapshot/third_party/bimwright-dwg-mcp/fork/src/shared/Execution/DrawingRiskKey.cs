using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bimwright.Dwg.Shared.Execution
{
    /// <summary>
    /// Cross-host canonical drawing-risk keys shared by the Server mirror and
    /// AutoCAD plugin journal. This is lexical normalization; trusted binding
    /// code must add physical file identity and historical aliases when known.
    /// </summary>
    public static class DrawingRiskKey
    {
        public static string Normalize(string riskKey)
        {
            if (string.IsNullOrEmpty(riskKey) || riskKey.Length > 2048 || riskKey.Any(char.IsControl)
                || riskKey != riskKey.Trim())
                throw new ArgumentException("Risk key length, whitespace, or contents are invalid.", nameof(riskKey));

            if (riskKey.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                return NormalizeAbsolutePath(riskKey.Substring(5));
            if (riskKey.StartsWith("alias:", StringComparison.OrdinalIgnoreCase))
            {
                string alias = riskKey.Substring(6).Normalize(NormalizationForm.FormC);
                if (string.IsNullOrWhiteSpace(alias) || alias.Length > 2040 || alias.Any(char.IsControl))
                    throw new ArgumentException("Historical aliases must be explicit and non-empty.", nameof(riskKey));
                return "ALIAS:" + alias.Replace('\\', '/').ToUpperInvariant();
            }
            return NormalizeAbsolutePath(riskKey);
        }

        public static bool TryNormalizePath(string value, out string pathRiskKey)
        {
            pathRiskKey = string.Empty;
            if (!IsAbsoluteWindowsPath(value))
                return false;
            pathRiskKey = NormalizeAbsolutePath(value);
            return true;
        }

        private static string NormalizeAbsolutePath(string value)
        {
            if (!IsAbsoluteWindowsPath(value))
                throw new ArgumentException("Risk keys must be absolute Windows paths or use an explicit alias: prefix.", nameof(value));
            if (value.Length > 2048 || value.Any(char.IsControl) || value != value.Trim())
                throw new ArgumentException("Windows path is empty, too long, or has unsafe whitespace.", nameof(value));

            string path = value.Replace('/', '\\');
            if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Extended and device namespace paths are unsupported.", nameof(value));

            string prefix;
            string remainder;
            if (path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            {
                if (path.IndexOf(':', 2) >= 0)
                    throw new ArgumentException("Alternate data streams are unsupported.", nameof(value));
                prefix = char.ToUpperInvariant(path[0]) + ":\\";
                remainder = path.Substring(3);
            }
            else if (path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                string[] unc = path.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (unc.Length < 2)
                    throw new ArgumentException("UNC paths require server, share, and file path.", nameof(value));
                ValidateWindowsSegment(unc[0], nameof(value));
                ValidateWindowsSegment(unc[1], nameof(value));
                prefix = "\\\\" + unc[0].Normalize(NormalizationForm.FormC).ToUpperInvariant() + "\\"
                    + unc[1].Normalize(NormalizationForm.FormC).ToUpperInvariant() + "\\";
                remainder = string.Join("\\", unc.Skip(2));
            }
            else
            {
                throw new ArgumentException("Risk keys must be absolute Windows paths.", nameof(value));
            }

            var segments = new List<string>();
            foreach (string segment in remainder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                    continue;
                if (segment == "..")
                {
                    if (segments.Count == 0)
                        throw new ArgumentException("Path cannot traverse above its root.", nameof(value));
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }
                ValidateWindowsSegment(segment, nameof(value));
                segments.Add(segment.Normalize(NormalizationForm.FormC).ToUpperInvariant());
            }

            if (segments.Count == 0)
                throw new ArgumentException("Path must identify a file, not a root.", nameof(value));
            return "PATH:" + prefix + string.Join("\\", segments);
        }

        private static bool IsAbsoluteWindowsPath(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            return (value.Length >= 3 && IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
                || value.StartsWith("\\\\", StringComparison.Ordinal)
                || value.StartsWith("//", StringComparison.Ordinal);
        }

        private static bool IsAsciiLetter(char value) =>
            (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        private static void ValidateWindowsSegment(string segment, string name)
        {
            if (string.IsNullOrEmpty(segment) || segment.EndsWith(" ", StringComparison.Ordinal)
                || segment.EndsWith(".", StringComparison.Ordinal)
                || segment.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0)
                throw new ArgumentException("Windows path has an ambiguous component.", name);

            string stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4
                    && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                        || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && stem[3] >= '1' && stem[3] <= '9'))
                throw new ArgumentException("Windows device-name path is unsupported.", name);
        }
    }
}
#nullable enable
