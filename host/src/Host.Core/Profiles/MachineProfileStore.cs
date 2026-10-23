using System.Text;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>A machine profile the User does not have.</summary>
public sealed class MachineProfileNotFoundException(string id) : Exception($"There is no machine profile \"{id}\".");

/// <summary>
/// Each User's machine profiles, one YAML file each in &lt;data&gt;\machine-profiles\&lt;user id&gt;\&lt;id&gt;.yaml, written
/// with the ProtectedFile ACL. As with setup profiles, the files are the source of truth: a hand-edited file is read
/// as it is, and listed with its problem when it no longer reads.
/// </summary>
public sealed partial class MachineProfileStore(string dataDirectory, MachineProfileValidator validator)
{
    public const string FolderName = "machine-profiles";
    public const int MaxProfiles = 100;
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>Route segments under /machine-profiles that a profile id must not take.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "import", "schema" };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex Id();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    private readonly string _root = Path.Combine(dataDirectory, FolderName);
    private readonly object _gate = new();

    public IReadOnlyList<MachineProfileSummary> List(Guid userId)
    {
        lock (_gate)
        {
            var folder = Folder(userId);
            if (!Directory.Exists(folder))
            {
                return [];
            }

            return Directory.EnumerateFiles(folder, "*.yaml")
                .Select(path => Summarize(Path.GetFileNameWithoutExtension(path), path))
                .OrderBy(summary => summary.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    /// <exception cref="MachineProfileNotFoundException">No such profile.</exception>
    /// <exception cref="LifecycleValidationException">The file on the host no longer reads (field "yaml").</exception>
    public StoredMachineProfile Get(Guid userId, string id)
    {
        lock (_gate)
        {
            var path = PathOf(userId, id);
            return new StoredMachineProfile(id, File.GetLastWriteTimeUtc(path), ReadValid(path));
        }
    }

    /// <summary>The file as stored, for export.</summary>
    public string GetYaml(Guid userId, string id)
    {
        lock (_gate)
        {
            return File.ReadAllText(PathOf(userId, id));
        }
    }

    /// <exception cref="LifecycleValidationException">The profile has problems, by field.</exception>
    public StoredMachineProfile Create(Guid userId, MachineProfile profile)
    {
        var normalized = validator.Normalize(profile);
        lock (_gate)
        {
            var folder = Folder(userId);
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.yaml").Count() >= MaxProfiles)
            {
                throw new LifecycleValidationException($"A User can keep {MaxProfiles} machine profiles. Delete one first.", [new ValidationIssue("name", $"You already have {MaxProfiles} machine profiles.")]);
            }

            return Write(userId, UniqueId(folder, normalized.Name), normalized);
        }
    }

    /// <exception cref="MachineProfileNotFoundException">No such profile.</exception>
    /// <exception cref="LifecycleValidationException">The profile has problems, by field.</exception>
    public StoredMachineProfile Update(Guid userId, string id, MachineProfile profile)
    {
        var normalized = validator.Normalize(profile);
        lock (_gate)
        {
            _ = PathOf(userId, id);
            return Write(userId, id, normalized);
        }
    }

    /// <summary>Saves a profile from YAML text, as a new profile.</summary>
    /// <exception cref="LifecycleValidationException">Not a machine profile (field "yaml"), or a profile with problems.</exception>
    public StoredMachineProfile Import(Guid userId, string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > MaxFileBytes)
        {
            throw new LifecycleValidationException("The file is too large.", [new ValidationIssue("yaml", $"A machine profile file is at most {MaxFileBytes / 1024} KB.")]);
        }

        MachineProfile profile;
        try
        {
            profile = MachineProfileYaml.Read(yaml);
        }
        catch (ProfileFormatException ex)
        {
            throw new LifecycleValidationException("The file is not a machine profile.", [new ValidationIssue("yaml", ex.Message)]);
        }

        return Create(userId, profile);
    }

    /// <exception cref="MachineProfileNotFoundException">No such profile.</exception>
    public void Delete(Guid userId, string id)
    {
        lock (_gate)
        {
            File.Delete(PathOf(userId, id));
        }
    }

    private StoredMachineProfile Write(Guid userId, string id, MachineProfile profile)
    {
        var folder = Folder(userId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".yaml");
        ProtectedFile.WriteAllBytes(path, Encoding.UTF8.GetBytes(MachineProfileYaml.Write(profile, validator.PolicyTypes)));
        return new StoredMachineProfile(id, File.GetLastWriteTimeUtc(path), profile);
    }

    private MachineProfile ReadValid(string path)
    {
        try
        {
            return validator.Normalize(MachineProfileYaml.Read(File.ReadAllText(path)));
        }
        catch (ProfileFormatException ex)
        {
            throw new LifecycleValidationException("The profile file on the host does not read.", [new ValidationIssue("yaml", ex.Message)]);
        }
    }

    private MachineProfileSummary Summarize(string id, string path)
    {
        var updatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        try
        {
            var profile = ReadValid(path);
            var remove = profile.Remove;
            var software = (profile.Install?.Count ?? 0)
                + (remove?.Appx?.Count ?? 0) + (remove?.Capabilities?.Count ?? 0) + (remove?.Features?.Count ?? 0)
                + (profile.Tweaks?.Count ?? 0)
                + (profile.Browser?.Extensions?.Count ?? 0);
            return new MachineProfileSummary(
                id,
                profile.Name,
                profile.Description,
                profile.Os,
                profile.Compute?.Processors,
                profile.Compute?.Memory?.StartupMb,
                profile.Gpu?.Enabled == true,
                profile.Firmware?.Tpm,
                profile.Backup?.Enabled == true,
                software,
                updatedAt,
                null);
        }
        catch (LifecycleValidationException ex)
        {
            var problem = string.Join(" ", ex.Errors.Select(issue => issue.Field == "yaml" ? issue.Message : $"{issue.Field}: {issue.Message}"));
            return new MachineProfileSummary(id, id, null, default, null, null, false, null, false, 0, updatedAt, problem);
        }
    }

    private string Folder(Guid userId) => Path.Combine(_root, userId.ToString("D"));

    private string PathOf(Guid userId, string id)
    {
        var path = Path.Combine(Folder(userId), id + ".yaml");
        if (!Id().IsMatch(id) || !File.Exists(path))
        {
            throw new MachineProfileNotFoundException(id);
        }

        return path;
    }

    /// <summary>A file name from the profile name: lowercase letters and digits joined by hyphens, numbered when taken.</summary>
    private static string UniqueId(string folder, string name)
    {
        var slug = NotSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "machine";
        }

        slug = slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
        var id = slug;
        for (var n = 2; Reserved.Contains(id) || File.Exists(Path.Combine(folder, id + ".yaml")); n++)
        {
            id = $"{slug}-{n}";
        }

        return id;
    }
}
