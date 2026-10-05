using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Profiles;

/// <summary>Something capture found, which the review screen offers with a checkbox. Schema: DraftItem.</summary>
/// <param name="Id">What goes into the profile: a winget id, a package name, a tweak id, or an extension id.</param>
/// <param name="Selected">Whether the review screen starts with it ticked.</param>
/// <param name="Note">Why it starts unticked, or other context.</param>
public sealed record DraftItem(
    string Id,
    string Name,
    bool Selected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Note);

/// <summary>A browser with the store extensions found in it. Schema: DraftBrowser.</summary>
/// <param name="App">The browser's winget id, for the profile's browser.app.</param>
/// <param name="Selected">The browser the review screen starts with (the one with the most extensions).</param>
public sealed record DraftBrowser(string App, string Name, bool Selected, IReadOnlyList<DraftItem> Extensions);

/// <summary>
/// What a VM has that a clean install does not, as a starting point for a setup profile. Nothing is saved
/// until the client saves a profile from it. Schema: ProfileDraft.
/// </summary>
/// <param name="Baseline">The clean Appx baseline used for RemoveAppx; null when there is none.</param>
/// <param name="Install">From winget export, run in the VM.</param>
/// <param name="RemoveAppx">Provisioned packages in the baseline that the VM no longer has.</param>
/// <param name="Tweaks">Catalog tweaks whose values are set in the VM.</param>
/// <param name="OtherPrograms">Installed programs, listed only when winget could not run in the VM.</param>
/// <param name="Warnings">What could not be read, in words.</param>
public sealed record ProfileDraft(
    Guid VmId,
    string VmName,
    string Build,
    string Edition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AppxBaselineInfo? Baseline,
    IReadOnlyList<DraftItem> Install,
    IReadOnlyList<DraftItem> RemoveAppx,
    IReadOnlyList<DraftItem> Tweaks,
    IReadOnlyList<DraftBrowser> Browsers,
    IReadOnlyList<string> OtherPrograms,
    IReadOnlyList<string> Warnings);
