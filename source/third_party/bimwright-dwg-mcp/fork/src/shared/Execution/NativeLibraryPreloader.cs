// .NET Framework native dependency bootstrap for the AutoCAD 2022 build.
//
// On .NET Framework a plain DllImport is resolved against the *host executable*
// directory (acad.exe) and PATH -- never against the managed assembly's own
// directory, which is what .NET 8+ does. The plugin therefore must pre-load its
// bundled native (e_sqlite3.dll) by absolute path before SQLitePCLRaw P/Invokes
// "e_sqlite3". LoadLibrary returns the already-loaded module for a repeated base
// name, so the later DllImport binds to this copy.
//
// Deliberately does NOT call SetDllDirectory: that would mutate the process-wide
// DLL search path of AutoCAD itself.
#if NETFRAMEWORK
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Bimwright.Dwg.Shared.Execution
{
    internal static class NativeLibraryPreloader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        private static bool _sqliteAttempted;
        private static string _sqliteFailure;

        /// <summary>
        /// Why the sqlite native failed to pre-load, or null when it loaded (or was absent).
        /// </summary>
        public static string SqliteFailure => _sqliteFailure;

        public static void EnsureSqliteNative()
        {
            if (_sqliteAttempted)
                return;
            _sqliteAttempted = true;

            string directory;
            try
            {
                directory = Path.GetDirectoryName(typeof(NativeLibraryPreloader).Assembly.Location);
            }
            catch (Exception ex)
            {
                _sqliteFailure = "assembly location unavailable: " + ex.Message;
                return;
            }

            if (string.IsNullOrEmpty(directory))
            {
                _sqliteFailure = "assembly directory is empty";
                return;
            }

            string native = Path.Combine(directory, "e_sqlite3.dll");
            if (!File.Exists(native))
            {
                _sqliteFailure = "e_sqlite3.dll not found next to the plugin: " + native;
                return;
            }

            try
            {
                IntPtr handle = LoadLibraryW(native);
                if (handle == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    _sqliteFailure = $"LoadLibrary failed for '{native}' (Win32 error {error})";
                }
            }
            catch (Exception ex)
            {
                _sqliteFailure = "LoadLibrary threw for '" + native + "': " + ex.Message;
            }
        }
    }
}
#endif
