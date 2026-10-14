// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.IO;
using System.Reflection;

namespace Bimwright.Dwg.Plugin.Export
{
    public static class ExportPathPolicy
    {
        public static string ValidateAndNormalize(
            string outputPath,
            string expectedExtension,
            bool overwriteExisting,
            bool allowRepoOutput,
            out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                error = "output path is required";
                return null;
            }

            if (!IsPathFullyQualified(outputPath))
            {
                error = "output path must be a fully qualified absolute path";
                return null;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(outputPath);
            }
            catch (Exception ex)
            {
                error = "invalid path: " + ex.Message;
                return null;
            }

            string ext = Path.GetExtension(normalized);
            if (expectedExtension.Equals(".image", StringComparison.OrdinalIgnoreCase))
            {
                if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                {
                    error = "extension must be .png, .jpg, .jpeg, or .bmp";
                    return null;
                }
            }
            else
            {
                if (!ext.Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"extension must be {expectedExtension}";
                    return null;
                }
            }

            if (File.Exists(normalized) && !overwriteExisting)
            {
                error = "file already exists and overwrite_existing is false";
                return null;
            }

            string dir = Path.GetDirectoryName(normalized);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                error = "target directory does not exist";
                return null;
            }

            if (!allowRepoOutput)
            {
                // Check all host/source locations and the target itself. A deployed
                // plugin can run outside the source tree while targeting that tree.
                var origins = new[]
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    Directory.GetCurrentDirectory(),
                    dir
                };
                foreach (var origin in origins)
                {
                    string repoRoot = FindRepositoryRoot(origin);
                    if (repoRoot != null && IsWithinRepository(normalized, repoRoot))
                    {
                        error = "writing to repository root is rejected; set allow_repo_output to true to override";
                        return null;
                    }
                }
            }
            return normalized;
        }

        internal static string FindRepositoryRoot(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return null;
            string current = Path.GetFullPath(baseDir);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(Path.Combine(current, ".git")) ||
                    File.Exists(Path.Combine(current, ".git")) ||
                    File.Exists(Path.Combine(current, "Bimwright.Dwg.sln")) ||
                    IsSourceRoot(current))
                {
                    return current;
                }
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
            return null;
        }

        private static bool IsSourceRoot(string directory)
        {
            // These real source members also exist in the trimmed R0 archive.
            // Do not require checkout-only solution or Git metadata.
            return File.Exists(Path.Combine(directory, "src", "shared", "Export", "ExportPathPolicy.cs")) &&
                File.Exists(Path.Combine(directory, "src", "server", "Bimwright.Dwg.Server.csproj")) &&
                File.Exists(Path.Combine(directory, "tests", "Bimwright.Dwg.Tests", "Bimwright.Dwg.Tests.csproj"));
        }

        internal static bool IsWithinRepository(string path, string repositoryRoot)
        {
            string root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalized = Path.GetFullPath(path);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return normalized.Equals(root, comparison) ||
                normalized.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }
        private static bool IsPathFullyQualified(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            // Windows UNC
            if (path.StartsWith("\\\\") || path.StartsWith("//")) return true;

            // Windows drive-rooted: C:\ or C:/
            if (path.Length >= 3 &&
                char.IsLetter(path[0]) &&
                path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/'))
            {
                return true;
            }

            // Unix absolute (CI Linux runners use /tmp/... for Path.GetTempPath())
            if (path[0] == '/') return true;

            return false;
        }
    }
}
