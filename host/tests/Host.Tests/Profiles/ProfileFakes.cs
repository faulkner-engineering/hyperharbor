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
