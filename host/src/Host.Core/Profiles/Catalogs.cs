using HyperHarbor.Shared.Contracts.Profiles;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>A package alias, also listed on the picker's Popular tab when Popular is set. catalogs/packages.yaml.</summary>
public sealed class PackageCatalogEntry
{
    public string Alias { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Popular { get; set; }
}

/// <summary>A browser extension offered in the picker. catalogs/extensions.yaml.</summary>
public sealed class ExtensionCatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>chrome (the default) or edge, for extensions only in the Edge Add-ons store.</summary>
    public string Store { get; set; } = "chrome";

    public string? EdgeId { get; set; }

    /// <summary>How a profile lists it: the id, or edge:&lt;id&gt; for an Edge-only entry.</summary>
    public string ProfileId => Store == "edge" ? ExtensionRef.EdgePrefix + Id : Id;
}

/// <summary>A provisioned Appx package with its removal rating. catalogs/appx.yaml.</summary>
public sealed class AppxCatalogEntry
{
    public string Name { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public AppxRating Rating { get; set; }
    public string? Note { get; set; }
}

/// <summary>A curated registry tweak. catalogs/tweaks.yaml.</summary>
public sealed class TweakCatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Note { get; set; }
    public List<TweakValue> Values { get; set; } = [];
}

/// <summary>One registry value of a tweak; Default null means the value is absent on a clean install.</summary>
public sealed class TweakValue
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Default { get; set; }
}

public sealed class BrowserCatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string WingetId { get; set; } = "";
    public string Alias { get; set; } = "";
    public string PolicyRoot { get; set; } = "";
    public string UserData { get; set; } = "";
    public string UpdateUrl { get; set; } = "";
    public string? EdgeUpdateUrl { get; set; }
}

public sealed class BrowserPolicyEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Policy { get; set; } = "";
    public string Type { get; set; } = "";
    public List<string> AppliesTo { get; set; } = [];
    public string? Description { get; set; }
    public Dictionary<string, string>? Values { get; set; }
}

/// <summary>An extension a browser installs by itself, by profile id.</summary>
public sealed class BuiltInExtension
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class BrowserCatalog
{
    public List<BrowserCatalogEntry> Browsers { get; set; } = [];
    public List<BrowserPolicyEntry> Policies { get; set; } = [];
    public List<BuiltInExtension> BuiltInExtensions { get; set; } = [];
}

/// <summary>
/// The curated catalogs in the repository's catalogs folder, embedded in the host. Loaded once; read-only.
/// </summary>
public sealed class Catalogs
{
    private static readonly Lazy<Catalogs> Embedded = new(() => new Catalogs(Read));

    internal Catalogs(Func<string, string> read)
    {
        var yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        Packages = yaml.Deserialize<List<PackageCatalogEntry>>(read("packages.yaml"));
        Extensions = yaml.Deserialize<List<ExtensionCatalogEntry>>(read("extensions.yaml"));
        Appx = yaml.Deserialize<List<AppxCatalogEntry>>(read("appx.yaml"));
        Tweaks = yaml.Deserialize<List<TweakCatalogEntry>>(read("tweaks.yaml"));
        var browsers = yaml.Deserialize<BrowserCatalog>(read("browsers.yaml"));
        Browsers = browsers.Browsers;
        Policies = browsers.Policies;
        BuiltInExtensions = browsers.BuiltInExtensions.Select(extension => extension.Id).ToHashSet(StringComparer.Ordinal);

        AliasIndex = Packages.ToDictionary(entry => entry.Alias, StringComparer.OrdinalIgnoreCase);
        PackageIdIndex = Packages.GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        AppxIndex = Appx.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        TweakIndex = Tweaks.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        PolicyIndex = Policies.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        ExtensionIndex = Extensions
            .SelectMany(entry => entry.EdgeId is null ? [(entry.ProfileId, entry)] : new[] { (entry.ProfileId, entry), (ExtensionRef.EdgePrefix + entry.EdgeId, entry) })
            .ToDictionary(pair => pair.Item1, pair => pair.entry, StringComparer.Ordinal);
    }

    /// <summary>The catalogs shipped with this host.</summary>
    public static Catalogs Default => Embedded.Value;

    public IReadOnlyList<PackageCatalogEntry> Packages { get; }

    public IReadOnlyList<ExtensionCatalogEntry> Extensions { get; }

    public IReadOnlyList<AppxCatalogEntry> Appx { get; }

    public IReadOnlyList<TweakCatalogEntry> Tweaks { get; }

    public IReadOnlyList<BrowserCatalogEntry> Browsers { get; }

    public IReadOnlyList<BrowserPolicyEntry> Policies { get; }

    /// <summary>Profile ids of extensions browsers install by themselves.</summary>
    public IReadOnlySet<string> BuiltInExtensions { get; }

    public IReadOnlyDictionary<string, PackageCatalogEntry> AliasIndex { get; }

    public IReadOnlyDictionary<string, PackageCatalogEntry> PackageIdIndex { get; }

    public IReadOnlyDictionary<string, AppxCatalogEntry> AppxIndex { get; }

    public IReadOnlyDictionary<string, TweakCatalogEntry> TweakIndex { get; }

    public IReadOnlyDictionary<string, BrowserPolicyEntry> PolicyIndex { get; }

    /// <summary>Catalog extensions by profile id: Chrome Web Store ids, and edge:&lt;id&gt; for Edge ids.</summary>
    public IReadOnlyDictionary<string, ExtensionCatalogEntry> ExtensionIndex { get; }

    /// <summary>The browser a profile's browser app names, by winget id or alias.</summary>
    public BrowserCatalogEntry? FindBrowser(string app) => Browsers.FirstOrDefault(browser =>
        string.Equals(browser.WingetId, app, StringComparison.OrdinalIgnoreCase) || string.Equals(browser.Alias, app, StringComparison.OrdinalIgnoreCase));

    /// <summary>The embedded profile JSON Schema (schemas/profile.v1.schema.json).</summary>
    public static string ProfileSchema => ReadResource("HyperHarbor.Schemas.profile.v1.schema.json");

    private static string Read(string file) => ReadResource("HyperHarbor.Catalogs." + file);

    private static string ReadResource(string name)
    {
        using var stream = typeof(Catalogs).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is missing from the host.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
