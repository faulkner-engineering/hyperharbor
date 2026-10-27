using System.Security.AccessControl;
using System.Security.Principal;

namespace HyperHarbor.Host.Core.Security;

/// <summary>
/// Finds and repairs data files a portable host cannot open. A portable host runs as the signed-in user, but its
/// elevated helpers run as whichever administrator approved the UAC prompt; for a standard user that is another
/// account, and the protected files it wrote gave the signed-in user no access. Not used by the installed service,
/// which runs as LocalSystem.
/// </summary>
public static class DataDirectoryAccess
{
    /// <summary>The files and folders under <paramref name="directory"/> the current account cannot open.</summary>
    public static IReadOnlyList<string> FindUnreadable(string directory)
    {
        var unreadable = new List<string>();
        if (Directory.Exists(directory))
        {
            CollectUnreadable(new DirectoryInfo(directory), unreadable);
        }

        return unreadable;
    }

    /// <summary>
    /// Gives <paramref name="hostAccount"/> full control of every file and folder under <paramref name="directory"/>
    /// that does not already grant it. Needs administrator rights for entries another account owns. Idempotent.
    /// </summary>
    /// <returns>The paths whose ACL changed.</returns>
    public static IReadOnlyList<string> GrantHostAccount(string directory, SecurityIdentifier hostAccount)
    {
        var changed = new List<string>();
        if (Directory.Exists(directory))
        {
            Grant(new DirectoryInfo(directory), hostAccount, changed);
        }

        return changed;
    }

    private static void CollectUnreadable(DirectoryInfo folder, List<string> unreadable)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = folder.GetFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            unreadable.Add(folder.FullName);
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is DirectoryInfo subfolder)
            {
                CollectUnreadable(subfolder, unreadable);
                continue;
            }

            try
            {
                // Open only: the running host may hold the logs open, and nothing needs the contents.
                using var stream = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (UnauthorizedAccessException)
            {
                unreadable.Add(entry.FullName);
            }
            catch (IOException)
            {
                // Locked or removed meanwhile; access is not the problem.
            }
        }
    }

    private static void Grant(DirectoryInfo folder, SecurityIdentifier hostAccount, List<string> changed)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is DirectoryInfo subfolder)
            {
                var security = subfolder.GetAccessControl(AccessControlSections.Access);
                if (AddFullControl(security, hostAccount))
                {
                    subfolder.SetAccessControl(security);
                    changed.Add(subfolder.FullName);
                }

                Grant(subfolder, hostAccount, changed);
            }
            else if (entry is FileInfo file)
            {
                var security = file.GetAccessControl(AccessControlSections.Access);
                if (AddFullControl(security, hostAccount))
                {
                    file.SetAccessControl(security);
                    changed.Add(file.FullName);
                }
            }
        }
    }

    /// <returns>False when the ACL already gives the account full control.</returns>
    private static bool AddFullControl(FileSystemSecurity security, SecurityIdentifier hostAccount)
    {
        var granted = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(rule => rule.IdentityReference.Equals(hostAccount)
                && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        if (granted)
        {
            return false;
        }

        security.AddAccessRule(new FileSystemAccessRule(hostAccount, FileSystemRights.FullControl, AccessControlType.Allow));
        return true;
    }
}
