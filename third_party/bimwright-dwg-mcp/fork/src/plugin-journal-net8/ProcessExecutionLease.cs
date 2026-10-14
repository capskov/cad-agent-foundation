using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bimwright.Dwg.Plugin.Execution;

/// <summary>
/// Holds an OS-enforced, process-wide lease for one PID and exact UTC process
/// start time. Every participant must use the same absolute shared directory.
/// The handle remains open until explicit Dispose or process exit; garbage
/// collection does not release an abandoned lease. A static in-memory registry
/// is not used, so separate plugin load contexts coordinate through the OS lock.
/// </summary>
public sealed class ProcessExecutionLease : IDisposable
{
    private const string LockFileSuffix = ".execution-lease";
    private FileStream? _handle;

    private ProcessExecutionLease(string directoryPath, string lockPath, int processId,
        DateTimeOffset processStartedAtUtc, FileStream handle)
    {
        DirectoryPath = directoryPath;
        LockPath = lockPath;
        ProcessId = processId;
        ProcessStartedAtUtc = processStartedAtUtc;
        _handle = handle;
    }

    public string DirectoryPath { get; }
    public string LockPath { get; }
    public int ProcessId { get; }
    public DateTimeOffset ProcessStartedAtUtc { get; }

    /// <summary>
    /// Acquires the lease for a process identity. The caller should pass the
    /// PID and <c>Process.StartTime.ToUniversalTime()</c> captured from that
    /// process. Any acquisition error is propagated; no lease is returned on
    /// failure, so callers must not execute work unless this method succeeds.
    /// </summary>
    public static ProcessExecutionLease Acquire(string sharedLeaseDirectory, int processId,
        DateTimeOffset processStartedAtUtc)
    {
        string directoryPath = ValidateDirectory(sharedLeaseDirectory);
        ValidateIdentity(processId, processStartedAtUtc);

        string lockPath = CreateLockPath(directoryPath, processId, processStartedAtUtc);
        Directory.CreateDirectory(directoryPath);

        var handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // If a plugin load context disappears while execution may still be active,
        // GC must not release the lease and permit another generation to enter CAD.
        GC.SuppressFinalize(handle);
        GC.SuppressFinalize(handle.SafeFileHandle);
        try
        {
            byte[] metadata = Encoding.UTF8.GetBytes(string.Join("\n",
                "bimwright-process-execution-lease-v1",
                processId.ToString(CultureInfo.InvariantCulture),
                processStartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                Path.GetFileNameWithoutExtension(lockPath)) + "\n");
            handle.SetLength(0);
            handle.Position = 0;
            handle.Write(metadata, 0, metadata.Length);
            handle.Flush(flushToDisk: true);

            return new ProcessExecutionLease(directoryPath, lockPath, processId, processStartedAtUtc, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();

    private static string ValidateDirectory(string sharedLeaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedLeaseDirectory);
        if (!Path.IsPathFullyQualified(sharedLeaseDirectory))
            throw new ArgumentException("The shared lease directory must be an absolute path.", nameof(sharedLeaseDirectory));

        string fullPath = Path.GetFullPath(sharedLeaseDirectory);
        string root = Path.GetPathRoot(fullPath)!;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(Path.TrimEndingDirectorySeparator(fullPath),
            Path.TrimEndingDirectorySeparator(root), comparison))
            throw new ArgumentException("The filesystem root cannot be used as the shared lease directory.", nameof(sharedLeaseDirectory));
        return fullPath;
    }

    private static void ValidateIdentity(int processId, DateTimeOffset processStartedAtUtc)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId), "Process ID must be positive.");
        if (processStartedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Process start time must be expressed in UTC.", nameof(processStartedAtUtc));
        if (processStartedAtUtc.UtcTicks <= 0 || processStartedAtUtc > DateTimeOffset.UtcNow)
            throw new ArgumentException("Process start time must be a valid time no later than now.", nameof(processStartedAtUtc));
    }

    private static string CreateLockPath(string directoryPath, int processId, DateTimeOffset processStartedAtUtc)
    {
        string identity = processId.ToString(CultureInfo.InvariantCulture) + "\n" +
            processStartedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(directoryPath, "pid-" + processId.ToString(CultureInfo.InvariantCulture) + "-" + hash + LockFileSuffix);
    }
}
