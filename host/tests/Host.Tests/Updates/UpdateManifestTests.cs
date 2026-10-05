using System.Text;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Updates;

namespace HyperHarbor.Host.Tests.Updates;

public sealed class UpdateManifestTests
{
    private static readonly UpdateOptions Options = new();

    [Fact]
    public void Parse_ReadsTheManifest_AndIgnoresUnknownFields()
    {
        var manifest = Parse(Releases.Manifest(minimumUpdateFrom: "1.0.0"));

        Assert.Equal(SemanticVersion.Parse("1.2.0"), manifest.ParsedVersion);
        Assert.Equal("stable", manifest.Channel);
        Assert.Equal("1.0.0", manifest.MinimumUpdateFrom);
        Assert.Equal(Releases.PackageUrl, manifest.HostPackage.Url);
        Assert.Equal(Releases.Package.Length, manifest.HostPackage.Size);
    }

    [Theory]
    [InlineData("schema", "this version reads schema 1")]
    [InlineData("version", "is not a version number")]
    [InlineData("http", "is not an https URL")]
    [InlineData("host", "Updates may not come from example.com")]
    [InlineData("size0", "outside 1 to")]
    [InlineData("sizeHuge", "outside 1 to")]
    [InlineData("sha", "64 hexadecimal characters")]
    [InlineData("minimum", "minimumUpdateFrom")]
    [InlineData("json", "not valid")]
    public void Parse_RejectsManifestsThatBreakARule(string breaks, string message)
    {
        var json = breaks switch
        {
            "schema" => Releases.Manifest(schemaVersion: 2),
            "version" => Releases.Manifest(version: "1.2"),
            "http" => Releases.Manifest(url: "http://github.com/a.exe"),
            "host" => Releases.Manifest(url: "https://example.com/HyperHarbor-Host-1.2.0.exe"),
            "size0" => Releases.Manifest(size: 0),
            "sizeHuge" => Releases.Manifest(size: Options.MaxPackageBytes + 1),
            "sha" => Releases.Manifest(sha256: "abc"),
            "minimum" => Releases.Manifest(minimumUpdateFrom: "soon"),
            _ => "{ \"schemaVersion\": 1 ",
        };

        var error = Assert.Throws<UpdateRejectedException>(() => Parse(json));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RequiresExactlyOneHostPackage()
    {
        var none = Releases.Manifest().Replace("\"component\": \"host\"", "\"component\": \"client\"", StringComparison.Ordinal);
        Assert.Throws<UpdateRejectedException>(() => Parse(none));

        var package = """{ "component": "host", "arch": "x64", "url": "https://github.com/b.exe", "size": 5, "sha256": "{{sha}}" }""";
        var two = Releases.Manifest().Replace("\"packages\": [", "\"packages\": [ " + package.Replace("{{sha}}", new string('a', 64), StringComparison.Ordinal) + ",", StringComparison.Ordinal);
        Assert.Throws<UpdateRejectedException>(() => Parse(two));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", "UpToDate")]
    [InlineData("1.3.0", "1.2.0", "UpToDate")]
    [InlineData("1.1.0", "1.2.0", "Available")]
    [InlineData("1.2.0-beta.1", "1.2.0", "Available")]
    public void Policy_OffersOnlyNewerVersions(string current, string offered, string expected)
    {
        var decision = UpdatePolicy.Evaluate(Parse(Releases.Manifest(version: offered)), SemanticVersion.Parse(current), []);

        Assert.Equal(Enum.Parse<UpdateDecisionKind>(expected), decision.Kind);
    }

    [Fact]
    public void Policy_SkipsAVersionThatWasRolledBack()
    {
        var decision = UpdatePolicy.Evaluate(Parse(Releases.Manifest()), SemanticVersion.Parse("1.1.0"), [SemanticVersion.Parse("1.2.0")]);

        Assert.Equal(UpdateDecisionKind.Skipped, decision.Kind);
    }

    [Fact]
    public void Policy_AsksForAnIntermediateVersion_BelowMinimumUpdateFrom()
    {
        var decision = UpdatePolicy.Evaluate(Parse(Releases.Manifest(minimumUpdateFrom: "1.1.0")), SemanticVersion.Parse("1.0.5"), []);

        Assert.Equal(UpdateDecisionKind.NeedsIntermediateVersion, decision.Kind);
        Assert.Contains("Install 1.1.0 first", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WritesAManifestThatEveryHostAccepts()
    {
        var executable = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(executable, Releases.Package);

            var created = UpdateManifest.Create(
                executable, SemanticVersion.Parse("1.2.0"), "stable", "https://github.com/faulkner-engineering/hyperharbor/releases/",
                "1.0.0", 2, "1.9.0", new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
            var parsed = UpdateManifest.Parse(created.ToJson(), Options);
            var properties = System.Text.Json.Nodes.JsonNode.Parse(created.ToJson())!.AsObject().Select(property => property.Key).Order();
            Assert.Equal(
                ["apiVersion", "channel", "dataFormat", "minimumUpdateFrom", "notesUrl", "packages", "publishedAt", "schemaVersion", "verification", "version"],
                properties);

            Assert.Equal("1.2.0", parsed.Version);
            Assert.Equal("1.0.0", parsed.MinimumUpdateFrom);
            Assert.Equal(2, parsed.DataFormat);
            Assert.Equal("https://github.com/faulkner-engineering/hyperharbor/releases/tag/v1.2.0", parsed.NotesUrl);
            Assert.Equal(Releases.PackageUrl, parsed.HostPackage.Url);
            Assert.Equal(Releases.Package.Length, parsed.HostPackage.Size);
            Assert.Equal(Releases.Sha256(Releases.Package), parsed.HostPackage.Sha256);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    private static UpdateManifest Parse(string json) => UpdateManifest.Parse(Encoding.UTF8.GetBytes(json), Options);
}
