using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Decides whether the process that serves the tray pipe can be the HyperHarbor service, before the tray sends it
/// anything (docs/architecture.md, Known limitations: pipe squatting). Another account's process could create the pipe
/// name before the service starts; the tray would then send it its commands and, for the VM page, the admin
/// passphrase. The check reads the pipe's owner: the installed service runs as LocalSystem, so its pipe is owned by
/// SYSTEM or Administrators, which a standard user's process cannot set; a portable host runs as the tray's own user.
/// A process of the tray's own user is not kept out, because it can already read everything the tray can.
/// </summary>
public static class PipeServerCheck
{
    /// <summary>True when <paramref name="owner"/> is SYSTEM, the Administrators group, or <paramref name="currentUser"/>.</summary>
    public static bool IsTrustedOwner(SecurityIdentifier? owner, SecurityIdentifier currentUser) =>
        owner is not null
        && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
            || owner == currentUser);

    /// <summary>Reads the connected pipe's owner and checks it; false when the owner cannot be read.</summary>
    public static bool IsTrusted(NamedPipeClientStream pipe)
    {
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            using var identity = WindowsIdentity.GetCurrent();
            return IsTrustedOwner(owner, identity.User!);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or IdentityNotMappedException)
        {
            return false;
        }
    }
}
