using System.Security.AccessControl;

namespace HyperHarbor.Host.Core.Installation;

/// <summary>
/// Copies of the data directory, taken before an update and used for the self-test. Each file and folder keeps
/// its ACL: a protected file copied with inherited permissions would become readable by the tray user, and the
/// DPAPI machine-scope files (certificate, credentials) can be decrypted by anyone who can read them.
/// Logs and the audit trail are never copied or restored: they only grow, and a rollback must not erase them.
/// </summary>
public static class DataBackup
{
    /// <summary>The folder in the data directory for update work: backups and self-test copies.</summary>
    public const string UpdateFolderName = "update";

    /// <summary>Entries at the top of the data directory that are neither copied nor restored.</summary>
    public static readonly IReadOnlySet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "audit.log", "audit.1.log", UpdateFolderName,
    };

    /// <summary>Copies the data directory to <paramref name="destination"/>, which must not exist yet.</summary>
    public static void Copy(string dataDirectory, string destination)
    {
        if (Path.Exists(destination))
        {
            throw new IOException($"{destination} already exists.");
        }

        CopyFolder(new DirectoryInfo(dataDirectory), destination, topLevel: true);
    }

    /// <summary>
    /// Replaces the data directory's contents with the backup's, keeping logs and the audit trail. Entries the
    /// backup does not have are deleted. If this stops halfway, running it again finishes the job.
    /// </summary>
    public static void Restore(string backup, string dataDirectory)
    {
        if (!Directory.Exists(backup))
        {
            throw new DirectoryNotFoundException($"The backup {backup} does not exist.");
        }

        foreach (var entry in new DirectoryInfo(dataDirectory).EnumerateFileSystemInfos().Where(entry => !Excluded.Contains(entry.Name)))
        {
            Delete(entry);
        }

        foreach (var entry in new DirectoryInfo(backup).EnumerateFileSystemInfos().Where(entry => !Excluded.Contains(entry.Name)))
        {
            var target = Path.Combine(dataDirectory, entry.Name);
            if (entry is DirectoryInfo folder)
            {
                CopyFolder(folder, target, topLevel: false);
            }
            else
            {
                CopyFile((FileInfo)entry, target);
            }
        }
    }

    /// <summary>Deletes a copy or backup. A junction inside it is removed without following it.</summary>
    public static void Delete(string folder)
    {
        if (Directory.Exists(folder))
        {
            Delete(new DirectoryInfo(folder));
        }
    }

    private static void CopyFolder(DirectoryInfo source, string destination, bool topLevel)
    {
        var target = Directory.CreateDirectory(destination);
        var access = new DirectorySecurity();
        access.SetSecurityDescriptorBinaryForm(source.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        target.SetAccessControl(access);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            // Reparse points could lead outside the data directory; nothing HyperHarbor writes is one.
            if ((topLevel && Excluded.Contains(entry.Name)) || entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || IsTemporary(entry.Name))
            {
                continue;
            }

            if (entry is DirectoryInfo folder)
            {
                CopyFolder(folder, Path.Combine(destination, entry.Name), topLevel: false);
            }
            else
            {
                CopyFile((FileInfo)entry, Path.Combine(destination, entry.Name));
            }
        }
    }

    private static void CopyFile(FileInfo source, string destination)
    {
        // A security object read from another file is written back only once marked changed, which setting
        // its binary form does; passing the source's object straight through would leave the copy inheriting.
        var access = new FileSecurity();
        access.SetSecurityDescriptorBinaryForm(source.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        source.CopyTo(destination);
        new FileInfo(destination).SetAccessControl(access);
    }

    /// <summary>ProtectedFile's in-progress writes (".name.guid.tmp"), which a copy taken while the host runs can see.</summary>
    private static bool IsTemporary(string name) =>
        name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static void Delete(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo folder)
        {
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                folder.Delete(recursive: false);
                return;
            }

            foreach (var child in folder.EnumerateFileSystemInfos())
            {
                Delete(child);
            }

            folder.Delete();
            return;
        }

        entry.Attributes = FileAttributes.Normal;
        entry.Delete();
    }
}
