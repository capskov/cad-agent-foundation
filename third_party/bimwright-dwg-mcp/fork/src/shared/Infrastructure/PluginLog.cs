// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.IO;
using Bimwright.Dwg.Shared;

namespace Bimwright.Dwg.Plugin
{
    /// <summary>
    /// Timestamped append-only debug log under the historical %LOCALAPPDATA%\Bimwright\Dwg
    /// location, or <BIMWRIGHT_DWG_ROOT>\Dwg when an override is configured.
    /// Never throws — logging must not break the plugin.
    /// </summary>
    internal static class PluginLog
    {
        internal static void Debug(string message)
        {
            try
            {
                var dir = BimwrightAppRoot.Current.Dwg;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "debug.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
