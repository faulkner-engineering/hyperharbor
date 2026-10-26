using System.Security.AccessControl;
using System.Security.Principal;

namespace HyperHarbor.Host.Core.Security;

/// <summary>
/// Writes files that only SYSTEM, Administrators, and the account running the host can access.
/// %ProgramData% grants read access to all users by default, so security-relevant files
/// (the host certificate and the paired device list) must not inherit its ACL.
/// </summary>
public static class ProtectedFile
{
    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="contents"/>.</summary>
    /// <param name="hostAccount">
    /// The account the host runs as, when another account writes the file for it: an elevated helper runs as
    /// whichever administrator approved the UAC prompt, which for a standard user is a different account.
    /// Null means the current account.
    /// </param>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents, SecurityIdentifier? hostAccount = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileInfo(temporary).Create(
                FileMode.CreateNew,
                FileSystemRights.Write | FileSystemRights.ReadData,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough,
                CreateSecurity(hostAccount: hostAccount)))
            {
                stream.Write(contents);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> for appending. A new file gets the restricted ACL; an existing
    /// file keeps the ACL it was created with.
    /// </summary>
    /// <param name="readers">
    /// Accounts that may also read a new file. The installed service grants the tray's user read access
    /// to the logs and the audit trail, which never contain secrets.
    /// </param>
    public static FileStream OpenAppend(string path, IReadOnlyCollection<SecurityIdentifier>? readers = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new FileInfo(path).Create(
            FileMode.Append,
            FileSystemRights.AppendData,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough,
            CreateSecurity(readers));
    }

    /// <summary>The ACL for new files. Exposed for tests.</summary>
    /// <param name="hostAccount">Gets full control in place of the current account; null means the current account.</param>
    internal static FileSecurity CreateSecurity(IReadOnlyCollection<SecurityIdentifier>? readers = null, SecurityIdentifier? hostAccount = null)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var identity in AllowedIdentities(hostAccount))
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        foreach (var reader in readers ?? [])
        {
            security.AddAccessRule(new FileSystemAccessRule(reader, FileSystemRights.Read | FileSystemRights.Synchronize, AccessControlType.Allow));
        }

        return security;
    }

    private static IEnumerable<IdentityReference> AllowedIdentities(SecurityIdentifier? hostAccount)
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        if (hostAccount is not null)
        {
            yield return hostAccount;
            yield break;
        }

        using var current = WindowsIdentity.GetCurrent();
        if (current.User is { } user)
        {
            yield return user;
        }
    }
}
