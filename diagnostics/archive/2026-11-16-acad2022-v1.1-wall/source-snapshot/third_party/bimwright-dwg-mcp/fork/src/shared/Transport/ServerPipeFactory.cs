// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
#if ACAD2026
using System;
using System.IO.Pipes;
#if NETFRAMEWORK
using System.Security.AccessControl;
using System.Security.Principal;
using Bimwright.Dwg.Shared.Execution;
#endif

namespace Bimwright.Dwg.Plugin
{
    internal static class ServerPipeFactory
    {
        private const int BufferSize = 4096;

        /// <summary>
        /// Creates a single-instance byte-mode server pipe whose DACL admits only
        /// the interactive user.
        /// </summary>
        public static NamedPipeServerStream Create(string pipeName, int maxInstances)
        {
#if NETFRAMEWORK
            // The overload taking PipeSecurity is the .NET Framework equivalent of
            // PipeOptions.CurrentUserOnly, which does not exist on net48.
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                BufferSize,
                BufferSize,
                CurrentUserOnlySecurity());
#else
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                NetFx.ServerPipeOptions());
#endif
        }

#if NETFRAMEWORK
        /// <summary>
        /// Builds an explicit DACL equivalent to PipeOptions.CurrentUserOnly:
        /// inheritance disabled, a single ACE granting the current user's SID
        /// full control, that SID as owner, and no other principal admitted.
        /// A remote (network logon) client has no matching ACE and is refused by
        /// the kernel rather than by application logic.
        /// </summary>
        private static PipeSecurity CurrentUserOnlySecurity()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            SecurityIdentifier owner;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                owner = identity.User;
            }

            if (owner == null)
                throw new InvalidOperationException(
                    "The current Windows identity has no SID; refusing to create a control pipe without an owner-only DACL.");

            security.SetOwner(owner);
            security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }
#endif
    }
}
#endif
