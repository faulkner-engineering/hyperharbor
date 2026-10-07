using HyperHarbor.Host.Core.HostProfiles;
using HyperHarbor.Host.Core.Profiles;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.HostProfiles;

/// <summary>
/// Reads this PC with the real scripts and prints what the Lean host profile would change. Read-only: it never
/// applies anything. Run it from an elevated shell (the app queries need administrator rights):
/// HH_HOSTLEAN_LIVE=1 dotnet test HyperHarbor.sln --filter HostLeanLiveTests
/// </summary>
public sealed class HostLeanLiveTests(ITestOutputHelper output)
{
    [EnvironmentFact("HH_HOSTLEAN_LIVE")]
    public async Task TheRealHost_CanBeRead_AndThereIsADryRun()
    {
        var system = new PowerShellHostSystem();
        var planner = new SetupProfilePlanner(Catalogs.Default);
        var validator = new ProfileValidator(Catalogs.Default, HostCatalogs.Default);
        var profile = validator.Normalize(ProfileYamlReader.Read(HostLeanProfiles.LeanYaml()));
        var plan = planner.Plan(profile);

        var snapshot = await system.InspectAsync(HostDiffer.Probes(plan), CancellationToken.None);
        var games = new SteamLibrary(SteamLibrary.FolderFromRegistry).InstalledGames();
        var diff = new HostDiffer(Catalogs.Default, HostCatalogs.Default).Diff(plan, snapshot, games);

        output.WriteLine($"Services {snapshot.Services.Count}, startup entries {snapshot.Startup.Count}, apps {snapshot.Appx.Count}, programs {snapshot.Programs.Count}, printers {snapshot.Printers.Count}, Steam games {games.Count}.");
        output.WriteLine($"Power: active {snapshot.Power.ActivePlan}, plans {string.Join(", ", snapshot.Power.Plans.Select(plan => plan.Name))}, armed [{string.Join("; ", snapshot.Power.ArmedDevices)}], network [{string.Join("; ", snapshot.Power.NetworkDevices)}].");
        output.WriteLine($"{diff.ChangeCount} changes, {diff.AlreadyInPlace} already in place, {diff.Kept.Count} kept, {diff.Problems.Count} problems.");
        foreach (var line in diff.Lines())
        {
            output.WriteLine($"  [{line.Handler}] {line.Item}: {line.Text}");
        }

        foreach (var note in diff.Kept)
        {
            output.WriteLine($"  kept: {note}");
        }

        foreach (var problem in diff.Problems.Concat(snapshot.Warnings))
        {
            output.WriteLine($"  problem: {problem}");
        }

        Assert.NotEmpty(snapshot.Services);
        Assert.NotEmpty(snapshot.Power.Plans);
        Assert.Equal(diff.Fingerprint(), new HostDiffer(Catalogs.Default, HostCatalogs.Default).Diff(plan, snapshot, games).Fingerprint());
    }
}
