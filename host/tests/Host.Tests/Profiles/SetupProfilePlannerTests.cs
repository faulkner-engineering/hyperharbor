using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Tests.Profiles;

public sealed class SetupProfilePlannerTests
{
    private static readonly SetupProfilePlanner Planner = new(Catalogs.Default);

    private static SetupProfile Profile(
        IReadOnlyList<ProfileItem>? install = null,
        ProfileRemove? remove = null,
        IReadOnlyList<ProfileTweak>? tweaks = null,
        ProfileBrowser? browser = null) =>
        new("Workstation", null, install, remove, tweaks, browser);

    [Fact]
    public void Packages_ResolveAliases_AndAnUnknownAliasIsAProblem()
    {
        var plan = Planner.Plan(Profile(install: [new("vscode"), new("Git.Git", "Git"), new("not-a-package")]));

        Assert.Equal(["Microsoft.VisualStudioCode", "Git.Git"], plan.Packages.Select(package => package.Id));
        Assert.Equal("Git", plan.Packages[1].Item);
        Assert.Contains(plan.Problems, problem => problem.Contains("not-a-package", StringComparison.Ordinal));
    }

    [Fact]
    public void Browser_IsInstalledOnce()
    {
        var withAlias = Planner.Plan(Profile(browser: new(new("brave"), null, null)));
        var alreadyListed = Planner.Plan(Profile(install: [new("Brave.Brave")], browser: new(new("brave"), null, null)));

        Assert.Equal("Brave.Brave", Assert.Single(withAlias.Packages).Id);
        Assert.Equal("Brave.Brave", Assert.Single(alreadyListed.Packages).Id);
    }

    [Fact]
    public void Tweaks_ExpandToRegistryValues_AndHkcuGoesToTheDefaultUserHive()
    {
        var plan = Planner.Plan(Profile(tweaks:
        [
            new("explorer.showFileExtensions"),
            new(Name: "Long paths", Registry: new(@"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", RegistryValueType.Dword, "1")),
            new("not.aTweak"),
        ]));

        Assert.Equal(
            [
                new RegistryWrite("Show file name extensions", RegistryTarget.DefaultUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", "dword", "0"),
                new RegistryWrite("Long paths", RegistryTarget.Machine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", "dword", "1"),
            ],
            plan.Registry);
        Assert.Contains(plan.Problems, problem => problem.Contains("not.aTweak", StringComparison.Ordinal));
    }

    [Fact]
    public void Policies_AreTypedByTheCatalog_AndOneForAnotherBrowserIsAProblem()
    {
        var plan = Planner.Plan(Profile(browser: new(new("edge"), null, new Dictionary<string, string>
        {
            ["homepage"] = "https://example.com",
            ["showHomeButton"] = "true",
            ["passwordManager"] = "false",
            ["bookmarkBar"] = "true",
        })));

        var policies = plan.Registry.ToDictionary(write => write.Name, write => (write.Target, write.Key, write.Type, write.Value));
        Assert.Equal((RegistryTarget.Machine, @"SOFTWARE\Policies\Microsoft\Edge", "string", "https://example.com"), policies["HomepageLocation"]);
        Assert.Equal((RegistryTarget.Machine, @"SOFTWARE\Policies\Microsoft\Edge", "dword", "1"), policies["ShowHomeButton"]);
        Assert.Equal("0", policies["PasswordManagerEnabled"].Value);
        Assert.Contains(plan.Problems, problem => problem.Contains("bookmarkBar", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("edge", @"SOFTWARE\Policies\Microsoft\Edge", true)]
    [InlineData("chrome", @"SOFTWARE\Policies\Google\Chrome", false)]
    [InlineData("brave", @"SOFTWARE\Policies\BraveSoftware\Brave", false)]
    public void Extensions_AreOneExtensionSettingsValue_InstalledButRemovableByTurningOff(string browser, string policyRoot, bool takesEdgeIds)
    {
        var plan = Planner.Plan(Profile(browser: new(new(browser), [new("eimadpbcbfnmbkopoojfekhnkhdbieeh", "Dark Reader"), new("edge:odfafepnkmbhccpbejgmiehpchacaeak", "uBlock Origin")], null)));

        var write = Assert.Single(plan.Registry);
        Assert.Equal((RegistryTarget.Machine, policyRoot, "ExtensionSettings", "string"), (write.Target, write.Key, write.Name, write.Type));
        var settings = JsonNode.Parse(write.Value)!.AsObject();
        Assert.Equal("normal_installed", (string?)settings["eimadpbcbfnmbkopoojfekhnkhdbieeh"]!["installation_mode"]);
        Assert.Equal("https://clients2.google.com/service/update2/crx", (string?)settings["eimadpbcbfnmbkopoojfekhnkhdbieeh"]!["update_url"]);
        if (takesEdgeIds)
        {
            Assert.Equal("https://edge.microsoft.com/extensionwebstorebase/v1/crx", (string?)settings["odfafepnkmbhccpbejgmiehpchacaeak"]!["update_url"]);
            Assert.Empty(plan.Problems);
        }
        else
        {
            Assert.Null(settings["odfafepnkmbhccpbejgmiehpchacaeak"]);
            Assert.Contains(plan.Problems, problem => problem.Contains("uBlock Origin", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Removals_PassThrough_AndCountAsItems()
    {
        var plan = Planner.Plan(Profile(
            remove: new([new("Microsoft.BingNews")], [new("Browser.InternetExplorer~~~~0.0.11.0")], [new("WorkFolders-Client")]),
            tweaks: [new("explorer.showFileExtensions")]));

        Assert.Equal("Microsoft.BingNews", Assert.Single(plan.Appx).Id);
        Assert.Single(plan.Capabilities);
        Assert.Single(plan.Features);
        Assert.Equal(4, plan.ItemCount);
    }
}
