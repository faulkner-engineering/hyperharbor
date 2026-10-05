using System.Text;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>A setup profile the User does not have.</summary>
public sealed class SetupProfileNotFoundException(string id) : Exception($"There is no setup profile \"{id}\".");

/// <summary>
/// Each User's setup profiles, one YAML file each in &lt;data&gt;\profiles\&lt;user id&gt;\&lt;id&gt;.yaml, written with the
/// ProtectedFile ACL. The files are the source of truth: a hand-edited file is read as it is, and listed with
/// its problem when it no longer reads.
/// </summary>
public sealed partial class SetupProfileStore
{
    public const string FolderName = "profiles";
    public const int MaxProfiles = 100;
    public const int MaxFileBytes = 256 * 1024;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex Id();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    private readonly string _root;
    private readonly ProfileValidator _validator;
    private readonly object _gate = new();

    public SetupProfileStore(string dataDirectory, Catalogs catalogs)
    {
        _root = Path.Combine(dataDirectory, FolderName);
        _validator = new ProfileValidator(catalogs);
    }

    public IReadOnlyList<SetupProfileSummary> List(Guid userId)
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

    /// <exception cref="SetupProfileNotFoundException">No such profile.</exception>
    /// <exception cref="LifecycleValidationException">The file on the host no longer reads (field "yaml").</exception>
    public StoredSetupProfile Get(Guid userId, string id)
    {
        lock (_gate)
        {
            var path = PathOf(userId, id);
            return new StoredSetupProfile(id, File.GetLastWriteTimeUtc(path), ReadValid(path));
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
    public StoredSetupProfile Create(Guid userId, SetupProfile profile)
    {
        var normalized = _validator.Normalize(profile);
        lock (_gate)
        {
            var folder = Folder(userId);
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.yaml").Count() >= MaxProfiles)
            {
                throw new LifecycleValidationException($"A User can keep {MaxProfiles} setup profiles. Delete one first.", [new ValidationIssue("name", $"You already have {MaxProfiles} setup profiles.")]);
            }

            var id = UniqueId(folder, normalized.Name);
            return Write(userId, id, normalized);
        }
    }

    /// <exception cref="SetupProfileNotFoundException">No such profile.</exception>
    /// <exception cref="LifecycleValidationException">The profile has problems, by field.</exception>
    public StoredSetupProfile Update(Guid userId, string id, SetupProfile profile)
    {
        var normalized = _validator.Normalize(profile);
        lock (_gate)
        {
            _ = PathOf(userId, id);
            return Write(userId, id, normalized);
        }
    }

    /// <summary>Saves a profile from YAML text, as a new profile.</summary>
    /// <exception cref="LifecycleValidationException">Not a profile (field "yaml"), or a profile with problems.</exception>
    public StoredSetupProfile Import(Guid userId, string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > MaxFileBytes)
        {
            throw new LifecycleValidationException("The file is too large.", [new ValidationIssue("yaml", $"A setup profile file is at most {MaxFileBytes / 1024} KB.")]);
        }

        SetupProfile profile;
        try
        {
            profile = ProfileYamlReader.Read(yaml);
        }
        catch (ProfileFormatException ex)
        {
            throw new LifecycleValidationException("The file is not a setup profile.", [new ValidationIssue("yaml", ex.Message)]);
        }

        return Create(userId, profile);
    }

    /// <exception cref="SetupProfileNotFoundException">No such profile.</exception>
    public void Delete(Guid userId, string id)
    {
        lock (_gate)
        {
            File.Delete(PathOf(userId, id));
        }
    }

    private StoredSetupProfile Write(Guid userId, string id, SetupProfile profile)
    {
        var folder = Folder(userId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".yaml");
        ProtectedFile.WriteAllBytes(path, Encoding.UTF8.GetBytes(ProfileYamlWriter.Write(profile, _validator.PolicyTypes)));
        return new StoredSetupProfile(id, File.GetLastWriteTimeUtc(path), profile);
    }

    private SetupProfile ReadValid(string path)
    {
        try
        {
            return _validator.Normalize(ProfileYamlReader.Read(File.ReadAllText(path)));
        }
        catch (ProfileFormatException ex)
        {
            throw new LifecycleValidationException("The profile file on the host does not read.", [new ValidationIssue("yaml", ex.Message)]);
        }
    }

    private SetupProfileSummary Summarize(string id, string path)
    {
        var updatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        try
        {
            var profile = ReadValid(path);
            var remove = profile.Remove;
            return new SetupProfileSummary(
                id,
                profile.Name,
                profile.Description,
                profile.Install?.Count ?? 0,
                (remove?.Appx?.Count ?? 0) + (remove?.Capabilities?.Count ?? 0) + (remove?.Features?.Count ?? 0),
                profile.Tweaks?.Count ?? 0,
                profile.Browser?.Extensions?.Count ?? 0,
                profile.Browser?.App.Name ?? profile.Browser?.App.Id,
                updatedAt,
                null);
        }
        catch (LifecycleValidationException ex)
        {
            var problem = string.Join(" ", ex.Errors.Select(issue => issue.Field == "yaml" ? issue.Message : $"{issue.Field}: {issue.Message}"));
            return new SetupProfileSummary(id, id, null, 0, 0, 0, 0, null, updatedAt, problem);
        }
    }

    private string Folder(Guid userId) => Path.Combine(_root, userId.ToString("D"));

    private string PathOf(Guid userId, string id)
    {
        var path = Path.Combine(Folder(userId), id + ".yaml");
        if (!Id().IsMatch(id) || !File.Exists(path))
        {
            throw new SetupProfileNotFoundException(id);
        }

        return path;
    }

    /// <summary>A file name from the profile name: lowercase letters and digits joined by hyphens, numbered when taken.</summary>
    private static string UniqueId(string folder, string name)
    {
        var slug = NotSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "profile";
        }

        slug = slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
        var id = slug;
        for (var n = 2; File.Exists(Path.Combine(folder, id + ".yaml")); n++)
        {
            id = $"{slug}-{n}";
        }

        return id;
    }
}
