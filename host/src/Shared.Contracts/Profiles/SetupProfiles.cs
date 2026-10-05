using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Profiles;

/// <summary>
/// What to install, remove, and change in a Windows VM, kept as YAML on the host
/// (schemas/profile.v1.schema.json). Schema: SetupProfile.
/// </summary>
/// <param name="Install">winget ids (they contain a dot), Microsoft Store ids (12 capitals and digits), or aliases from the package catalog.</param>
/// <param name="Tweaks">Curated tweaks by id, or custom registry values.</param>
public sealed record SetupProfile(
    [property: JsonRequired] string Name,
    string? Description,
    IReadOnlyList<ProfileItem>? Install,
    ProfileRemove? Remove,
    IReadOnlyList<ProfileTweak>? Tweaks,
    ProfileBrowser? Browser);

/// <summary>An id with its friendly name; the name is written as a trailing comment in YAML. Schema: ProfileItem.</summary>
public sealed record ProfileItem([property: JsonRequired] string Id, string? Name = null);

/// <summary>What to remove from Windows. Schema: ProfileRemove.</summary>
/// <param name="Appx">Provisioned package names without the version, for example Microsoft.BingNews.</param>
/// <param name="Capabilities">Full capability names, for example Browser.InternetExplorer~~~~0.0.11.0.</param>
/// <param name="Features">Optional feature names, for example MicrosoftWindowsPowerShellV2Root.</param>
public sealed record ProfileRemove(
    IReadOnlyList<ProfileItem>? Appx,
    IReadOnlyList<ProfileItem>? Capabilities,
    IReadOnlyList<ProfileItem>? Features);

/// <summary>A curated tweak (Id) or a custom registry value (Registry), never both. Schema: ProfileTweak.</summary>
public sealed record ProfileTweak(string? Id = null, string? Name = null, RegistryTweak? Registry = null);

public enum RegistryValueType
{
    Dword,
    Qword,
    String,
}

/// <summary>
/// A registry value to set. HKCU values apply to the User's VM account. Schema: RegistryTweak.
/// </summary>
/// <param name="Key">Starts with HKLM\ or HKCU\.</param>
/// <param name="Name">The value name; empty for the key's default value.</param>
/// <param name="Value">Decimal for dword and qword, the text for string.</param>
public sealed record RegistryTweak(
    [property: JsonRequired] string Key,
    [property: JsonRequired] string Name,
    [property: JsonRequired] RegistryValueType Type,
    [property: JsonRequired] string Value);

/// <summary>A browser to install, with extensions and policies. Schema: ProfileBrowser.</summary>
/// <param name="App">The browser's winget id or alias (Chrome, Edge, or Brave).</param>
/// <param name="Extensions">Chrome Web Store ids, or edge:&lt;id&gt; for the Edge Add-ons store.</param>
/// <param name="Policies">Friendly policy keys from the browser catalog, with values as text (true, false, numbers, or text).</param>
public sealed record ProfileBrowser(
    [property: JsonRequired] ProfileItem App,
    IReadOnlyList<ProfileItem>? Extensions,
    IReadOnlyDictionary<string, string>? Policies);

/// <summary>A saved setup profile as listed. Schema: SetupProfileSummary.</summary>
/// <param name="Id">The file name on the host, without .yaml.</param>
/// <param name="Error">Set when the file on the host cannot be read (for example after a hand edit); the counts are then zero.</param>
public sealed record SetupProfileSummary(
    string Id,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Description,
    int InstallCount,
    int RemoveCount,
    int TweakCount,
    int ExtensionCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Browser,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error);

/// <summary>A saved setup profile. Schema: StoredSetupProfile.</summary>
public sealed record StoredSetupProfile(string Id, DateTimeOffset UpdatedAt, SetupProfile Profile);

/// <summary>A winget package found by search. Schema: PackageSearchResult.</summary>
public sealed record PackageSearchResult(string Id, string Name, string Version, string Source);

/// <summary>A package from HyperHarbor's package catalog (aliases and the Popular list). Schema: PackageCatalogItem.</summary>
/// <param name="Alias">What a profile may write instead of the winget id.</param>
public sealed record PackageCatalogItem(string Alias, string Id, string Name, string Category, bool Popular);

/// <summary>Which store an extension id belongs to. Schema: ExtensionStore.</summary>
public enum ExtensionStore
{
    /// <summary>The Chrome Web Store; its ids work in Chrome, Brave, and Edge.</summary>
    Chrome,

    /// <summary>The Edge Add-ons store; profiles write these ids as edge:&lt;id&gt;.</summary>
    Edge,
}

/// <summary>An extension from HyperHarbor's extension catalog. Schema: ExtensionCatalogItem.</summary>
/// <param name="Id">How a profile lists it: the Chrome Web Store id, or edge:&lt;id&gt; for an Edge-only entry.</param>
/// <param name="EdgeId">The same extension in the Edge Add-ons store (edge:&lt;id&gt;), when it is there.</param>
public sealed record ExtensionCatalogItem(
    string Id,
    string Name,
    string Category,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? EdgeId);

/// <summary>An extension found from a store URL or id. Schema: ResolvedExtension.</summary>
/// <param name="ProfileId">How a profile lists it: the id, or edge:&lt;id&gt;.</param>
/// <param name="IconDataUrl">The store icon as a data: URL, so the client loads nothing remote; null for catalog entries and when the store has none.</param>
public sealed record ResolvedExtension(
    ExtensionStore Store,
    string Id,
    string ProfileId,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? IconDataUrl,
    bool InCatalog);

/// <summary>A curated tweak, for the profile editor. Schema: TweakCatalogItem.</summary>
public sealed record TweakCatalogItem(
    string Id,
    string Name,
    string Category,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Note);

/// <summary>A browser a profile can configure. Schema: BrowserCatalogItem.</summary>
public sealed record BrowserCatalogItem(string Id, string Name, string WingetId, string Alias);

/// <summary>A browser policy a profile can set by its friendly key. Schema: BrowserPolicyItem.</summary>
/// <param name="Type">boolean, integer, or string.</param>
/// <param name="AppliesTo">Browser ids (chrome, edge, brave).</param>
/// <param name="Values">For integers with fixed meanings: value to label.</param>
public sealed record BrowserPolicyItem(
    string Key,
    string Name,
    string Type,
    IReadOnlyList<string> AppliesTo,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyDictionary<string, string>? Values);

/// <summary>What the profile editor offers besides packages and extensions. Schema: SetupProfileCatalog.</summary>
public sealed record SetupProfileCatalog(
    IReadOnlyList<TweakCatalogItem> Tweaks,
    IReadOnlyList<BrowserCatalogItem> Browsers,
    IReadOnlyList<BrowserPolicyItem> Policies);
