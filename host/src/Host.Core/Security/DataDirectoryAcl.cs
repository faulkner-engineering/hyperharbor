using System.Security.AccessControl;
using System.Security.Principal;

namespace HyperHarbor.Host.Core.Security;

/// <summary>
/// Locks down the data directory for the installed service. %ProgramData% lets every user create files in
/// its subfolders, and the service (LocalSystem) trusts what it reads there: a paired-devices.json or
/// host-settings.json planted by another local account before first use would grant API access or move
/// where VMs are written. The folder gets a protected ACL (SYSTEM, Administrators, and the account running
/// this, full control; the tray user may read), and entries owned by anyone else are moved out of the service's
/// way into an "untrusted-" folder that nothing reads. They are moved, not deleted, because they may be the
/// owner's own data: a portable host's files belong to the account that ran it, and an installer run from another
/// administrator's console does not trust that account (2026-11-06, docs/platform-notes.md).
/// </summary>
public static class DataDirectoryAcl
{
    /// <summary>The start of the folders that hold untrusted entries; one per run that found any.</summary>
    public const string QuarantinePrefix = "untrusted-";

    /// <summary>Applies the ACL and moves untrusted entries aside. Idempotent.</summary>
    /// <param name="trayUser">The installing user, who may read (the tray opens the logs) and whose files are trusted.</param>
    /// <returns>The entries another account owned, and where each went.</returns>
    public static IReadOnlyList<UntrustedEntry> Secure(string directory, SecurityIdentifier? trayUser) =>
        Secure(directory, trayUser, TrustedOwners(trayUser), DateTimeOffset.UtcNow);

    /// <summary><see cref="Secure(string, SecurityIdentifier?)"/> with the trusted owners given. Exposed for tests.</summary>
    internal static IReadOnlyList<UntrustedEntry> Secure(string directory, SecurityIdentifier? trayUser, IReadOnlySet<SecurityIdentifier> trusted, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        var info = new DirectoryInfo(directory);
        info.SetAccessControl(CreateSecurity(trayUser));

        // Created only after the ACL above is in place, so no other account can have planted it or write into it.
        var quarantine = new Lazy<string>(() => CreateQuarantine(directory, now));
        var found = new List<UntrustedEntry>();
        MoveUntrusted(info, info.FullName, trusted, quarantine, found);
        return found;
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

    private static string CreateQuarantine(string directory, DateTimeOffset now)
    {
        var name = QuarantinePrefix + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, name);
        for (var suffix = 2; Directory.Exists(path); suffix++)
        {
            path = Path.Combine(directory, $"{name}-{suffix}");
        }

        Directory.CreateDirectory(path);
        return path;
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

    private static void MoveUntrusted(DirectoryInfo folder, string root, IReadOnlySet<SecurityIdentifier> trusted, Lazy<string> quarantine, List<UntrustedEntry> found)
    {
        // A snapshot: the quarantine folder may be created inside the folder being listed.
        foreach (var entry in folder.EnumerateFileSystemInfos().ToList())
        {
            var owner = entry switch
            {
                DirectoryInfo directory => directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)),
                FileInfo file => file.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)),
                _ => null,
            };
            if (owner is SecurityIdentifier sid && trusted.Contains(sid))
            {
                var isQuarantine = folder.FullName == root && entry.Name.StartsWith(QuarantinePrefix, StringComparison.OrdinalIgnoreCase);
                if (entry is DirectoryInfo subfolder && !subfolder.Attributes.HasFlag(FileAttributes.ReparsePoint) && !isQuarantine)
                {
                    MoveUntrusted(subfolder, root, trusted, quarantine, found);
                }

                continue;
            }

            var ownerName = owner is SecurityIdentifier ownerSid ? AccountName(ownerSid) : "an unknown account";
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A planted junction or link holds no data, and a later copy of the folder could follow it out.
                if (entry is DirectoryInfo link)
                {
                    link.Delete(recursive: false);
                }
                else
                {
                    entry.Delete();
                }

                found.Add(new UntrustedEntry(entry.FullName, ownerName, MovedTo: null));
                continue;
            }

            // MoveTo changes FullName to the new location.
            var path = entry.FullName;
            var target = Path.Combine(quarantine.Value, Path.GetRelativePath(root, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (entry is DirectoryInfo untrustedFolder)
            {
                untrustedFolder.MoveTo(target);
            }
            else
            {
                ((FileInfo)entry).MoveTo(target);
            }

            found.Add(new UntrustedEntry(path, ownerName, target));
        }
    }

    private static string AccountName(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}

/// <summary>A data directory entry another account owned.</summary>
/// <param name="MovedTo">Where it is now; null for a link, which is deleted.</param>
public sealed record UntrustedEntry(string Path, string Owner, string? MovedTo);
