using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class ProfileCaptureApiTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("6a7b8c9d-0e1f-4a2b-8c3d-4e5f6a7b8c9d");
    private const string AdminPassword = "Admin-Pass1!";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public ProfileCaptureApiTests()
    {
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Running));
        _host.Services.GetRequiredService<VmCredentialStore>().Save(VmId, new GuestCredential("hhadmin", AdminPassword));
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    private async Task<JsonObject> CaptureAsync()
    {
        var response = await _client.PostAsync($"/api/v1/vms/{VmId}/profile-capture", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task TheDraft_ListsWhatToInstall_RemoveAndKeep_WithSensibleTicks()
    {
        // The VM started with BingNews and Clipchamp, and the user removed them.
        await _client.PostAsync($"/api/v1/vms/{VmId}/appx-baseline", null);
        _host.ProfileReader.Appx = ["Microsoft.WindowsStore", "Microsoft.GamingApp"];

        var draft = await CaptureAsync();

        var install = draft["install"]!.AsArray();
        Assert.True((bool)Item(install, "Git.Git")["selected"]!);
        Assert.Equal("Visual Studio Code", (string?)Item(install, "Microsoft.VisualStudioCode")["name"]);
        Assert.False((bool)Item(install, "Microsoft.Edge")["selected"]!);
        Assert.False((bool)Item(install, "Microsoft.VCRedist.2015+.x64")["selected"]!);

        Assert.Equal(["Clipchamp.Clipchamp", "Microsoft.BingNews"], draft["removeAppx"]!.AsArray().Select(item => (string)item!["id"]!));
        Assert.Equal("Microsoft News", (string?)Item(draft["removeAppx"]!.AsArray(), "Microsoft.BingNews")["name"]);

        // HideFileExt=0 is the tweak; dark mode only has one of its two values set, so it is not.
        Assert.Equal(["explorer.showFileExtensions"], draft["tweaks"]!.AsArray().Select(item => (string)item!["id"]!));

        var browsers = draft["browsers"]!.AsArray();
        Assert.Equal(("Brave.Brave", true), ((string?)browsers[0]!["app"], (bool)browsers[0]!["selected"]!));
        Assert.Equal(["Dark Reader", "Google Docs Offline"], browsers[0]!["extensions"]!.AsArray().Select(item => (string)item!["name"]!));
        // Browsers install Google Docs Offline by themselves, so it starts unticked.
        Assert.False((bool)browsers[0]!["extensions"]![1]!["selected"]!);
        Assert.Equal(("Microsoft.Edge", false), ((string?)browsers[1]!["app"], (bool)browsers[1]!["selected"]!));
        Assert.Equal("edge:odfafepnkmbhccpbejgmiehpchacaeak", (string?)browsers[1]!["extensions"]![0]!["id"]);

        Assert.Empty(draft["otherPrograms"]!.AsArray());
        Assert.Empty(draft["warnings"]!.AsArray());
        Assert.Equal("hh-owner", Assert.Single(_host.ProfileReader.AccountsRead));
        Assert.Contains(_host.AuditEntries(), entry => (string?)entry["action"] == "captureSetupProfile");
    }

    [Fact]
    public async Task WithoutWingetOrABaselineOrAProfile_TheDraftSaysWhatIsMissing()
    {
        _host.ProfileReader.Winget = null;
        _host.ProfileReader.ProfileFound = false;

        var draft = await CaptureAsync();

        Assert.Empty(draft["install"]!.AsArray());
        Assert.Empty(draft["removeAppx"]!.AsArray());
        Assert.Null(draft["baseline"]);
        Assert.Equal(["Contoso Tool", "Git", "Microsoft Visual Studio Code (User)"], draft["otherPrograms"]!.AsArray().Select(item => (string)item!));
        var warnings = draft["warnings"]!.AsArray().Select(item => (string)item!).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, warning => warning.StartsWith("winget could not run", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.StartsWith("There is no clean Appx baseline", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("has not signed in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyWingetExport_ListsTheInstalledPrograms_WithAWarning()
    {
        _host.ProfileReader.Winget = [];

        var draft = await CaptureAsync();

        Assert.Equal(3, draft["otherPrograms"]!.AsArray().Count);
        Assert.Contains(draft["warnings"]!.AsArray(), warning => ((string)warning!).StartsWith("winget export found nothing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANeedsTheVmRunning_AndGuestErrorsHideThePassword()
    {
        _host.ProfileReader.Failure = new GuestOperationException($"failed for hhadmin / {AdminPassword}");
        var failed = await _client.PostAsync($"/api/v1/vms/{VmId}/profile-capture", null);
        _host.Inventory.Vms[0] = _host.Inventory.Vms[0] with { State = VmState.Off };
        var off = await _client.PostAsync($"/api/v1/vms/{VmId}/profile-capture", null);

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.DoesNotContain(AdminPassword, await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(ContractInfo.ProblemCodes.VmNotRunning, (string?)(await off.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
        _host.Logs.AssertNoneContain(AdminPassword);
    }

    private static JsonNode Item(JsonArray items, string id) => items.Single(item => (string?)item!["id"] == id)!;
}

public sealed class GuestProfileReaderParseTests
{
    [Fact]
    public void Installed_ReadsWingetIds_OrItsError_AndPrograms()
    {
        var ok = PowerShellDirectProfileReader.ParseInstalled(System.Text.Json.Nodes.JsonNode.Parse("""{"winget":["Git.Git","git.git",""],"wingetError":null,"programs":["Zeta","Alpha","Alpha"]}"""));
        var single = PowerShellDirectProfileReader.ParseInstalled(System.Text.Json.Nodes.JsonNode.Parse("""{"winget":"Git.Git","programs":"Only"}"""));
        var failed = PowerShellDirectProfileReader.ParseInstalled(System.Text.Json.Nodes.JsonNode.Parse("""{"winget":null,"wingetError":"no source","programs":[]}"""));

        Assert.Equal(["Git.Git"], ok.WingetPackages);
        Assert.Equal(["Alpha", "Zeta"], ok.Programs);
        Assert.Equal(["Git.Git"], single.WingetPackages);
        Assert.Equal(["Only"], single.Programs);
        Assert.Equal((null, "no source"), (failed.WingetPackages, failed.WingetError));
    }

    [Fact]
    public void Browsers_KeepStoreExtensions_WithEdgeIdsPrefixed_AndValuesByKeyAndName()
    {
        var found = PowerShellDirectProfileReader.ParseBrowsers(System.Text.Json.Nodes.JsonNode.Parse("""
            {"profileFound":true,
             "extensions":[
               {"browser":"chrome","id":"eimadpbcbfnmbkopoojfekhnkhdbieeh","store":"chrome","name":"Dark Reader"},
               {"browser":"chrome","id":"eimadpbcbfnmbkopoojfekhnkhdbieeh","store":"chrome","name":"Dark Reader"},
               {"browser":"edge","id":"odfafepnkmbhccpbejgmiehpchacaeak","store":"edge","name":""},
               {"browser":"chrome","id":"not-an-id","store":"chrome","name":"x"}],
             "values":[{"key":"HKCU\\X","name":"A","value":"0"},{"key":"HKLM\\Y","name":"B","value":null}]}
            """));

        Assert.True(found.ProfileFound);
        Assert.Equal(
            [new GuestExtension("chrome", "eimadpbcbfnmbkopoojfekhnkhdbieeh", "Dark Reader"), new GuestExtension("edge", "edge:odfafepnkmbhccpbejgmiehpchacaeak", "odfafepnkmbhccpbejgmiehpchacaeak")],
            found.Extensions);
        Assert.Equal("0", found.Values[@"HKCU\X|A"]);
        Assert.Null(found.Values[@"HKLM\Y|B"]);
    }
}
