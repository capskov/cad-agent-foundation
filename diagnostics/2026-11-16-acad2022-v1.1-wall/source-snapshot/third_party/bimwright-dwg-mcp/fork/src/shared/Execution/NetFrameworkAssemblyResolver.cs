// .NET Framework assembly binding shim resolution for the AutoCAD 2022 build.
//
// net48 binds by exact assembly version, unlike .NET 8, which unifies upward.
// SQLitePCLRaw 2.1.12 (netstandard2.0 asset) demands System.Memory 4.0.1.1 while
// System.Text.Json 8.0.5 pulls the 4.0.1.2 build, so the SQLite provider's static
// initializer dies with FileNotFoundException inside acad.exe. The plugin cannot
// alter binding redirects -- those live in acad.exe.config, which is host-owned --
// so it registers a resolver for its own shim assemblies only.
//
// The handler fires only when default probing has already failed, and only for the
// BCL shims the plugin itself ships. It never modifies process state, never touches
// AutoCAD configuration, and any other assembly name is declined.
#if NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Bimwright.Dwg.Shared.Execution
{
    internal static class NetFrameworkAssemblyResolver
    {
        private static readonly string[] Shims =
        {
            "System.Memory",
            "System.Buffers",
            "System.Runtime.CompilerServices.Unsafe",
            "System.Numerics.Vectors",
            "System.Text.Encoding.CodePages",
        };

        private static bool _installed;
        private static string _pluginDirectory;
        private static readonly Dictionary<string, Assembly> Cache =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Last resolution failure, for startup diagnostics; null when none.</summary>
        public static string LastFailure { get; private set; }

        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;

            try
            {
                _pluginDirectory = Path.GetDirectoryName(typeof(NetFrameworkAssemblyResolver).Assembly.Location);
            }
            catch (Exception ex)
            {
                LastFailure = "plugin directory unavailable: " + ex.Message;
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                if (args == null || string.IsNullOrEmpty(args.Name))
                    return null;

                AssemblyName requested;
                try { requested = new AssemblyName(args.Name); }
                catch { return null; }
                if (requested.Name == null)
                    return null;
                if (Array.IndexOf(Shims, requested.Name) < 0)
                    return null;

                Assembly cached;
                if (Cache.TryGetValue(requested.Name, out cached))
                    return cached;

                string candidate = Path.Combine(_pluginDirectory, requested.Name + ".dll");
                if (!File.Exists(candidate))
                {
                    LastFailure = "shim not shipped: " + candidate;
                    return null;
                }

                Assembly loaded = Assembly.LoadFrom(candidate);
                Cache[requested.Name] = loaded;
                return loaded;
            }
            catch (Exception ex)
            {
                LastFailure = "resolve failed for '" + args?.Name + "': " + ex.Message;
                return null;
            }
        }
    }
}
#endif
