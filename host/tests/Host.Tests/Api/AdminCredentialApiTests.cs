using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

/// <summary>
/// The stored administrator credential over the API: connecting when the guest rejects it, the VM list's warning, and
/// checking and replacing it.
/// </summary>
public sealed class AdminCredentialApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private const string AdminPassword = "Adm1n-Secret!";
    private const string NewPassword = "Typed-Secret2!";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly string Route = $"/api/v1/vms/{VmId}/admin-credential";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public AdminCredentialApiTests()
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
    public async Task Connect_WhenTheGuestRejectsTheAdministrator_StillSignsTheUserIn_AndTheListWarns()
    {
        await ProvisionAsync();
        var first = await ConnectAsync();
        Assert.True((bool?)first["passwordRotated"]);
        Assert.Null((await VmAsync())["adminCredentialAlert"]);

        // The administrator's password expired in the guest, and the next connect comes after the reuse window.
        _host.Guest.RejectedAdminPasswords.Add(AdminPassword);
        ForgetReuseWindow();

        var degraded = await ConnectAsync();

        Assert.False((bool?)degraded["passwordRotated"]);
        Assert.Equal((string?)first["password"], (string?)degraded["password"]);
        Assert.Equal("rejected", (string?)(await VmAsync())["adminCredentialAlert"]!["kind"]);
        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "connectVm");
        Assert.Equal("succeeded", (string?)audited["outcome"]);
        Assert.Equal("passwordRotated=False", (string?)audited["detail"]);
        AssertNoSecretLeaked(AdminPassword, (string)first["password"]!);
    }

    [Fact]
    public async Task Connect_WithARejectedAdministrator_AndNoEarlierPassword_Is422WithACodeAndWhereToFixIt()
    {
        // As for a VM moved here from another host: its accounts come along, the password of the User's account does not.
        await ProvisionAsync();
        _host.Services.GetRequiredService<VmAccountPasswordStore>().RemoveVm(VmId);
        _host.Guest.RejectedAdminPasswords.Add(AdminPassword);

        var response = await _client.PostAsync($"/api/v1/vms/{VmId}/connect", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(ContractInfo.ProblemCodes.AdminCredentialRejected, (string?)problem["code"]);
        Assert.Contains("Dev Box", (string?)problem["detail"], StringComparison.Ordinal);
        Assert.Contains("Administrator account…", (string?)problem["detail"], StringComparison.Ordinal);
        AssertNoSecretLeaked(AdminPassword);
    }

    [Fact]
    public async Task Get_ReportsTheStoredAccount_WithoutContactingTheGuestOrReturningThePassword()
    {
        var none = (await _client.GetFromJsonAsync<JsonObject>(Route))!;
        Assert.Null((string?)none["userName"]);
        Assert.Equal("unchecked", (string?)none["state"]);

        await ProvisionAsync();
        var calls = _host.Guest.TargetsUsed.Count;
        var body = await _client.GetStringAsync(Route);

        var stored = JsonNode.Parse(body)!;
        Assert.Equal("hhadmin", (string?)stored["userName"]);
        Assert.False((bool?)stored["managed"]);
        Assert.Equal("working", (string?)stored["state"]);
        Assert.DoesNotContain(AdminPassword, body, StringComparison.Ordinal);
        Assert.Equal(calls, _host.Guest.TargetsUsed.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/vms/{Guid.NewGuid()}/admin-credential")).StatusCode);
    }

    [Fact]
    public async Task CheckAndSet_NeedElevation()
    {
        await ProvisionAsync();

        var check = await _client.PostAsJsonAsync($"{Route}/check", new { });
        var set = await _client.PutAsJsonAsync(Route, new { userName = "hhadmin", password = NewPassword });

        Assert.Equal(HttpStatusCode.Forbidden, check.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, set.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.ElevationRequired, (string?)(await set.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
        Assert.Empty(_host.Guest.AdministratorChecks);
        Assert.Equal(AdminPassword, _host.Services.GetRequiredService<VmCredentialStore>().Find(VmId)!.Password);
    }

    [Fact]
    public async Task Check_RecordsTheExpiryDate_WarnsInTheList_AndTurnsExpiryOffOnRequest()
    {
        await ProvisionAsync();
        var token = await ElevateAsync();
        var expires = DateTimeOffset.UtcNow.AddDays(6);
        _host.Guest.Administrator = new GuestAdministrator(true, expires);

        var checkedOnly = await SendAsync(HttpMethod.Post, $"{Route}/check", token, new { });

        Assert.Equal(HttpStatusCode.OK, checkedOnly.StatusCode);
        var status = (await checkedOnly.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("working", (string?)status["state"]);
        Assert.Equal(expires, status["passwordExpiresAt"]!.GetValue<DateTimeOffset>());
        var alert = (await VmAsync())["adminCredentialAlert"]!;
        Assert.Equal("expiring", (string?)alert["kind"]);
        Assert.Equal(expires, alert["passwordExpiresAt"]!.GetValue<DateTimeOffset>());

        var kept = await SendAsync(HttpMethod.Post, $"{Route}/check", token, new { passwordNeverExpires = true });

        Assert.Null((string?)(await kept.Content.ReadFromJsonAsync<JsonObject>())!["passwordExpiresAt"]);
        Assert.Null((await VmAsync())["adminCredentialAlert"]);
        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "checkVmAdminCredential");
        Assert.Equal("passwordNeverExpires=True, state=Working", (string?)audited["detail"]);
        Assert.True((bool?)audited["elevated"]);
    }

    [Fact]
    public async Task Check_OfARejectedCredential_Answers200WithTheState()
    {
        await ProvisionAsync();
        _host.Guest.RejectedAdminPasswords.Add(AdminPassword);

        var response = await SendAsync(HttpMethod.Post, $"{Route}/check", await ElevateAsync(), new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("rejected", (string?)status["state"]);
        Assert.False(string.IsNullOrEmpty((string?)status["detail"]));
        AssertNoSecretLeaked(AdminPassword);
    }

    [Fact]
    public async Task Set_StoresAWorkingCredential_ClearsTheWarning_AndTheNextConnectRotatesAgain()
    {
        await ProvisionAsync();
        var first = await ConnectAsync();
        _host.Guest.RejectedAdminPasswords.Add(AdminPassword);
        ForgetReuseWindow();
        Assert.False((bool?)(await ConnectAsync())["passwordRotated"]);

        var set = await SendAsync(HttpMethod.Put, Route, await ElevateAsync(), new { userName = "hhadmin", password = NewPassword });

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("working", (string?)(await set.Content.ReadFromJsonAsync<JsonObject>())!["state"]);
        Assert.Equal(NewPassword, _host.Services.GetRequiredService<VmCredentialStore>().Find(VmId)!.Password);
        Assert.Null((await VmAsync())["adminCredentialAlert"]);

        // The User's account was not reset: the next connect changes its password from the one it still has.
        _host.Guest.AcceptedCurrentPasswords.Add((string)first["password"]!);
        var after = await ConnectAsync();
        Assert.True((bool?)after["passwordRotated"]);
        Assert.Equal((string?)first["password"], _host.Guest.PasswordsChanged.Last().Current);

        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "setVmAdminCredential");
        Assert.Equal("userName=hhadmin, passwordNeverExpires=False", (string?)audited["detail"]);
        AssertNoSecretLeaked(AdminPassword, NewPassword);
    }

    [Fact]
    public async Task Set_WithACredentialTheGuestRejects_Is422_AndKeepsTheStoredOne()
    {
        await ProvisionAsync();
        _host.Guest.RejectedAdminPasswords.Add(NewPassword);

        var response = await SendAsync(HttpMethod.Put, Route, await ElevateAsync(), new { userName = "hhadmin", password = NewPassword });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.AdminCredentialRejected, (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
        Assert.Equal(AdminPassword, _host.Services.GetRequiredService<VmCredentialStore>().Find(VmId)!.Password);
        AssertNoSecretLeaked(AdminPassword, NewPassword);
    }

    [Fact]
    public async Task Set_ValidatesItsBody_AndNeedsARunningVm()
    {
        var token = await ElevateAsync();

        var missing = await SendAsync(HttpMethod.Put, Route, token, new { userName = "hhadmin" });
        var empty = await SendAsync(HttpMethod.Put, Route, token, new { userName = " ", password = NewPassword });
        _host.Inventory.SetState(VmId, VmState.Off);
        var stopped = await SendAsync(HttpMethod.Put, Route, token, new { userName = "hhadmin", password = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.VmNotRunning, (string?)(await stopped.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
        Assert.Empty(_host.Guest.AdministratorChecks);
    }

    /// <summary>As when the reuse window has ended: the rotator drops the password it kept for it and rotates on the next connect.</summary>
    private void ForgetReuseWindow() => _host.Services.GetRequiredService<PasswordRotator>().Dispose();

    private void AssertNoSecretLeaked(params string[] secrets)
    {
        _host.Logs.AssertNoneContain(secrets);
        var audit = File.ReadAllText(Path.Combine(_host.DataDirectory, FileAuditLog.FileName));
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, audit, StringComparison.Ordinal));
    }

    private async Task<JsonNode> VmAsync() => (await _client.GetFromJsonAsync<JsonArray>("/api/v1/vms"))!.Single()!;

    private async Task<JsonObject> ConnectAsync()
    {
        var response = await _client.PostAsync($"/api/v1/vms/{VmId}/connect", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private async Task ProvisionAsync()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/provision", new { adminUserName = "hhadmin", adminPassword = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> ElevateAsync()
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        return (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, string token, object body)
    {
        var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return _client.SendAsync(request);
    }
}
