#nullable disable

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Bimwright.Dwg.Shared
{
    /// <summary>
    /// Cross-process, cross-session gate shared by discovery publishers and
    /// cleanup for the same Windows user and AutoCAD year.
    /// </summary>
    public sealed class DiscoveryFileMutex : IDisposable
    {
        private const uint MutexAllAccess = 0x001F0001;
        private const uint SddlRevision1 = 1;

        private readonly Mutex _mutex;
        private bool _ownsMutex;

        public DiscoveryFileMutex(string target, int timeoutMilliseconds = 10000)
        {
            if (string.IsNullOrWhiteSpace(target) || target.Trim().Length != 4)
                throw new ArgumentException("A four-digit AutoCAD target is required.", nameof(target));
            if (timeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

            target = target.Trim();
            for (int index = 0; index < target.Length; index++)
            {
                if (target[index] < '0' || target[index] > '9')
                    throw new ArgumentException("A four-digit AutoCAD target is required.", nameof(target));
            }

            string userSid = GetCurrentUserSid();
            string mutexName = BuildMutexName(userSid, target);
            _mutex = CreateProtectedMutex(mutexName, userSid);

            try
            {
                _ownsMutex = _mutex.WaitOne(timeoutMilliseconds);
            }
            catch (AbandonedMutexException)
            {
                // Windows grants ownership to the waiter that observes an
                // abandoned mutex; the protected file still needs validation.
                _ownsMutex = true;
            }

            if (!_ownsMutex)
            {
                _mutex.Dispose();
                throw new TimeoutException("Timed out waiting for the AutoCAD discovery-file lock.");
            }
        }

        internal static string BuildMutexName(string userSid, string target) =>
            "Global\\Bimwright.Discovery." + userSid + "." + target;

        internal static string GetMutexNameForCurrentUser(string target) =>
            BuildMutexName(GetCurrentUserSid(), target);

        internal static string BuildMutexSecurityDescriptorSddl(string userSid) =>
            "O:" + userSid + "D:P(A;;0x" + MutexAllAccess.ToString("X8") + ";;;" + userSid + ")";

        private static string GetCurrentUserSid()
        {
#if NET5_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
#else
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
#endif
                throw new PlatformNotSupportedException("Discovery-file mutexes require Windows.");

            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null || string.IsNullOrWhiteSpace(identity.User.Value))
                    throw new InvalidOperationException("The current Windows user SID is unavailable.");
                return identity.User.Value;
            }
        }

        private static Mutex CreateProtectedMutex(string name, string userSid)
        {
#if NET5_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
#else
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
#endif
                throw new PlatformNotSupportedException("Discovery-file mutexes require Windows.");

            string securityDescriptorSddl = BuildMutexSecurityDescriptorSddl(userSid);
            IntPtr securityDescriptor = IntPtr.Zero;
            IntPtr mutexHandle = IntPtr.Zero;
            SafeWaitHandle safeMutexHandle = null;
            Mutex mutex = null;

            try
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                    securityDescriptorSddl, SddlRevision1, out securityDescriptor, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not create the discovery mutex security descriptor.");
                }

                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                    SecurityDescriptor = securityDescriptor,
                    InheritHandle = false
                };

                mutexHandle = CreateMutex(ref attributes, false, name);
                if (mutexHandle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the discovery mutex.");

                safeMutexHandle = new SafeWaitHandle(mutexHandle, ownsHandle: true);
                mutexHandle = IntPtr.Zero;
                mutex = new Mutex();
                mutex.SafeWaitHandle = safeMutexHandle;
                safeMutexHandle = null;
                return mutex;
            }
            catch
            {
                if (mutex != null)
                    mutex.Dispose();
                if (safeMutexHandle != null)
                    safeMutexHandle.Dispose();
                if (mutexHandle != IntPtr.Zero)
                    CloseHandle(mutexHandle);
                throw;
            }
            finally
            {
                if (securityDescriptor != IntPtr.Zero)
                    LocalFree(securityDescriptor);
            }
        }

        public void Dispose()
        {
            if (_ownsMutex)
            {
                _ownsMutex = false;
                try { _mutex.ReleaseMutex(); }
                finally { _mutex.Dispose(); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string stringSecurityDescriptor, uint stringSDRevision, out IntPtr securityDescriptor,
            IntPtr securityDescriptorSize);

        [DllImport("kernel32.dll", EntryPoint = "CreateMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutex(ref SecurityAttributes mutexAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
