using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Tests.Profiles;

/// <summary>A Windows guest whose provisioned packages tests can change.</summary>
internal sealed class FakeGuestProfileReader : IGuestProfileReader
{
    public string CurrentBuild { get; set; } = "26100";

    public string Edition { get; set; } = "Professional";

    public List<string> Appx { get; set; } = ["Microsoft.WindowsStore", "Microsoft.BingNews", "Clipchamp.Clipchamp", "Microsoft.GamingApp"];

    /// <summary>Thrown by every read, for example a guest error with the admin password in it.</summary>
    public Exception? Failure { get; set; }

    public List<GuestCredential> AdminsUsed { get; } = [];

    public Task<GuestAppxInventory> ReadAppxAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken)
    {
        AdminsUsed.Add(admin);
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(new GuestAppxInventory(
            CurrentBuild,
            2033,
            Edition,
            Appx.Select(name => new GuestAppxPackage(name, "1.0.0.0", "8wekyb3d8bbwe")).ToList()));
    }

    /// <summary>winget export's ids; null as when winget cannot run in the guest.</summary>
    public List<string>? Winget { get; set; } = ["Git.Git", "Microsoft.VisualStudioCode", "Microsoft.Edge", "Microsoft.VCRedist.2015+.x64"];

    public List<string> Programs { get; set; } = ["Git", "Microsoft Visual Studio Code (User)", "Contoso Tool"];

    public List<GuestExtension> Extensions { get; set; } =
    [
        new("brave", "eimadpbcbfnmbkopoojfekhnkhdbieeh", "Dark Reader"),
        new("brave", "ghbmnnjooekpmoecnnnilnnbdlolhkhi", "Google Docs Offline"),
        new("edge", "edge:odfafepnkmbhccpbejgmiehpchacaeak", "uBlock Origin"),
    ];

    /// <summary>Registry values by "key|name"; anything not here reads as absent.</summary>
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|HideFileExt"] = "0",
        [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize|AppsUseLightTheme"] = "0",
        [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize|SystemUsesLightTheme"] = "1",
    };

    public bool ProfileFound { get; set; } = true;

    public List<string> AccountsRead { get; } = [];

    public Task<GuestInstalledSoftware> ReadInstalledAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken)
    {
        AdminsUsed.Add(admin);
        return Failure is not null
            ? throw Failure
            : Task.FromResult(new GuestInstalledSoftware(Winget, Winget is null ? "winget export wrote nothing (exit code -1978335212)." : null, Programs));
    }

    public Task<GuestBrowsersAndSettings> ReadBrowsersAndSettingsAsync(
        Guid vmId,
        GuestCredential admin,
        string userAccount,
        IReadOnlyList<(string Id, string UserData)> browsers,
        IReadOnlyList<GuestRegistryRead> values,
        CancellationToken cancellationToken)
    {
        AdminsUsed.Add(admin);
        AccountsRead.Add(userAccount);
        if (Failure is not null)
        {
            throw Failure;
        }

        var read = values.ToDictionary(value => $"{value.Key}|{value.Name}", value => Values.GetValueOrDefault($"{value.Key}|{value.Name}"), StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new GuestBrowsersAndSettings(ProfileFound, Extensions, read));
    }
}

/// <summary>Package search with fixed results, or unavailable like a host without PowerShell 7.</summary>
internal sealed class FakePackageSearch : IPackageSearch
{
    public bool Available { get; set; } = true;

    public List<(string Query, int Count)> Searches { get; } = [];

    public Task<IReadOnlyList<Shared.Contracts.Profiles.PackageSearchResult>> SearchAsync(string query, int count, CancellationToken cancellationToken)
    {
        Searches.Add((query, count));
        if (!Available)
        {
            throw new PackageSearchUnavailableException("Package search is not set up on this host.");
        }

        IReadOnlyList<Shared.Contracts.Profiles.PackageSearchResult> results = [new("Git.Git", "Git", "2.55.0.5", "winget"), new("Microsoft.Git", "Git", "2.55.0.0.10", "winget")];
        return Task.FromResult(results);
    }
}

/// <summary>Resolves through the real catalog, and finds one extension "in the store" without the network.</summary>
internal sealed class FakeExtensionResolver : IExtensionResolver
{
    public const string StoreOnlyId = "ghbmnnjooekpmoecnnnilnnbdlolhkhi";

    public Task<Shared.Contracts.Profiles.ResolvedExtension> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        var parsed = ExtensionIdParser.Parse(input) ?? throw new ExtensionInputException("Paste a store link or an extension id.");
        if (Catalogs.Default.ExtensionIndex.TryGetValue(parsed.ProfileId, out var known))
        {
            return Task.FromResult(new Shared.Contracts.Profiles.ResolvedExtension(parsed.Store, parsed.Id, parsed.ProfileId, known.Name, null, true));
        }

        return parsed.Id == StoreOnlyId
            ? Task.FromResult(new Shared.Contracts.Profiles.ResolvedExtension(parsed.Store, parsed.Id, parsed.ProfileId, "Google Docs Offline", "data:image/png;base64,iVBORw0KGgo=", false))
            : throw new ExtensionNotFoundException($"The store has no extension {parsed.Id}.");
    }
}
