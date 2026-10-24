using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

public sealed class ProfileImageNotFoundException(string name) : Exception($"There is no wallpaper image named \"{name}\".");

/// <summary>
/// Each User's wallpaper images for machine profiles, in &lt;data&gt;\machine-profiles\&lt;user id&gt;\images with the
/// ProtectedFile ACL. A profile names an image; the host copies it into the guest when the profile is applied.
/// </summary>
public sealed partial class ProfileImageStore(string dataDirectory)
{
    public const string FolderName = "images";
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int MaxImages = 50;

    /// <summary>Where images go in the guest; the wallpaper setting points here.</summary>
    public const string GuestFolder = @"C:\Users\Public\Pictures\HyperHarbor";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _.()-]{0,95}\.(jpg|jpeg|png|bmp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageName();

    private readonly string _root = Path.Combine(dataDirectory, MachineProfileStore.FolderName);
    private readonly object _gate = new();

    public static bool IsValidName(string name) => ImageName().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>The image's path in the guest once copied.</summary>
    public static string GuestPath(string name) => Path.Combine(GuestFolder, name);

    public IReadOnlyList<ProfileImage> List(Guid userId)
    {
        lock (_gate)
        {
            var folder = Folder(userId);
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder)
                    .Select(path => new FileInfo(path))
                    .Where(file => IsValidName(file.Name))
                    .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(file => new ProfileImage(file.Name, file.Length))
                    .ToList()
                : [];
        }
    }

    /// <summary>The image's file on the host, or null when the User has no image of that name.</summary>
    public string? PathOf(Guid userId, string name)
    {
        if (!IsValidName(name))
        {
            return null;
        }

        var path = Path.Combine(Folder(userId), name);
        return File.Exists(path) ? path : null;
    }

    /// <exception cref="LifecycleValidationException">The name, size, or content is not an image the host takes.</exception>
    public ProfileImage Save(Guid userId, string name, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!IsValidName(name))
        {
            throw Invalid("name", "Use a file name ending in .jpg, .jpeg, .png, or .bmp, with letters, digits, spaces, dots, hyphens, or underscores.");
        }

        if (content.Length is 0 or > MaxBytes)
        {
            throw Invalid("content", $"An image is at most {MaxBytes / 1024 / 1024} MB.");
        }

        if (!LooksLikeImage(content))
        {
            throw Invalid("content", "The file is not a JPEG, PNG, or BMP image.");
        }

        lock (_gate)
        {
            var folder = Folder(userId);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            if (!File.Exists(path) && Directory.EnumerateFiles(folder).Count() >= MaxImages)
            {
                throw Invalid("name", $"A User can keep {MaxImages} images. Delete one first.");
            }

            ProtectedFile.WriteAllBytes(path, content);
            return new ProfileImage(name, content.Length);
        }
    }

    /// <exception cref="ProfileImageNotFoundException">The User has no image of that name.</exception>
    public void Delete(Guid userId, string name)
    {
        lock (_gate)
        {
            File.Delete(PathOf(userId, name) ?? throw new ProfileImageNotFoundException(name));
        }
    }

    /// <summary>JPEG (FF D8 FF), PNG (89 50 4E 47), or BMP (42 4D) by the first bytes, whatever the name says.</summary>
    internal static bool LooksLikeImage(byte[] content) =>
        content is [0xFF, 0xD8, 0xFF, ..]
        || content is [0x89, 0x50, 0x4E, 0x47, ..]
        || content is [0x42, 0x4D, ..];

    private static LifecycleValidationException Invalid(string field, string message) => new(message, [new ValidationIssue(field, message)]);

    private string Folder(Guid userId) => Path.Combine(_root, userId.ToString("D"), FolderName);
}
