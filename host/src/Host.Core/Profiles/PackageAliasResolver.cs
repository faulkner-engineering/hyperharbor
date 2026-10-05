using System.Text.RegularExpressions;

namespace HyperHarbor.Host.Core.Profiles;

public enum InstallEntryKind
{
    Invalid,
    Winget,
    MicrosoftStore,
    Alias,
}

public enum PackageSource
{
    Winget,
    MicrosoftStore,
}

/// <summary>A package to install, with the alias it came from when there was one.</summary>
public sealed record PackageRef(string Id, PackageSource Source, string? Name, string? Alias);

/// <summary>An install entry that names an alias the package catalog does not have.</summary>
public sealed class UnknownAliasException(string alias)
    : Exception($"\"{alias}\" is not a known package alias. Use the winget id instead, for example Git.Git.")
{
    public string Alias { get; } = alias;
}

/// <summary>Tells install entries apart and resolves aliases to winget ids (when a profile is applied).</summary>
public sealed partial class PackageAliasResolver(Catalogs catalogs)
{
    /// <summary>winget ids contain a dot: Publisher.Name, sometimes with more parts.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9+_-]*(\.[A-Za-z0-9+_-]+)+$")]
    private static partial Regex WingetId();

    /// <summary>Microsoft Store product ids: 12 capital letters and digits, for example 9NBLGGH4NNS1.</summary>
    [GeneratedRegex(@"^[0-9A-Z]{12}$")]
    private static partial Regex StoreId();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex Alias();

    public static InstallEntryKind Classify(string entry) => entry switch
    {
        _ when WingetId().IsMatch(entry) => InstallEntryKind.Winget,
        _ when StoreId().IsMatch(entry) => InstallEntryKind.MicrosoftStore,
        _ when Alias().IsMatch(entry) => InstallEntryKind.Alias,
        _ => InstallEntryKind.Invalid,
    };

    /// <exception cref="UnknownAliasException">An alias the catalog does not have.</exception>
    /// <exception cref="ArgumentException">Not a winget id, a Store id, or an alias.</exception>
    public PackageRef Resolve(string entry)
    {
        var trimmed = entry.Trim();
        switch (Classify(trimmed))
        {
            case InstallEntryKind.Winget:
                return new PackageRef(trimmed, PackageSource.Winget, catalogs.PackageIdIndex.GetValueOrDefault(trimmed)?.Name, null);
            case InstallEntryKind.MicrosoftStore:
                return new PackageRef(trimmed, PackageSource.MicrosoftStore, null, null);
            case InstallEntryKind.Alias:
                var known = catalogs.AliasIndex.GetValueOrDefault(trimmed) ?? throw new UnknownAliasException(trimmed);
                return new PackageRef(known.Id, PackageSource.Winget, known.Name, known.Alias);
            default:
                throw new ArgumentException($"\"{entry}\" is not a winget id, a Microsoft Store id, or a package alias.", nameof(entry));
        }
    }

    /// <summary>The friendly name for an install entry, when the catalog knows it.</summary>
    public string? NameOf(string entry) => Classify(entry) switch
    {
        InstallEntryKind.Alias => catalogs.AliasIndex.GetValueOrDefault(entry)?.Name,
        InstallEntryKind.Winget => catalogs.PackageIdIndex.GetValueOrDefault(entry)?.Name,
        _ => null,
    };
}
