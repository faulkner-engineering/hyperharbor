using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Core.Updates;

/// <summary>Thrown when a manifest or package does not meet the update rules; nothing is installed.</summary>
public sealed class UpdateRejectedException(string message) : Exception(message);

/// <summary>One downloadable file of a release.</summary>
public sealed record UpdatePackage(
    [property: JsonRequired] string Component,
    [property: JsonRequired] string Arch,
    [property: JsonRequired] string Url,
    [property: JsonRequired] long Size,
    [property: JsonRequired] string Sha256);

/// <summary>
/// latest.json, attached to each GitHub release. Schema version 1:
/// <code>
/// { "schemaVersion": 1, "channel": "stable", "version": "0.4.0", "publishedAt": "2026-11-01T12:00:00Z",
///   "minimumUpdateFrom": "0.3.0", "dataFormat": 2, "apiVersion": "1.9.0", "notesUrl": "https://…",
///   "packages": [ { "component": "host", "arch": "x64", "url": "https://…/HyperHarbor-Host-0.4.0.exe",
///                   "size": 98765432, "sha256": "…64 hex…" } ],
///   "verification": { } }
/// </code>
/// "verification" is reserved for signatures (Authenticode, Sigstore) and is not read yet.
/// </summary>
public sealed record UpdateManifest(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Channel,
    [property: JsonRequired] string Version,
    DateTimeOffset? PublishedAt,
    string? MinimumUpdateFrom,
    int? DataFormat,
    string? ApiVersion,
    string? NotesUrl,
    [property: JsonRequired] IReadOnlyList<UpdatePackage> Packages,
    JsonElement? Verification)
{
    public const int SupportedSchemaVersion = 1;
    public const string HostComponent = "host";
    public const string HostArch = "x64";

    /// <summary>Longest manifest read, in bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SemanticVersion ParsedVersion => SemanticVersion.Parse(Version);

    /// <summary>The host package (component "host", arch "x64").</summary>
    public UpdatePackage HostPackage => Packages.Single(package =>
        string.Equals(package.Component, HostComponent, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(package.Arch, HostArch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses and checks a manifest: schema, versions, notes URL, and the host package.</summary>
    /// <exception cref="UpdateRejectedException">The manifest breaks a rule.</exception>
    public static UpdateManifest Parse(ReadOnlySpan<byte> json, UpdateOptions options)
    {
        UpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(Utf8Json.WithoutBom(json), JsonOptions) ?? throw new UpdateRejectedException("The update manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new UpdateRejectedException($"The update manifest is not valid: {ex.Message}");
        }

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new UpdateRejectedException($"The update manifest uses schema {manifest.SchemaVersion}; this version reads schema {SupportedSchemaVersion}. Install a newer version by hand.");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            throw new UpdateRejectedException($"\"{manifest.Version}\" is not a version number.");
        }

        if (manifest.MinimumUpdateFrom is { } minimum && !SemanticVersion.TryParse(minimum, out _))
        {
            throw new UpdateRejectedException($"minimumUpdateFrom \"{minimum}\" is not a version number.");
        }

        if (manifest.NotesUrl is { } notes && !(Uri.TryCreate(notes, UriKind.Absolute, out var notesUri) && notesUri.Scheme == Uri.UriSchemeHttps))
        {
            throw new UpdateRejectedException("The release notes link must be an https URL.");
        }

        var hosts = manifest.Packages.Count(package =>
            string.Equals(package.Component, HostComponent, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(package.Arch, HostArch, StringComparison.OrdinalIgnoreCase));
        if (hosts != 1)
        {
            throw new UpdateRejectedException($"The update manifest must list exactly one {HostComponent} package for {HostArch}; it lists {hosts}.");
        }

        var package = manifest.HostPackage;
        CheckUrl(package.Url, options);
        if (package.Size <= 0 || package.Size > options.MaxPackageBytes)
        {
            throw new UpdateRejectedException($"The package size {package.Size} is outside 1 to {options.MaxPackageBytes} bytes.");
        }

        if (package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit))
        {
            throw new UpdateRejectedException("The package's sha256 must be 64 hexadecimal characters.");
        }

        return manifest;
    }

    /// <summary>Only https, and only an allowed host, for the manifest, the package, and every redirect.</summary>
    /// <exception cref="UpdateRejectedException">The URL breaks the rule.</exception>
    public static Uri CheckUrl(string url, UpdateOptions options)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateRejectedException($"{url} is not an https URL.");
        }

        if (!options.AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
        {
            throw new UpdateRejectedException($"Updates may not come from {uri.IdnHost}.");
        }

        return uri;
    }
}
