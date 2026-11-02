using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace HyperHarbor.Host.Tests.Packaging;

/// <summary>
/// The winget job in .github/workflows/release.yml: it runs only for published stable releases, and each package's
/// pattern picks that package's installer alone from a release's assets. winget-releaser matches asset names with
/// PowerShell's -match, which is the .NET regular expression engine used here (case-insensitive, as -match is).
/// </summary>
public sealed class ReleaseWorkflowTests
{
    private static readonly string[] ReleaseAssets =
        ["HyperHarbor-Host-0.2.6.exe", "HyperHarbor_0.2.6_x64-setup.exe", "HyperHarbor_0.2.6_x64-setup.exe.sig", "latest.json", "client-latest.json"];

    [Fact]
    public void WingetJob_RunsForPublishedStableReleases_AfterTheBetaChannel()
    {
        var job = Job("winget");
        var condition = (string)job["if"];

        Assert.Equal("beta-channel", job["needs"]);
        Assert.Contains("!github.event.release.prerelease", condition);
        Assert.Contains("github.event_name == 'release'", condition);
        Assert.DoesNotContain("'push'", condition);
        var inputs = (Dictionary<object, object>)Step(job)["with"];
        Assert.Equal("${{ secrets.WINGET_TOKEN }}", inputs["token"]);
        Assert.Equal("tylerfaulkner", inputs["fork-user"]);

        // The release event fires for "published" only, so drafts never start it.
        var release = (Dictionary<object, object>)((Dictionary<object, object>)Workflow()["on"])["release"];
        Assert.Equal(["published"], ((List<object>)release["types"]).Cast<string>());
    }

    [Fact]
    public void WingetJob_PinsTheActionToACommit()
    {
        Assert.Matches("^vedantmgoyal9/winget-releaser@[0-9a-f]{40}$", (string)Step(Job("winget"))["uses"]);
    }

    [Theory]
    [InlineData("FaulknerEngineering.HyperHarbor.Host", "HyperHarbor-Host-0.2.6.exe")]
    [InlineData("FaulknerEngineering.HyperHarbor.Client", "HyperHarbor_0.2.6_x64-setup.exe")]
    public void EachPackage_MatchesOnlyItsOwnInstaller(string identifier, string installer)
    {
        var entry = ((List<object>)((Dictionary<object, object>)((Dictionary<object, object>)Job("winget")["strategy"])["matrix"])["include"])
            .Cast<Dictionary<object, object>>()
            .Single(item => (string)item["identifier"] == identifier);
        var pattern = (string)entry["installers"];

        Assert.Equal([installer], ReleaseAssets.Where(asset => Regex.IsMatch(asset, pattern, RegexOptions.IgnoreCase)));
    }

    private static Dictionary<object, object> Job(string name) =>
        (Dictionary<object, object>)((Dictionary<object, object>)Workflow()["jobs"])[name];

    private static Dictionary<object, object> Step(Dictionary<object, object> job) =>
        ((List<object>)job["steps"]).Cast<Dictionary<object, object>>().Single(step => step.ContainsKey("uses"));

    private static Dictionary<object, object> Workflow()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".github", "workflows", "release.yml")))
        {
            directory = directory.Parent;
        }

        var yaml = File.ReadAllText(Path.Combine(directory!.FullName, ".github", "workflows", "release.yml"));
        return (Dictionary<object, object>)new DeserializerBuilder().Build().Deserialize<object>(yaml)!;
    }
}
