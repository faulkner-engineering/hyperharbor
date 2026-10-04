using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http;

namespace HyperHarbor.Host.Tests.Api;

public sealed class ConsoleApiTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid OtherVmId = Guid.Parse("1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f");

    private readonly TestHost _host = new();
    private readonly HttpClient _client;

    public ConsoleApiTests()
    {
        using var certificate = TestHost.CreateClientCertificate();
        _host.Pair(certificate, "Living Room Laptop");
        _client = _host.CreateClient(certificate);
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Windows 11 Dev", VmState.Running));
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(OtherVmId, "Ubuntu", VmState.Running));
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    [Theory]
    [InlineData("/console")]
    [InlineData("/console/tunnel")]
    public async Task Endpoints_RequirePairing(string suffix)
    {
        using var anonymous = _host.CreateClient();

        var response = await anonymous.PostAsync($"/api/v1/vms/{VmId}{suffix}", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OpenConsole_GrantsAccessRotatesAndReturnsASessionWithoutCaching()
    {
        var account = _host.SetUpConsoleAccount();

        var response = await _client.PostAsync($"/api/v1/vms/{VmId}/console", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var trustee = $@"{Environment.MachineName}\{account.AccountName}";
        Assert.Equal(trustee, (string?)body["userName"]);
        Assert.Equal(VmId.ToString("D"), (string?)body["pcb"]);
        Assert.Equal(_host.ConsolePasswords.Passwords[account.AccountName], (string?)body["password"]);
        Assert.NotEqual(account.Password, (string?)body["password"]);
        Assert.False(string.IsNullOrEmpty((string?)body["ticket"]));
        Assert.Equal([(VmId, trustee)], _host.ConsoleAccess.Grants);
    }

    [Fact]
    public async Task OpenConsole_VmOff_Returns409VmNotRunning()
    {
        _host.SetUpConsoleAccount();
        _host.Inventory.Vms[0] = _host.Inventory.Vms[0] with { State = VmState.Off };

        var response = await _client.PostAsync($"/api/v1/vms/{VmId}/console", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, ContractInfo.ProblemCodes.VmNotRunning);
        Assert.Empty(_host.ConsoleAccess.Grants);
    }

    [Fact]
    public async Task OpenConsole_NoConsoleAccount_Returns409SetupRequired()
    {
        var response = await _client.PostAsync($"/api/v1/vms/{VmId}/console", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, ContractInfo.ProblemCodes.ConsoleSetupRequired);
    }

    [Fact]
    public async Task OpenConsole_UnknownVm_Returns404()
    {
        _host.SetUpConsoleAccount();

        var response = await _client.PostAsync($"/api/v1/vms/{Guid.NewGuid()}/console", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Tunnel_WithoutTicket_Returns403()
    {
        var response = await _client.PostAsync($"/api/v1/vms/{VmId}/console/tunnel", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Tunnel_WithAnotherVmsTicket_Returns403()
    {
        _host.SetUpConsoleAccount();
        var ticket = await OpenTicketAsync(OtherVmId);

        var response = await TunnelAsync(VmId, ticket);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>TestServer cannot upgrade connections, so a valid ticket gets as far as the upgrade check.</summary>
    [Fact]
    public async Task Tunnel_WithValidTicketButNoUpgrade_Returns426()
    {
        _host.SetUpConsoleAccount();
        var ticket = await OpenTicketAsync(VmId);

        var response = await TunnelAsync(VmId, ticket);

        Assert.Equal((HttpStatusCode)StatusCodes.Status426UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task ConsoleRequests_AreAuditedWithoutSecrets()
    {
        _host.SetUpConsoleAccount();
        var open = await _client.PostAsync($"/api/v1/vms/{VmId}/console", null);
        var body = (await open.Content.ReadFromJsonAsync<JsonObject>())!;
        await TunnelAsync(VmId, (string)body["ticket"]!);

        var entries = _host.AuditEntries();
        var opened = entries.Last(entry => (string?)entry["action"] == "openVmConsole");
        Assert.Equal("succeeded", (string?)opened["outcome"]);
        Assert.Equal("Windows 11 Dev", (string?)opened["vmName"]);
        Assert.Contains(entries, entry => (string?)entry["action"] == "openVmConsoleTunnel");

        string[] secrets = [(string)body["password"]!, (string)body["ticket"]!, "Console-Initial1!"];
        _host.Logs.AssertNoneContain(secrets);
        var auditText = File.ReadAllText(Path.Combine(_host.DataDirectory, Core.Audit.FileAuditLog.FileName));
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, auditText, StringComparison.Ordinal));
    }

    [Fact]
    public void ConsoleSession_ToString_HidesSecrets()
    {
        var session = new ConsoleSession("ticket-secret", @"PC\hhc-owner", "Password-Secret1!", VmId.ToString("D"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.DoesNotContain("ticket-secret", session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Password-Secret1!", session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Password-Secret1!", new ConsoleCredential("hhc-owner", "Password-Secret1!").ToString(), StringComparison.Ordinal);
    }

    private async Task<string> OpenTicketAsync(Guid vmId)
    {
        var response = await _client.PostAsync($"/api/v1/vms/{vmId}/console", null);
        response.EnsureSuccessStatusCode();
        return (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["ticket"]!;
    }

    private Task<HttpResponseMessage> TunnelAsync(Guid vmId, string ticket)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/vms/{vmId}/console/tunnel");
        request.Headers.Add(ContractInfo.ConsoleTicketHeader, ticket);
        request.Headers.Connection.Add("Upgrade");
        request.Headers.Upgrade.ParseAdd(ContractInfo.ConsoleUpgradeProtocol);
        return _client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(code, (string?)problem["code"]);
    }
}
