using System;
using System.IO;

namespace Bimwright.Dwg.Shared
{
    /// <summary>
    /// Immutable path layout for the Bimwright application root. The root is the
    /// directory whose immediate children are Dwg and Fonts.
    /// </summary>
    public sealed class BimwrightAppRootPaths
    {
        internal BimwrightAppRootPaths(string root, string legacyTransportRoot, bool overrideConfigured)
        {
            Root = root;
            TransportRoot = overrideConfigured ? root : legacyTransportRoot;
            OverrideConfigured = overrideConfigured;
        }

        public string Root { get; }
        /// <summary>Historical ExpandEnvironmentVariables-based root used by pipe/TCP discovery when no override is set.</summary>
        public string TransportRoot { get; }
        public bool OverrideConfigured { get; }
        public string Dwg => Path.Combine(Root, "Dwg");
        public string Fonts => Path.Combine(Root, "Fonts");

        public string UnderRoot(params string[] segments) => Combine(Root, segments);
        public string UnderDwg(params string[] segments) => Combine(Dwg, segments);

        private static string Combine(string root, string[] segments)
        {
            string result = root;
            if (segments == null)
                return result;

            for (int i = 0; i < segments.Length; i++)
            {
                if (string.IsNullOrEmpty(segments[i]) || Path.IsPathRooted(segments[i])
                    || segments[i] == "." || segments[i] == ".."
                    || segments[i].IndexOf(Path.DirectorySeparatorChar) >= 0
                    || segments[i].IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                    throw new ArgumentException("Path segments must be non-empty relative names.", nameof(segments));
                result = Path.Combine(result, segments[i]);
            }
            return result;
        }
    }

    /// <summary>
    /// Pins the environment-provided root exactly once. The injectable delegates
    /// make pinning deterministic in contracts without changing process-wide
    /// environment variables. Production uses one process-lifetime instance.
    /// </summary>
    public sealed class BimwrightAppRootPin
    {
        public const string EnvironmentVariable = "BIMWRIGHT_DWG_ROOT";

        private readonly Func<string> _rootEnvironment;
        private readonly Func<string> _localApplicationData;
        private readonly Func<string> _legacyTransportRoot;
        private readonly object _gate = new object();
        private bool _attempted;
        private BimwrightAppRootPaths _paths;
        private Exception _failure;

        public BimwrightAppRootPin(Func<string> rootEnvironment, Func<string> localApplicationData, Func<string> legacyTransportRoot = null)
        {
            _rootEnvironment = rootEnvironment ?? throw new ArgumentNullException(nameof(rootEnvironment));
            _localApplicationData = localApplicationData ?? throw new ArgumentNullException(nameof(localApplicationData));
            _legacyTransportRoot = legacyTransportRoot;
        }

        public BimwrightAppRootPaths Pin()
        {
            lock (_gate)
            {
                if (_attempted)
                {
                    if (_failure != null)
                        throw new InvalidOperationException("BIMWRIGHT_DWG_ROOT initialization previously failed; this process remains fail-closed.", _failure);
                    return _paths;
                }

                _attempted = true;
                try
                {
                    string configured = _rootEnvironment();
                    string localApplicationData = _localApplicationData();
                    string root = ResolveRoot(configured, localApplicationData);
                    if (configured != null)
                        EnsureWritableOverride(root);
                    string legacyTransportRoot = configured == null
                        ? (_legacyTransportRoot == null ? root : _legacyTransportRoot())
                        : root;
                    _paths = new BimwrightAppRootPaths(root, legacyTransportRoot, configured != null);
                    return _paths;
                }
                catch (Exception exception)
                {
                    _failure = exception;
                    throw;
                }
            }
        }

        /// <summary>
        /// Resolves paths without touching the filesystem. A null override
        /// intentionally preserves the historical LocalApplicationData layout.
        /// </summary>
        public static string ResolveRoot(string configuredRoot, string localApplicationData)
        {
            if (configuredRoot == null)
            {
                return Path.Combine(localApplicationData ?? string.Empty, "Bimwright");
            }

            if (string.IsNullOrWhiteSpace(configuredRoot)
                || !string.Equals(configuredRoot, configuredRoot.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("BIMWRIGHT_DWG_ROOT must be a non-empty absolute directory path.", nameof(configuredRoot));
            }

            if (Path.DirectorySeparatorChar == '\\' && IsWindowsDeviceNamespace(configuredRoot))
                throw new ArgumentException("BIMWRIGHT_DWG_ROOT cannot use a Windows device or NT object namespace.", nameof(configuredRoot));

            if (!IsFullyQualified(configuredRoot))
                throw new ArgumentException("BIMWRIGHT_DWG_ROOT must be fully qualified; relative and drive-relative paths are not allowed.", nameof(configuredRoot));

            RejectInvalidWindowsPathCharacters(configuredRoot);
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(configuredRoot);
            }
            catch (Exception exception)
            {
                throw new ArgumentException("BIMWRIGHT_DWG_ROOT is not a valid absolute directory path.", nameof(configuredRoot), exception);
            }

            string volumeRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(volumeRoot)
                || string.Equals(RemoveTrailingSeparators(fullPath), RemoveTrailingSeparators(volumeRoot), PathComparison))
            {
                throw new ArgumentException("BIMWRIGHT_DWG_ROOT cannot be a filesystem root.", nameof(configuredRoot));
            }

            return RemoveTrailingSeparators(fullPath);
        }

        private static bool IsFullyQualified(string path)
        {
            if (!Path.IsPathRooted(path))
                return false;

            if (Path.DirectorySeparatorChar == '\\')
            {
                bool driveAbsolute = path.Length >= 3
                    && IsAsciiLetter(path[0])
                    && path[1] == ':'
                    && IsSeparator(path[2]);
                bool uncAbsolute = path.Length >= 5
                    && IsSeparator(path[0])
                    && IsSeparator(path[1])
                    && !IsSeparator(path[2]);
                return driveAbsolute || uncAbsolute;
            }

            return path.Length > 1 && path[0] == Path.DirectorySeparatorChar;
        }

        private static bool IsWindowsDeviceNamespace(string path) =>
            path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\??\", StringComparison.OrdinalIgnoreCase);

        private static void RejectInvalidWindowsPathCharacters(string path)
        {
            for (int i = 0; i < path.Length; i++)
            {
                char value = path[i];
                if (value < 32 || Array.IndexOf(Path.GetInvalidPathChars(), value) >= 0)
                    throw new ArgumentException("BIMWRIGHT_DWG_ROOT contains an invalid path character.", nameof(path));

                // The configured runtime is Windows. Colon is permitted only
                // for a drive prefix; wildcards and shell-reserved characters
                // are never valid directory-name characters there.
                if (value == '<' || value == '>' || value == '"' || value == '|' || value == '?' || value == '*'
                    || (value == ':' && !(i == 1 && IsAsciiLetter(path[0]))))
                {
                    throw new ArgumentException("BIMWRIGHT_DWG_ROOT contains an invalid Windows path character.", nameof(path));
                }
            }

            string[] components = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < components.Length; i++)
            {
                string component = components[i];
                if (component.Length == 0 || (component.Length == 2 && component[1] == ':'))
                    continue;
                if (component[component.Length - 1] == '.' || component[component.Length - 1] == ' ')
                    throw new ArgumentException("BIMWRIGHT_DWG_ROOT contains a directory name ending in a dot or space.", nameof(path));

                string deviceBaseName = component.Split('.')[0].TrimEnd(' ');
                if (IsReservedDeviceName(deviceBaseName))
                    throw new ArgumentException("BIMWRIGHT_DWG_ROOT contains a reserved Windows device name.", nameof(path));
            }
        }

        private static bool IsReservedDeviceName(string value)
        {
            if (string.Equals(value, "CON", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "PRN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "AUX", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "NUL", StringComparison.OrdinalIgnoreCase))
                return true;

            if (value.Length == 4)
            {
                string prefix = value.Substring(0, 3);
                char digit = value[3];
                return (string.Equals(prefix, "COM", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(prefix, "LPT", StringComparison.OrdinalIgnoreCase))
                    && digit >= '1' && digit <= '9';
            }

            return false;
        }

        private static void EnsureWritableOverride(string root)
        {
            string probePath = null;
            try
            {
                Directory.CreateDirectory(root);
                probePath = Path.Combine(root, ".bimwright-root-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }
                File.Delete(probePath);
            }
            catch (Exception exception)
            {
                if (probePath != null)
                {
                    try { File.Delete(probePath); } catch { }
                }
                throw new InvalidOperationException("BIMWRIGHT_DWG_ROOT is invalid or not writable; Bimwright startup is fail-closed.", exception);
            }
        }

        private static string RemoveTrailingSeparators(string path)
        {
            string root = Path.GetPathRoot(path) ?? string.Empty;
            int end = path.Length;
            while (end > root.Length && IsSeparator(path[end - 1]))
                end--;
            return end == path.Length ? path : path.Substring(0, end);
        }

        private static bool IsAsciiLetter(char value) =>
            (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        private static bool IsSeparator(char value) =>
            value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

        private static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>Process-wide root snapshot shared by plugin and Server call sites.</summary>
    public static class BimwrightAppRoot
    {
        private static readonly BimwrightAppRootPin ProcessPin = new BimwrightAppRootPin(
            () => Environment.GetEnvironmentVariable(BimwrightAppRootPin.EnvironmentVariable),
            () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            () => Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Bimwright"));

        public static BimwrightAppRootPaths Initialize() => ProcessPin.Pin();

        public static BimwrightAppRootPaths Current => ProcessPin.Pin();
    }
}
