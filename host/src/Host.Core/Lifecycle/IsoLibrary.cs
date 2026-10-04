using System.Text.Json;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>Thrown when no image in the library has the requested name (404).</summary>
public sealed class IsoNotFoundException : Exception
{
    public IsoNotFoundException(string name)
        : base($"\"{name}\" is not in the ISO library.")
    {
    }
}

/// <summary>Thrown when the library's drive does not have room for an upload (507).</summary>
public sealed class InsufficientStorageException : Exception
{
    public InsufficientStorageException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Host settings changed at run time from the tray, stored as host-settings.json in the data directory.
/// They take precedence over the configuration files.
/// </summary>
public sealed class HostSettingsStore
{
    public const string FileName = "host-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Settings? _settings;

    public HostSettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    /// <summary>Raised after a setting changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The ISO library folder chosen in the tray, or null to use the configured default.</summary>
    public string? IsoFolder
    {
        get
        {
            lock (_gate)
            {
                return Load().IsoFolder;
            }
        }
    }

    public void SetIsoFolder(string? folder) => Update(settings => settings with { IsoFolder = folder });

    /// <summary>The folder for new VMs chosen in the tray, or null to use the configured default.</summary>
    public string? VmFolder
    {
        get
        {
            lock (_gate)
            {
                return Load().VmFolder;
            }
        }
    }

    public void SetVmFolder(string? folder) => Update(settings => settings with { VmFolder = folder });

    private void Update(Func<Settings, Settings> change)
    {
        lock (_gate)
        {
            _settings = change(Load());
            ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(_settings, JsonOptions));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Settings Load() => _settings ??= File.Exists(_path)
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(_path), JsonOptions) ?? new Settings()
        : new Settings();

    private sealed record Settings(string? IsoFolder = null, string? VmFolder = null);
}

/// <summary>
/// The host's ISO library: installation images stored as files directly in one folder, so clients can
/// add, rename, and delete them and every client sees the same set. Names are plain file names ending in
/// .iso; a name can never reach outside the folder, including through a junction or symbolic link.
/// Uploads are written to a hidden ".partial" file and renamed into place only when complete.
/// </summary>
public sealed class IsoLibrary
{
    public const int MaxNameLength = 200;
    private const string PartialExtension = ".partial";
    private static readonly TimeSpan StalePartialAge = TimeSpan.FromDays(1);
    private const int CopyBufferBytes = 1024 * 1024;

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })];

    private readonly Func<string> _folder;
    private readonly ILogger? _logger;

    public IsoLibrary(string folder)
        : this(() => folder)
    {
    }

    /// <param name="folder">Read on every call, so a change from the tray applies at once.</param>
    public IsoLibrary(Func<string> folder, ILogger? logger = null)
    {
        _folder = folder;
        _logger = logger;
    }

    public string Folder => Path.GetFullPath(_folder()).TrimEnd(Path.DirectorySeparatorChar);

    /// <returns>Null when <paramref name="name"/> is an acceptable image name, else why it is not.</returns>
    public static string? NameProblem(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength)
        {
            return $"Use a name of 1 to {MaxNameLength} characters ending in .iso.";
        }

        if (!trimmed.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || trimmed.Length == 4)
        {
            return "The name must end in .iso.";
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.StartsWith('.'))
        {
            return "The name cannot contain \\ / : * ? \" < > | or start with a period.";
        }

        return ReservedNames.Contains(Path.GetFileNameWithoutExtension(trimmed), StringComparer.OrdinalIgnoreCase)
            ? $"\"{trimmed}\" is reserved by Windows."
            : null;
    }

    /// <summary>The images in the library, sorted by name. Creates the folder if it is missing.</summary>
    /// <remarks>UsedBy is empty here; <see cref="IsoLibraryService"/> fills it in.</remarks>
    public IReadOnlyList<IsoImage> List()
    {
        var folder = Folder;
        try
        {
            Directory.CreateDirectory(folder);
            DeleteStalePartials(folder);
            return new DirectoryInfo(folder).EnumerateFiles("*.iso")
                .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint) && NameProblem(file.Name) is null)
                .Select(file => ToImage(file))
                .OrderBy(image => image.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "The ISO library folder {Folder} could not be read.", folder);
            return [];
        }
    }

    /// <summary>The full path of the image named <paramref name="name"/>, for attaching it to a VM.</summary>
    /// <exception cref="LifecycleValidationException">The name is not an image in the library (field isoName).</exception>
    public string Resolve(string name)
    {
        try
        {
            return Existing(name);
        }
        catch (Exception ex) when (ex is IsoNotFoundException or LifecycleValidationException)
        {
            throw new LifecycleValidationException(ex.Message, [new ValidationIssue("isoName", ex.Message)]);
        }
    }

    /// <summary>The full path of an image in the library.</summary>
    /// <exception cref="IsoNotFoundException">No image has this name.</exception>
    /// <exception cref="LifecycleValidationException">The name is invalid.</exception>
    public string PathOf(string name) => Existing(name);

    /// <summary>The image's details.</summary>
    /// <exception cref="IsoNotFoundException">No image has this name.</exception>
    public IsoImage Get(string name) => ToImage(new FileInfo(Existing(name)));

    /// <summary>Saves <paramref name="content"/> as a new image. Nothing appears in the library unless it completes.</summary>
    /// <param name="length">The expected size, when the sender declared it; a shorter body is an incomplete upload.</param>
    /// <exception cref="LifecycleValidationException">The name is invalid.</exception>
    /// <exception cref="LifecycleConflictException">An image with this name exists.</exception>
    /// <exception cref="InsufficientStorageException">The drive does not have room.</exception>
    public async Task<IsoImage> SaveAsync(string name, Stream content, long? length, CancellationToken cancellationToken)
    {
        name = Validate(name, "name");
        var folder = Folder;
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        if (File.Exists(target))
        {
            throw new LifecycleConflictException($"\"{name}\" is already in the ISO library. Rename or delete it first.");
        }

        if (length is { } expected && FreeBytes(folder) is { } free && expected > free)
        {
            throw new InsufficientStorageException(
                $"The ISO library's drive has {free / (1024 * 1024)} MB free; this image needs {expected / (1024 * 1024)} MB.");
        }

        var partial = Path.Combine(folder, $".{name}.{Guid.NewGuid():N}{PartialExtension}");
        try
        {
            long written = 0;
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
            {
                var buffer = new byte[CopyBufferBytes];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                }
            }

            if (length is { } declared && written != declared)
            {
                throw new IOException($"The upload ended after {written} of {declared} bytes.");
            }

            try
            {
                File.Move(partial, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                throw new LifecycleConflictException($"\"{name}\" was added to the ISO library while this upload was running.");
            }

            _logger?.LogInformation("Added {Name} ({Bytes} bytes) to the ISO library.", name, written);
            return ToImage(new FileInfo(target));
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <exception cref="IsoNotFoundException">No image has this name.</exception>
    /// <exception cref="LifecycleValidationException">The new name is invalid.</exception>
    /// <exception cref="LifecycleConflictException">Another image has the new name.</exception>
    public IsoImage Rename(string name, string newName)
    {
        var source = Existing(name);
        newName = Validate(newName, "newName");
        var target = Path.Combine(Folder, newName);
        var caseOnly = string.Equals(Path.GetFileName(source), newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(target))
        {
            throw new LifecycleConflictException($"\"{newName}\" is already in the ISO library.");
        }

        File.Move(source, target);
        _logger?.LogInformation("Renamed ISO {Name} to {NewName}.", Path.GetFileName(source), newName);
        return ToImage(new FileInfo(target));
    }

    /// <exception cref="IsoNotFoundException">No image has this name.</exception>
    public void Delete(string name)
    {
        var path = Existing(name);
        File.Delete(path);
        _logger?.LogInformation("Deleted ISO {Name}.", Path.GetFileName(path));
    }

    /// <summary>The full path of an existing image file in the library, never outside it.</summary>
    internal string Existing(string name)
    {
        name = Validate(name, "name");
        var path = Path.GetFullPath(Path.Combine(Folder, name));
        if (!string.Equals(Path.GetDirectoryName(path), Folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new IsoNotFoundException(name);
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new LifecycleValidationException($"\"{name}\" is a link; only files stored in the ISO library can be used.");
        }

        return path;
    }

    private static string Validate(string? name, string field)
    {
        if (NameProblem(name) is { } problem)
        {
            throw new LifecycleValidationException(problem, [new ValidationIssue(field, problem)]);
        }

        return name!.Trim();
    }

    private static IsoImage ToImage(FileInfo file) => new(file.Name, file.Length, file.LastWriteTimeUtc, []);

    private static long? FreeBytes(string folder)
    {
        try
        {
            return Path.GetPathRoot(folder) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes uploads interrupted long ago, for example by a service restart.</summary>
    private void DeleteStalePartials(string folder)
    {
        foreach (var partial in new DirectoryInfo(folder).EnumerateFiles("*" + PartialExtension))
        {
            if (partial.LastWriteTimeUtc < DateTime.UtcNow - StalePartialAge)
            {
                TryDelete(partial.FullName);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not delete {Path}.", path);
        }
    }
}

/// <summary>The ISO library together with which VMs use each image, so in-use images are not changed.</summary>
public sealed class IsoLibraryService
{
    private readonly IsoLibrary _library;
    private readonly IHyperVStorage _storage;

    public IsoLibraryService(IsoLibrary library, IHyperVStorage storage)
    {
        _library = library;
        _storage = storage;
    }

    public async Task<IReadOnlyList<IsoImage>> ListAsync(CancellationToken cancellationToken)
    {
        var images = _library.List();
        var users = await UsersAsync(cancellationToken).ConfigureAwait(false);
        return images.Select(image => image with { UsedBy = users.GetValueOrDefault(image.Name, []) }).ToList();
    }

    public Task<IsoImage> UploadAsync(string name, Stream content, long? length, CancellationToken cancellationToken) =>
        _library.SaveAsync(name, content, length, cancellationToken);

    /// <exception cref="LifecycleConflictException">A VM has the image attached.</exception>
    public async Task<IsoImage> RenameAsync(string name, string newName, CancellationToken cancellationToken)
    {
        await EnsureUnusedAsync(name, "renamed", cancellationToken).ConfigureAwait(false);
        return _library.Rename(name, newName);
    }

    /// <exception cref="LifecycleConflictException">A VM has the image attached.</exception>
    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        await EnsureUnusedAsync(name, "deleted", cancellationToken).ConfigureAwait(false);
        _library.Delete(name);
    }

    private async Task EnsureUnusedAsync(string name, string verb, CancellationToken cancellationToken)
    {
        var path = _library.Existing(name);
        var users = await UsersAsync(cancellationToken).ConfigureAwait(false);
        if (users.TryGetValue(Path.GetFileName(path), out var vms) && vms.Count > 0)
        {
            throw new LifecycleConflictException(
                $"\"{Path.GetFileName(path)}\" is attached to {string.Join(", ", vms)}. Remove it from the VM's DVD drive (or delete the checkpoint that uses it) before it can be {verb}.");
        }
    }

    /// <summary>Library file name to the names of the VMs that attach it.</summary>
    private async Task<Dictionary<string, IReadOnlyList<string>>> UsersAsync(CancellationToken cancellationToken)
    {
        var folder = _library.Folder;
        var storage = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        return storage.AttachedImages
            .Where(image => string.Equals(Path.GetDirectoryName(DiskPaths.Normalize(image.Path)), folder, StringComparison.OrdinalIgnoreCase))
            .GroupBy(image => Path.GetFileName(image.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(image => image.VmName).Distinct(StringComparer.CurrentCultureIgnoreCase).Order().ToList(),
                StringComparer.OrdinalIgnoreCase);
    }
}
