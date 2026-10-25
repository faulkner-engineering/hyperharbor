using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class GuestAdminApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly string Route = $"/api/v1/vms/{VmId}/guest-admin";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public GuestAdminApiTests()
    {
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Running, address: "192.168.0.50"));
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task Open_WithoutElevation_IsRefused_AndAudited()
    {
        await ProvisionAsync();

        var response = await _client.PostAsync(Route, null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.ElevationRequired, await CodeAsync(response));
        Assert.Empty(_host.Guest.AdminEnabled);
        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "openGuestAdmin");
        Assert.Equal("failed", (string?)audited["outcome"]);
    }

    [Fact]
    public async Task OpenGetClose_RunsTheWindow_AndAuditsAccountAndExpiry()
    {
        await ProvisionAsync();
        var token = await ElevateAsync();

        var open = await SendAsync(HttpMethod.Post, token);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.True(open.Headers.CacheControl?.NoStore);
        var window = (await open.Content.ReadFromJsonAsync<JsonObject>())!;
        var passphrase = (string)window["passphrase"]!;
        var expiresAt = window["expiresAt"]!.GetValue<DateTimeOffset>();
        Assert.Equal("hha-owner", (string?)window["accountName"]);
        Assert.Equal(passphrase, _host.Guest.AdminEnabled.Single().Password);

        var again = (await (await SendAsync(HttpMethod.Post, token)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(passphrase, (string?)again["passphrase"]);
        Assert.Single(_host.Guest.AdminEnabled);

        var status = (await _client.GetFromJsonAsync<JsonObject>(Route))!;
        Assert.True((bool?)status["open"]);
        Assert.False((bool?)status["closePending"]);
        Assert.Equal(expiresAt, status["expiresAt"]!.GetValue<DateTimeOffset>());
        Assert.Null(status["passphrase"]);

        // Ending a window needs no elevation: it only takes access away.
        var close = await _client.DeleteAsync(Route);
        Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
        Assert.Single(_host.Guest.AdminDisabled);
        var closed = (await _client.GetFromJsonAsync<JsonObject>(Route))!;
        Assert.False((bool?)closed["open"]);
        Assert.Null((string?)closed["expiresAt"]);

        var opened = _host.AuditEntries().Last(entry => (string?)entry["action"] == "openGuestAdmin");
        Assert.Equal("succeeded", (string?)opened["outcome"]);
        Assert.True((bool?)opened["elevated"]);
        Assert.Equal(VmId.ToString(), (string?)opened["vmId"]);
        Assert.Equal("Laptop", (string?)opened["deviceName"]);
        Assert.Equal($"account=hha-owner, expiresAt={expiresAt:u}", (string?)opened["detail"]);
        var ended = _host.AuditEntries().Last(entry => (string?)entry["action"] == "closeGuestAdmin");
        Assert.Equal("succeeded", (string?)ended["outcome"]);
        Assert.Equal($"account=hha-owner, expiresAt={expiresAt:u}, reason=user", (string?)ended["detail"]);

        // The passphrase is in no audit entry and no log line, at any level.
        Assert.DoesNotContain(passphrase, File.ReadAllText(Path.Combine(_host.DataDirectory, FileAuditLog.FileName)), StringComparison.Ordinal);
        _host.Logs.AssertNoneContain(passphrase);
    }

    [Fact]
    public async Task Close_WhenNoneIsOpen_Returns404WithDetail()
    {
        await ProvisionAsync();

        var response = await _client.DeleteAsync(Route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["detail"]));
    }

    [Fact]
    public async Task Get_ForAnUnknownVm_Returns404()
    {
        var response = await _client.GetAsync($"/api/v1/vms/{Guid.NewGuid()}/guest-admin");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Open_OnAnUnprovisionedVm_Returns409VmNotProvisioned()
    {
        var response = await SendAsync(HttpMethod.Post, await ElevateAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.VmNotProvisioned, await CodeAsync(response));
    }

    [Fact]
    public async Task Open_OnAStoppedVm_Returns409VmNotRunning()
    {
        await ProvisionAsync();
        _host.Inventory.SetState(VmId, VmState.Off);

        var response = await SendAsync(HttpMethod.Post, await ElevateAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.VmNotRunning, await CodeAsync(response));
    }

    [Fact]
    public async Task Open_OnLinux_Returns409GuestAdminUnsupported()
    {
        _host.Inventory.Vms[0] = FakeVmInventory.CreateVm(VmId, "Ubuntu", VmState.Running, GuestOsFamily.Linux, "192.168.0.51");

        var response = await SendAsync(HttpMethod.Post, await ElevateAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.GuestAdminUnsupported, await CodeAsync(response));
    }

    [Fact]
    public async Task Open_WhenTheGuestIsUnreachable_Returns409GuestUnavailable()
    {
        await ProvisionAsync();
        _host.Guest.AdminFailure = new Core.Provisioning.GuestUnavailableException("PowerShell Direct could not connect to the guest.");

        var response = await SendAsync(HttpMethod.Post, await ElevateAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.GuestUnavailable, await CodeAsync(response));
    }

    [Fact]
    public async Task Open_WhenTheGuestEchoesSecrets_KeepsThemOutOfTheResponseLogsAndAudit()
    {
        await ProvisionAsync();
        _host.Guest.EchoSecretsInFailure = true;

        var response = await SendAsync(HttpMethod.Post, await ElevateAsync());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var secrets = _host.Guest.AdminEnabled.Select(enabled => enabled.Password)
            .Concat(_host.Guest.AdminDisabled.Select(disabled => disabled.Password))
            .Append("Adm1n!")
            .ToArray();
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, body, StringComparison.Ordinal));
        var audit = File.ReadAllText(Path.Combine(_host.DataDirectory, FileAuditLog.FileName));
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, audit, StringComparison.Ordinal));
        _host.Logs.AssertNoneContain(secrets);
    }

    [Fact]
    public async Task Settings_NeedElevation_AndAreRangeChecked()
    {
        var unelevated = await _client.PutAsJsonAsync("/api/v1/host/guest-admin/settings", new { windowMinutes = 5 });
        Assert.Equal(HttpStatusCode.Forbidden, unelevated.StatusCode);

        var token = await ElevateAsync();
        var tooLong = await PutSettingsAsync(token, 31);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var saved = await PutSettingsAsync(token, 5);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(5, (int?)(await saved.Content.ReadFromJsonAsync<JsonObject>())!["windowMinutes"]);
        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "setGuestAdminSettings");
        Assert.Equal("windowMinutes=5", (string?)audited["detail"]);

        await ProvisionAsync();
        var window = (await (await SendAsync(HttpMethod.Post, token)).Content.ReadFromJsonAsync<JsonObject>())!;
        var expiresAt = window["expiresAt"]!.GetValue<DateTimeOffset>();
        Assert.InRange(expiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    private async Task ProvisionAsync()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/provision", new { adminUserName = "hhadmin", adminPassword = "Adm1n!" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> ElevateAsync()
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        return (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string token)
    {
        var request = new HttpRequestMessage(method, Route);
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PutSettingsAsync(string token, int minutes)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/host/guest-admin/settings") { Content = JsonContent.Create(new { windowMinutes = minutes }) };
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return _client.SendAsync(request);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"];
}
