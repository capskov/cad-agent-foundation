using System.Diagnostics;
using System.Security;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bimwright.Dwg.Plugin.Execution;

/// <summary>
/// Checks both the process start time and the generation lock. A PID that now
/// belongs to another process is confirmed lost; failures to inspect it are
/// indeterminate and therefore never authorize recovery.
/// </summary>
public sealed class FileAndProcessPluginOwnerLivenessProvider : IPluginOwnerLivenessProvider
{
    private readonly string _databasePath;

    public FileAndProcessPluginOwnerLivenessProvider(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public PluginOwnerLiveness GetLiveness(PluginOwnerIdentity owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        try
        {
            using Process process = Process.GetProcessById(owner.ProcessId);
            DateTimeOffset actualStart = process.StartTime.ToUniversalTime();
            if (actualStart.UtcTicks != owner.ProcessStartedAtUtc.UtcTicks)
                return PluginOwnerLiveness.ConfirmedLost;
        }
        catch (ArgumentException)
        {
            return PluginOwnerLiveness.ConfirmedLost;
        }
        catch (InvalidOperationException)
        {
            return PluginOwnerLiveness.Indeterminate;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return PluginOwnerLiveness.Indeterminate;
        }
        catch (SecurityException)
        {
            return PluginOwnerLiveness.Indeterminate;
        }
        catch (NotSupportedException)
        {
            return PluginOwnerLiveness.Indeterminate;
        }

        string lockPath;
        try { lockPath = PluginOwnerLockIdentity.GetLockPath(_databasePath, owner); }
        catch (ArgumentException) { return PluginOwnerLiveness.Indeterminate; }

        try
        {
            using var probe = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return PluginOwnerLiveness.ConfirmedLost;
        }
        catch (DirectoryNotFoundException)
        {
            return PluginOwnerLiveness.ConfirmedLost;
        }
        catch (FileNotFoundException)
        {
            return PluginOwnerLiveness.ConfirmedLost;
        }
        catch (IOException exception)
        {
            int win32Code = exception.HResult & 0xFFFF;
            return win32Code is 32 or 33 ? PluginOwnerLiveness.Alive : PluginOwnerLiveness.Indeterminate;
        }
        catch (UnauthorizedAccessException)
        {
            return PluginOwnerLiveness.Indeterminate;
        }
    }

}

internal static class PluginOwnerLockIdentity
{
    internal static string CanonicalOwnerGuid(string value)
    {
        if (!Guid.TryParse(value, out Guid parsed))
            throw new ArgumentException("OwnerId must be a GUID.", nameof(value));
        return parsed.ToString("N");
    }

    internal static string ComputeKey(PluginOwnerIdentity owner)
    {
        string identity = string.Join("\n", CanonicalOwnerGuid(owner.OwnerId),
            owner.ProcessId.ToString(CultureInfo.InvariantCulture),
            owner.ProcessStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), owner.PluginGeneration);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    internal static string GetLockPath(string databasePath, PluginOwnerIdentity owner)
    {
        string directory = Path.GetFullPath(databasePath) + ".owners";
        return Path.Combine(directory, CanonicalOwnerGuid(owner.OwnerId) + "." + ComputeKey(owner) + ".lock");
    }
}
