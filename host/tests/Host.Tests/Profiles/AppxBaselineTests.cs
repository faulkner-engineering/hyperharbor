using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Tests.Profiles;

public sealed class AppxBaselineDiffTests
{
    [Fact]
    public void Packages_AreRemovedAddedOrKept()
    {
        var diff = AppxDiff.Compare(
            ["Microsoft.WindowsStore", "Microsoft.BingNews", "Clipchamp.Clipchamp", "Microsoft.GamingApp"],
            ["Microsoft.WindowsStore", "Microsoft.GamingApp", "Contoso.Extra"]);

        Assert.Equal(["Clipchamp.Clipchamp", "Microsoft.BingNews"], diff.Removed);
        Assert.Equal(["Contoso.Extra"], diff.Added);
        Assert.Equal(["Microsoft.GamingApp", "Microsoft.WindowsStore"], diff.Kept);
    }

    [Fact]
    public void Names_AreComparedWithoutCase_AndDuplicatesCountOnce()
    {
        var diff = AppxDiff.Compare(
            ["microsoft.windowscommunicationsapps", "Microsoft.BingNews", "Microsoft.BingNews"],
            ["Microsoft.WindowsCommunicationsApps", "MICROSOFT.BINGNEWS"]);

        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Added);
        Assert.Equal(2, diff.Kept.Count);
    }

    [Fact]
    public void AnEmptyBaseline_MakesEverythingAdded()
    {
        var diff = AppxDiff.Compare([], ["Microsoft.BingNews"]);

        Assert.Equal(["Microsoft.BingNews"], diff.Added);
        Assert.Empty(diff.Removed);
    }
}

public sealed class AppxBaselineStoreTests : IDisposable
{
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private static AppxBaseline Baseline(string build, string edition, params string[] packages) => new(
        AppxBaseline.KeyOf(build, edition),
        $"10.0.{build}.1000",
        edition,
        new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero),
        new AppxBaselineOrigin(AppxBaselineSource.Manual, Guid.NewGuid(), "Clean VM"),
        packages);

    [Fact]
    public void TheExactBuildAndEdition_IsFound_ThenTheSameBuildAsApproximate_ThenNothing()
    {
        var store = new AppxBaselineStore(_data);
        store.Save(Baseline("26100", "Professional", "Microsoft.BingNews"));
        store.Save(Baseline("22631", "Enterprise", "Microsoft.BingWeather"));

        var exact = store.Find("26100", "Professional");
        var approximate = store.Find("22631", "Professional");
        var none = store.Find("19045", "Professional");

        Assert.Equal(("10.0.26100/Professional", false), (exact!.Value.Baseline.Key, exact.Value.Approximate));
        Assert.Equal(("10.0.22631/Enterprise", true), (approximate!.Value.Baseline.Key, approximate.Value.Approximate));
        Assert.Null(none);
    }

    [Fact]
    public void OnlyIfMissing_KeepsTheFirstBaseline_AndAManualSaveReplacesIt()
    {
        var store = new AppxBaselineStore(_data);

        Assert.True(store.Save(Baseline("26100", "Professional", "First")));
        Assert.False(store.Save(Baseline("26100", "Professional", "Second"), onlyIfMissing: true));
        Assert.Equal(["First"], store.Find("26100", "Professional")!.Value.Baseline.Packages);

        Assert.True(store.Save(Baseline("26100", "Professional", "Third")));
        Assert.Equal(["Third"], new AppxBaselineStore(_data).Find("26100", "Professional")!.Value.Baseline.Packages);
    }

    [Fact]
    public void TheFile_HasTheProposedShape()
    {
        new AppxBaselineStore(_data).Save(Baseline("26100", "Professional", "Microsoft.BingNews"));

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(_data, AppxBaselineStore.FileName)))!;

        Assert.Equal(1, (int)json["schemaVersion"]!);
        var baseline = json["baselines"]![0]!;
        Assert.Equal("10.0.26100/Professional", (string?)baseline["key"]);
        Assert.Equal("manual", (string?)baseline["source"]!["how"]);
        Assert.Equal("Microsoft.BingNews", (string?)baseline["packages"]![0]);
    }
}
