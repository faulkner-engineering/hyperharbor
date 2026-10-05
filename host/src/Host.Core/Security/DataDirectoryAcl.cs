using System.Security.AccessControl;
using System.Security.Principal;

namespace HyperHarbor.Host.Core.Security;

/// <summary>
/// Locks down the data directory for the installed service. %ProgramData% lets every user create files in
/// its subfolders, and the service (LocalSystem) trusts what it reads there: a paired-devices.json or
/// host-settings.json planted by another local account before first use would grant API access or move
/// where VMs are written. The folder gets a protected ACL (SYSTEM, Administrators, and the account running
/// this, full control; the tray user may read), and entries owned by anyone else are removed.
/// </summary>
public static class DataDirectoryAcl
{
    /// <summary>Applies the ACL and removes untrusted entries. Idempotent.</summary>
    /// <param name="trayUser">The installing user, who may read (the tray opens the logs) and whose files are trusted.</param>
    /// <returns>The paths removed because another account owned them.</returns>
    public static IReadOnlyList<string> Secure(string directory, SecurityIdentifier? trayUser)
    {
        Directory.CreateDirectory(directory);
        var info = new DirectoryInfo(directory);
        info.SetAccessControl(CreateSecurity(trayUser));

        var trusted = TrustedOwners(trayUser);
        var removed = new List<string>();
        RemoveUntrusted(info, trusted, removed);
        return removed;
    }

    /// <summary>The folder ACL. Exposed for tests.</summary>
    internal static DirectorySecurity CreateSecurity(SecurityIdentifier? trayUser)
    {
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in FullControlIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        if (trayUser is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(trayUser, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    private static IEnumerable<SecurityIdentifier> FullControlIdentities()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var current = WindowsIdentity.GetCurrent();
        if (current.User is { } user)
        {
            yield return user;
        }
    }

    private static HashSet<SecurityIdentifier> TrustedOwners(SecurityIdentifier? trayUser)
    {
        var trusted = FullControlIdentities().ToHashSet();
        trusted.Add(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null));
        trusted.Add(new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")); // TrustedInstaller
        if (trayUser is not null)
        {
            trusted.Add(trayUser);
        }

        return trusted;
    }

    private static void RemoveUntrusted(DirectoryInfo folder, HashSet<SecurityIdentifier> trusted, List<string> removed)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            var owner = entry switch
            {
                DirectoryInfo directory => directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)),
                FileInfo file => file.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)),
                _ => null,
            };
            if (owner is SecurityIdentifier sid && trusted.Contains(sid))
            {
                if (entry is DirectoryInfo subfolder && !subfolder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    RemoveUntrusted(subfolder, trusted, removed);
                }

                continue;
            }

            if (entry is DirectoryInfo untrustedFolder)
            {
                untrustedFolder.Delete(recursive: !untrustedFolder.Attributes.HasFlag(FileAttributes.ReparsePoint));
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }

            removed.Add(entry.FullName);
        }
    }
}
