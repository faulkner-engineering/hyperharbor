using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Profiles;

namespace HyperHarbor.Host.Tests.Profiles;

/// <summary>The curated catalogs in the repository's catalogs folder load and hold well-formed entries.</summary>
public sealed class CatalogTests
{
    private static readonly Catalogs Catalogs = Catalogs.Default;

    [Fact]
    public void Packages_HaveUniqueAliases_WingetIds_AndAPopularList()
    {
        Assert.All(Catalogs.Packages, entry =>
        {
            Assert.Equal(InstallEntryKind.Alias, PackageAliasResolver.Classify(entry.Alias));
            Assert.Equal(InstallEntryKind.Winget, PackageAliasResolver.Classify(entry.Id));
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.Contains(entry.Category, (string[])["browsers", "development", "utilities", "media", "communication", "productivity", "security", "gaming"]);
        });
        Assert.Equal(Catalogs.Packages.Count, Catalogs.Packages.Select(entry => entry.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(Catalogs.Packages.Count(entry => entry.Popular) >= 10);
    }

    [Fact]
    public void EveryAlias_Resolves()
    {
        var resolver = new PackageAliasResolver(Catalogs);

        Assert.All(Catalogs.Packages, entry => Assert.Equal(entry.Id, resolver.Resolve(entry.Alias).Id));
    }

    [Fact]
    public void Extensions_HaveStoreIds_AndUniqueEntries()
    {
        var id = new Regex("^[a-p]{32}$");
        Assert.All(Catalogs.Extensions, entry =>
        {
            Assert.Matches(id, entry.Id);
            if (entry.EdgeId is not null)
            {
                Assert.Matches(id, entry.EdgeId);
            }

            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.Contains(entry.Category, (string[])["privacy", "security", "productivity", "developer", "accessibility", "media"]);
        });
        Assert.Equal(Catalogs.Extensions.Count, Catalogs.Extensions.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public void Appx_EntriesAreRated_AndTheStoreAndWingetAreKept()
    {
        Assert.All(Catalogs.Appx, entry =>
        {
            Assert.NotEqual(AppxRating.Unrated, entry.Rating);
            Assert.False(string.IsNullOrWhiteSpace(entry.FriendlyName));
            Assert.False(string.IsNullOrWhiteSpace(entry.Publisher));
        });
        Assert.Equal(Catalogs.Appx.Count, Catalogs.Appx.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(AppxRating.Keep, Catalogs.AppxIndex["Microsoft.WindowsStore"].Rating);
        Assert.Equal(AppxRating.Keep, Catalogs.AppxIndex["Microsoft.DesktopAppInstaller"].Rating);
        Assert.Contains(Catalogs.Appx, entry => entry.Rating == AppxRating.Safe);
    }

    [Fact]
    public void Tweaks_HaveValidRegistryValues()
    {
        var key = new Regex(@"^(HKLM|HKCU)\\.+$");
        Assert.All(Catalogs.Tweaks, tweak =>
        {
            Assert.Matches("^[a-z]+\\.[A-Za-z0-9]+$", tweak.Id);
            Assert.StartsWith(tweak.Category + ".", tweak.Id, StringComparison.Ordinal);
            Assert.NotEmpty(tweak.Values);
            Assert.All(tweak.Values, value =>
            {
                Assert.Matches(key, value.Key);
                Assert.Contains(value.Type, (string[])["dword", "qword", "string"]);
                if (value.Type != "string")
                {
                    Assert.True(ulong.TryParse(value.Value, out _), $"{tweak.Id}: {value.Value}");
                }

                Assert.NotEqual(value.Value, value.Default);
            });
        });
        Assert.Equal(Catalogs.Tweaks.Count, Catalogs.Tweaks.Select(tweak => tweak.Id).Distinct().Count());
    }

    [Fact]
    public void Browsers_AndPolicies_AreConsistent()
    {
        var browsers = Catalogs.Browsers.Select(browser => browser.Id).ToHashSet();
        Assert.Equal(["chrome", "edge", "brave"], browsers);
        Assert.All(Catalogs.Browsers, browser =>
        {
            Assert.Equal(browser.WingetId, Catalogs.AliasIndex[browser.Alias].Id);
            Assert.StartsWith(@"HKLM\SOFTWARE\Policies\", browser.PolicyRoot, StringComparison.Ordinal);
        });
        Assert.All(Catalogs.Policies, policy =>
        {
            Assert.Matches("^[a-z][A-Za-z0-9]{0,63}$", policy.Key);
            Assert.Contains(policy.Type, (string[])["boolean", "integer", "string"]);
            Assert.NotEmpty(policy.AppliesTo);
            Assert.All(policy.AppliesTo, id => Assert.Contains(id, browsers));
            Assert.True(policy.Values is null || policy.Type == "integer");
        });
        Assert.Equal(Catalogs.Policies.Count, Catalogs.Policies.Select(policy => policy.Key).Distinct().Count());
    }

    [Fact]
    public void TheProfileSchema_IsEmbedded()
    {
        Assert.Contains("\"$id\": \"" + ProfileYamlWriter.SchemaUrl + "\"", Catalogs.ProfileSchema, StringComparison.Ordinal);
    }
}
