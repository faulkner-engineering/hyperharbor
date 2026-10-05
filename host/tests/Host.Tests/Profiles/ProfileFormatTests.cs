using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;
using Json.Schema;
using YamlDotNet.Serialization;

namespace HyperHarbor.Host.Tests.Profiles;

public sealed class ExtensionIdParserTests
{
    private const string UBlock = "cjpalhdlnbpafiamejdnhcphjbkeiagm";
    private const string UBlockEdge = "odfafepnkmbhccpbejgmiehpchacaeak";

    [Theory]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("https://chromewebstore.google.com/detail/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm?hl=en&pli=1")]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm/reviews")]
    [InlineData("https://chrome.google.com/webstore/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm/")]
    [InlineData("chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("  CJPALHDLNBPAFIAMEJDNHCPHJBKEIAGM  ")]
    [InlineData("cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    public void ChromeWebStoreUrlsAndIds_GiveTheChromeId(string input)
    {
        Assert.Equal(new ExtensionRef(ExtensionStore.Chrome, UBlock), ExtensionIdParser.Parse(input));
    }

    [Theory]
    [InlineData("https://microsoftedge.microsoft.com/addons/detail/ublock-origin/odfafepnkmbhccpbejgmiehpchacaeak")]
    [InlineData("https://microsoftedge.microsoft.com/addons/detail/odfafepnkmbhccpbejgmiehpchacaeak?hl=en-US")]
    [InlineData("edge:odfafepnkmbhccpbejgmiehpchacaeak")]
    public void EdgeAddonsUrls_GiveTheEdgeId(string input)
    {
        var parsed = ExtensionIdParser.Parse(input);

        Assert.Equal(new ExtensionRef(ExtensionStore.Edge, UBlockEdge), parsed);
        Assert.Equal("edge:" + UBlockEdge, parsed!.ProfileId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ublock origin")]
    [InlineData("cjpalhdlnbpafiamejdnhcphjbkeiag")]
    [InlineData("cjpalhdlnbpafiamejdnhcphjbkeiagz")]
    [InlineData("https://example.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("https://chromewebstore.google.com/category/extensions")]
    [InlineData("ftp://chromewebstore.google.com/detail/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("edge:not-an-id")]
    public void AnythingElse_GivesNothing(string? input)
    {
        Assert.Null(ExtensionIdParser.Parse(input));
    }
}

public sealed class PackageAliasResolverTests
{
    private readonly PackageAliasResolver _resolver = new(Catalogs.Default);

    [Theory]
    [InlineData("vscode", "Microsoft.VisualStudioCode", "Visual Studio Code")]
    [InlineData("7zip", "7zip.7zip", "7-Zip")]
    [InlineData("notepadplusplus", "Notepad++.Notepad++", "Notepad++")]
    public void Aliases_ResolveToTheirWingetIds(string alias, string id, string name)
    {
        Assert.Equal(new PackageRef(id, PackageSource.Winget, name, alias), _resolver.Resolve(alias));
    }

    [Fact]
    public void WingetIds_PassThrough_WithTheCatalogNameWhenKnown()
    {
        Assert.Equal(new PackageRef("Git.Git", PackageSource.Winget, "Git", null), _resolver.Resolve("Git.Git"));
        Assert.Equal(new PackageRef("Contoso.Unlisted.App", PackageSource.Winget, null, null), _resolver.Resolve(" Contoso.Unlisted.App "));
    }

    [Fact]
    public void StoreIds_PassThrough()
    {
        Assert.Equal(new PackageRef("9NBLGGH4NNS1", PackageSource.MicrosoftStore, null, null), _resolver.Resolve("9NBLGGH4NNS1"));
    }

    [Fact]
    public void AnUnknownAlias_IsRefused_WithTheAlias()
    {
        var error = Assert.Throws<UnknownAliasException>(() => _resolver.Resolve("not-a-package"));

        Assert.Equal("not-a-package", error.Alias);
    }

    [Theory]
    [InlineData("VSCode")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("-leading")]
    public void Other_Entries_AreNotPackages(string entry)
    {
        Assert.Throws<ArgumentException>(() => _resolver.Resolve(entry));
    }

    [Theory]
    [InlineData("Git.Git", InstallEntryKind.Winget)]
    [InlineData("Adobe.Acrobat.Reader.64-bit", InstallEntryKind.Winget)]
    [InlineData("9NBLGGH4NNS1", InstallEntryKind.MicrosoftStore)]
    [InlineData("git", InstallEntryKind.Alias)]
    [InlineData("Git", InstallEntryKind.Invalid)]
    public void Entries_AreClassifiedByTheirForm(string entry, InstallEntryKind kind)
    {
        Assert.Equal(kind, PackageAliasResolver.Classify(entry));
    }
}

public sealed class ProfileYamlTests
{
    private static readonly ProfileValidator Validator = new(Catalogs.Default);

    internal static SetupProfile Sample() => new(
        "Dev workstation",
        "Tools: Git, VS Code, and # signs",
        [new("Git.Git", "Git"), new("Microsoft.VisualStudioCode", "Visual Studio Code"), new("9NBLGGH4NNS1", "App Installer"), new("7zip", "7-Zip")],
        new ProfileRemove(
            [new("Microsoft.BingNews", "Microsoft News"), new("Microsoft.GamingApp", "Xbox")],
            [new("Browser.InternetExplorer~~~~0.0.11.0", "Internet Explorer mode")],
            [new("MicrosoftWindowsPowerShellV2Root", "Windows PowerShell 2.0")]),
        [
            new("explorer.showFileExtensions", "Show file name extensions"),
            new(Name: "Disable Bing in Start", Registry: new RegistryTweak(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", RegistryValueType.Dword, "1")),
            new(Name: "Classic menu", Registry: new RegistryTweak(@"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", RegistryValueType.String, "")),
        ],
        new ProfileBrowser(
            new("Brave.Brave", "Brave"),
            [new("cjpalhdlnbpafiamejdnhcphjbkeiagm", "uBlock Origin"), new("edge:odfafepnkmbhccpbejgmiehpchacaeak", "uBlock Origin for Edge")],
            new Dictionary<string, string> { ["homepage"] = "https://start.duckduckgo.com", ["passwordManager"] = "false", ["restoreOnStartup"] = "5" }));

    [Fact]
    public void AWrittenProfile_ReadsBackUnchanged_IncludingTheNamesInComments()
    {
        var profile = Validator.Normalize(Sample());

        var yaml = ProfileYamlWriter.Write(profile, Validator.PolicyTypes);
        var read = ProfileYamlReader.Read(yaml);

        Assert.Equal(Json(profile), Json(read));
        Assert.Equal(yaml, ProfileYamlWriter.Write(Validator.Normalize(read), Validator.PolicyTypes));
    }

    [Fact]
    public void TheFile_StartsWithTheSchema_AndEveryIdCarriesItsName()
    {
        var yaml = ProfileYamlWriter.Write(Validator.Normalize(Sample()), Validator.PolicyTypes);
        var lines = yaml.Split('\n');

        Assert.Equal($"# yaml-language-server: $schema={ProfileYamlWriter.SchemaUrl}", lines[0]);
        Assert.Contains(lines, line => line.StartsWith("  - Git.Git ", StringComparison.Ordinal) && line.EndsWith("# Git", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("    - cjpalhdlnbpafiamejdnhcphjbkeiagm", StringComparison.Ordinal) && line.EndsWith("# uBlock Origin", StringComparison.Ordinal));
        Assert.Contains("    passwordManager: false", lines);
        Assert.Contains("    restoreOnStartup: 5", lines);
        Assert.Contains("    homepage: 'https://start.duckduckgo.com'", lines);
        // Comments in one list line up.
        var install = lines.Where(line => line.StartsWith("  - ", StringComparison.Ordinal) && line.Contains('#')).Take(4).Select(line => line.IndexOf('#')).Distinct();
        Assert.Single(install);
    }

    [Fact]
    public void TheWrittenFile_IsValidAgainstTheSchema()
    {
        var yaml = ProfileYamlWriter.Write(Validator.Normalize(Sample()), Validator.PolicyTypes);

        var result = ProfileSchema.Value.Evaluate(ToJson(yaml), new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, string.Join("; ", result.Details?.Where(detail => detail.Errors is not null).SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}")) ?? []));
    }

    [Fact]
    public void TheSchema_RejectsWhatTheValidatorRejects()
    {
        const string yaml = "schemaVersion: 1\nname: Bad\ninstall:\n  - Not An Id\nunknown: 1\n";

        Assert.False(ProfileSchema.Value.Evaluate(ToJson(yaml)).IsValid);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("1.5")]
    [InlineData("a: b")]
    [InlineData("hash # inside")]
    [InlineData("it's")]
    [InlineData(" padded ")]
    [InlineData("- dash")]
    [InlineData("tab\there")]
    [InlineData(@"HKLM\SOFTWARE\Policies")]
    [InlineData("{braces}")]
    [InlineData("")]
    public void Scalars_ReadBackAsTheSameText(string value)
    {
        var profile = new SetupProfile(value.Length == 0 ? "x" : value, value, null, null, null, null);

        var read = ProfileYamlReader.Read(ProfileYamlWriter.Write(profile));

        Assert.Equal(value.Length == 0 ? null : value, read.Description);
    }

    [Theory]
    [InlineData("schemaVersion: 2\nname: x\n", "schemaVersion must be 1")]
    [InlineData("name: x\n", "schemaVersion must be 1")]
    [InlineData("schemaVersion: 1\n", "name is required")]
    [InlineData("schemaVersion: 1\nname: x\nextras: 1\n", "does not have \"extras\"")]
    [InlineData("schemaVersion: 1\nname: x\ninstall: Git.Git\n", "install must be a list")]
    [InlineData("schemaVersion: 1\nname: [x\n", "Line")]
    [InlineData("- just\n- a list\n", "mapping at the top")]
    public void NotAProfile_IsRefused_WithWhatIsWrong(string yaml, string expected)
    {
        var error = Assert.Throws<ProfileFormatException>(() => ProfileYamlReader.Read(yaml));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HandWrittenFiles_WithoutComments_GetNamesFromTheCatalogs()
    {
        const string yaml = "schemaVersion: 1\nname: Quick\ninstall:\n  - vscode\n  - Git.Git\nremove:\n  appx:\n    - Microsoft.BingNews\ntweaks:\n  - explorer.showFileExtensions\nbrowser:\n  app: brave\n  extensions:\n    - eimadpbcbfnmbkopoojfekhnkhdbieeh\n";

        var profile = Validator.Normalize(ProfileYamlReader.Read(yaml));

        Assert.Equal(["Visual Studio Code", "Git"], profile.Install!.Select(item => item.Name));
        Assert.Equal("Microsoft News", profile.Remove!.Appx![0].Name);
        Assert.Equal("Show file name extensions", profile.Tweaks![0].Name);
        Assert.Equal("Brave", profile.Browser!.App.Name);
        Assert.Equal("Dark Reader", profile.Browser.Extensions![0].Name);
    }

    [Fact]
    public void TheSchema_AndTheModel_HaveTheSameProperties()
    {
        var schema = JsonNode.Parse(Catalogs.ProfileSchema)!;

        Assert.Equal(["schemaVersion", .. Properties(typeof(SetupProfile))], Keys(schema["properties"]));
        Assert.Equal(Properties(typeof(ProfileRemove)), Keys(schema["properties"]!["remove"]!["properties"]));
        Assert.Equal(Properties(typeof(ProfileBrowser)), Keys(schema["properties"]!["browser"]!["properties"]));
        Assert.Equal(Properties(typeof(RegistryTweak)), Keys(schema["$defs"]!["registryValue"]!["properties"]));
        Assert.Equal(
            Enum.GetNames<RegistryValueType>().Select(name => name.ToLowerInvariant()),
            schema["$defs"]!["registryValue"]!["properties"]!["type"]!["enum"]!.AsArray().Select(value => (string)value!));
    }

    internal static readonly Lazy<JsonSchema> ProfileSchema = new(() => JsonSchema.FromText(Catalogs.ProfileSchema));

    /// <summary>YAML to JSON with plain scalars typed (numbers and booleans), as YAML tools see them.</summary>
    internal static JsonElement ToJson(string yaml)
    {
        var data = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object>(yaml);
        return JsonSerializer.SerializeToElement(data);
    }

    private static string Json(SetupProfile profile) => JsonSerializer.Serialize(profile, Shared.Contracts.ContractJson.Options);

    private static IEnumerable<string> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name));

    private static IEnumerable<string> Keys(JsonNode? node) => node!.AsObject().Select(pair => pair.Key);
}

public sealed class ProfileValidatorTests
{
    private readonly ProfileValidator _validator = new(Catalogs.Default);

    [Fact]
    public void Problems_AreReported_WithTheFieldTheyAreAbout()
    {
        var profile = new SetupProfile(
            "",
            null,
            [new("Git.Git"), new("unknown-alias"), new("Not valid")],
            new ProfileRemove([new("Bad Name!")], null, null),
            [new("explorer.notATweak"), new(Registry: new RegistryTweak(@"HKCR\Thing", "x", RegistryValueType.Dword, "-1"))],
            new ProfileBrowser(new("Mozilla.Firefox"), [new("nope")], new Dictionary<string, string> { ["passwordManager"] = "maybe", ["unknownPolicy"] = "1" }));

        var error = Assert.Throws<LifecycleValidationException>(() => _validator.Normalize(profile));

        Assert.Equal(
            ["name", "install[1]", "install[2]", "remove.appx[0]", "tweaks[0]", "tweaks[1].registry.key", "tweaks[1].registry.value", "browser.app", "browser.extensions[0]", "browser.policies.passwordManager", "browser.policies.unknownPolicy"],
            error.Errors.Select(issue => issue.Field));
    }

    [Fact]
    public void Duplicates_AreDropped_AndPoliciesNormalized()
    {
        var profile = new SetupProfile(
            " Name ",
            "  ",
            [new("Git.Git"), new("git.git"), new("vscode")],
            null,
            null,
            new ProfileBrowser(new("Microsoft.Edge"), null, new Dictionary<string, string> { ["passwordManager"] = "False", ["edgeShopping"] = "TRUE" }));

        var normalized = _validator.Normalize(profile);

        Assert.Equal("Name", normalized.Name);
        Assert.Null(normalized.Description);
        Assert.Equal(["Git.Git", "vscode"], normalized.Install!.Select(item => item.Id));
        Assert.Equal("false", normalized.Browser!.Policies!["passwordManager"]);
        Assert.Equal("true", normalized.Browser.Policies["edgeShopping"]);
    }

    [Fact]
    public void APolicyForAnotherBrowser_IsRefused()
    {
        var profile = new SetupProfile("x", null, null, null, null, new ProfileBrowser(new("Google.Chrome"), null, new Dictionary<string, string> { ["edgeShopping"] = "false" }));

        var error = Assert.Throws<LifecycleValidationException>(() => _validator.Normalize(profile));

        Assert.Contains("does not apply to Google Chrome", Assert.Single(error.Errors).Message, StringComparison.Ordinal);
    }
}

public sealed class PackageSearchParseTests
{
    [Fact]
    public void Results_AreRead_FromTheLastJsonLine()
    {
        var results = PwshPackageSearch.Parse("WARNING: noise\n{\"ok\":true,\"result\":[{\"id\":\"Git.Git\",\"name\":\"Git\",\"version\":\"2.55.0.5\",\"source\":\"winget\"},{\"id\":\"\"}]}\n");

        Assert.Equal([new Shared.Contracts.Profiles.PackageSearchResult("Git.Git", "Git", "2.55.0.5", "winget")], results);
    }

    [Fact]
    public void NoResults_IsAnEmptyList()
    {
        Assert.Empty(PwshPackageSearch.Parse("{\"ok\":true,\"result\":[]}"));
    }

    [Fact]
    public void AMissingModule_IsUnavailable_AndOtherFailuresAreFailures()
    {
        Assert.Throws<PackageSearchUnavailableException>(() => PwshPackageSearch.Parse("{\"ok\":false,\"stage\":\"module\",\"error\":\"not found\"}"));
        Assert.Contains("source unreachable", Assert.Throws<PackageSearchFailedException>(() => PwshPackageSearch.Parse("{\"ok\":false,\"stage\":\"search\",\"error\":\"source unreachable\"}")).Message, StringComparison.Ordinal);
        Assert.Throws<PackageSearchFailedException>(() => PwshPackageSearch.Parse("not json"));
    }

    /// <summary>Set HH_PACKAGE_SEARCH_LIVE=1 on a host with PowerShell 7 and Microsoft.WinGet.Client for all users.</summary>
    [EnvironmentFact("HH_PACKAGE_SEARCH_LIVE")]
    public async Task Live_SearchFindsGit()
    {
        var results = await new PwshPackageSearch(TimeProvider.System).SearchAsync("git", 5, CancellationToken.None);

        Assert.Contains(results, result => result.Id == "Git.Git");
    }
}
