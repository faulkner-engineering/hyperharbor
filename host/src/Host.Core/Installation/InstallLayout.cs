using System.Security.Cryptography;

namespace HyperHarbor.Host.Core.Installation;

/// <summary>
/// The installed host's folders: root\versions\&lt;version&gt;\HyperHarbor.Host.exe for each version kept, and the
/// junction root\current pointing at the active one. The service and the tray's sign-in entry use the
/// stable path through current. Data never lives here; it stays in %ProgramData%\HyperHarbor.
/// </summary>
public sealed class InstallLayout
{
    public const string ExecutableName = "HyperHarbor.Host.exe";

    public InstallLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>%ProgramFiles%\HyperHarbor, writable only by administrators and SYSTEM.</summary>
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HyperHarbor");

    public string Root { get; }

    public string VersionsFolder => Path.Combine(Root, "versions");

    public string Current => Path.Combine(Root, "current");

    public string CurrentExecutable => Path.Combine(Current, ExecutableName);

    public string VersionFolder(SemanticVersion version) => Path.Combine(VersionsFolder, version.ToString());

    /// <summary>The version current points at, or null when nothing is installed here.</summary>
    public SemanticVersion? CurrentVersion =>
        Junction.GetTarget(Current) is { } target &&
        string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target)), VersionsFolder, StringComparison.OrdinalIgnoreCase) &&
        SemanticVersion.TryParse(Path.GetFileName(Path.TrimEndingDirectorySeparator(target)), out var version)
            ? version
            : null;

    /// <summary>The versions with a folder here, oldest first.</summary>
    public IReadOnlyList<SemanticVersion> InstalledVersions =>
        Directory.Exists(VersionsFolder)
            ? Directory.EnumerateDirectories(VersionsFolder)
                .Select(folder => SemanticVersion.TryParse(Path.GetFileName(folder), out var version) ? version : null)
                .OfType<SemanticVersion>()
                .Order()
                .ToList()
            : [];

    /// <summary>True when <paramref name="path"/> is inside the root (through current or a version folder).</summary>
    public bool Contains(string path) =>
        Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies <paramref name="sourceExecutable"/> into the version's folder. An identical file already there is
    /// kept, so installing the running version again does not touch the locked executable.
    /// </summary>
    /// <param name="unlockTimeout">
    /// How long to keep trying to replace an executable that is still locked by a process that is exiting.
    /// </param>
    /// <returns>The executable's path in the version folder.</returns>
    public string Stage(string sourceExecutable, SemanticVersion version, TimeSpan unlockTimeout = default)
    {
        var folder = VersionFolder(version);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, ExecutableName);
        if (File.Exists(target) && SameContents(sourceExecutable, target))
        {
            return target;
        }

        // Copied beside the target and then moved, so a failed copy never leaves a truncated executable.
        var partial = target + ".partial";
        File.Copy(sourceExecutable, partial, overwrite: true);
        var deadline = DateTime.UtcNow + unlockTimeout;
        while (true)
        {
            try
            {
                File.Move(partial, target, overwrite: true);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    /// <summary>Points current at the version's folder, which must hold the executable.</summary>
    public void Activate(SemanticVersion version)
    {
        var folder = VersionFolder(version);
        if (!File.Exists(Path.Combine(folder, ExecutableName)))
        {
            throw new FileNotFoundException($"Version {version} is not staged in {folder}.");
        }

        Junction.Repoint(Current, folder);
    }

    /// <summary>
    /// Deletes every version folder except <paramref name="keep"/> and the one current points at. A folder still
    /// in use (an old tray) is left for the next time.
    /// </summary>
    /// <returns>The versions deleted.</returns>
    public IReadOnlyList<SemanticVersion> PruneExcept(params SemanticVersion?[] keep)
    {
        var kept = keep.OfType<SemanticVersion>().Append(CurrentVersion).OfType<SemanticVersion>().ToHashSet();
        var deleted = new List<SemanticVersion>();
        foreach (var version in InstalledVersions.Where(version => !kept.Contains(version)))
        {
            try
            {
                Directory.Delete(VersionFolder(version), recursive: true);
                deleted.Add(version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use; the next install or update tries again.
            }
        }

        return deleted;
    }

    private static bool SameContents(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length)
        {
            return false;
        }

        using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }
}
