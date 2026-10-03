using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Pairing;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

/// <summary>
/// Runs the flows that handle secrets with every log level captured, and checks that no PIN or
/// password reaches a log entry or an error response.
/// </summary>
public sealed class SecretLeakTests : IDisposable
{
    private const string AdminPassword = "Adm1n-Secret!";
    private static readonly Guid VmId = Guid.Parse("4f7d1c2b-8e3a-4b5c-9d6e-1a2b3c4d5e6f");

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();

    public SecretLeakTests()
    {
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Windows 11 Dev", VmState.Running) with { IpAddresses = ["192.168.0.50"] });
    }

    public void Dispose()
    {
        _certificate.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task Pairing_DoesNotLogPin()
    {
        using var client = _host.CreateClient(_certificate);
        var created = await CreateAsync(client);
        var pin = _host.Tray.Pin!;
        var wrongPin = pin == "000000" ? "000001" : "000000";

        var wrong = TestPairingClient.Compute(created.PairingId, wrongPin, created.HostShare, _certificate, HostCertificateDer());
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConfirmAsync(client, created.PairingId, wrong.Confirmation)).StatusCode);
        var right = TestPairingClient.Compute(created.PairingId, pin, created.HostShare, _certificate, HostCertificateDer());
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, created.PairingId, right.Confirmation)).StatusCode);

        Assert.NotEmpty(_host.Logs.Entries);
        _host.Logs.AssertNoneContain(pin, wrongPin);
    }

    [Fact]
    public async Task ProvisionAndConnect_DoNotLogPasswords()
    {
        using var client = PairedClient();

        Assert.Equal(HttpStatusCode.OK, (await ProvisionAsync(client)).StatusCode);
        var connect = await client.PostAsync($"/api/v1/vms/{VmId}/connect", null);
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
        var password = (string?)(await connect.Content.ReadFromJsonAsync<JsonObject>())!["password"];

        Assert.NotNull(password);
        _host.Logs.AssertNoneContain([AdminPassword, password!, .. _host.Guest.PasswordsSet.Select(set => set.Password)]);
    }

    [Fact]
    public async Task ProvisionFailure_GuestOutputWithSecrets_IsRedacted()
    {
        using var client = PairedClient();
        _host.Guest.EchoSecretsInFailure = true;

        var response = await ProvisionAsync(client);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var detail = (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["detail"];
        AssertClean(detail);
    }

    [Fact]
    public async Task ConnectFailure_GuestOutputWithSecrets_IsRedacted()
    {
        using var client = PairedClient();
        Assert.Equal(HttpStatusCode.OK, (await ProvisionAsync(client)).StatusCode);
        _host.Guest.EchoSecretsInFailure = true;

        var response = await client.PostAsync($"/api/v1/vms/{VmId}/connect", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var detail = (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["detail"];
        AssertClean(detail);
    }

    private void AssertClean(string? detail)
    {
        Assert.NotNull(detail);
        Assert.Contains("[redacted]", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(detail!, char.IsControl);
        string[] secrets = [AdminPassword, .. _host.Guest.PasswordsSet.Select(set => set.Password)];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, detail, StringComparison.Ordinal);
        }

        _host.Logs.AssertNoneContain(secrets);
    }

    private HttpClient PairedClient()
    {
        _host.Pair(_certificate);
        return _host.CreateClient(_certificate);
    }

    private Task<HttpResponseMessage> ProvisionAsync(HttpClient client) =>
        client.PostAsJsonAsync($"/api/v1/vms/{VmId}/provision", new { adminUserName = "Administrator", adminPassword = AdminPassword });

    private static async Task<PairingRequestCreated> CreateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/pairing/requests", new PairingRequest("Laptop", ClientPem(client)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PairingRequestCreated>(ContractJson.Options))!;
    }

    private static string ClientPem(HttpClient client)
    {
        var raw = client.DefaultRequestHeaders.GetValues(TestHost.ClientCertificateHeader).Single();
        return new X509Certificate2(Convert.FromBase64String(raw)).ExportCertificatePem();
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Guid pairingId, PairingConfirmation confirmation) =>
        client.PostAsJsonAsync($"/api/v1/pairing/requests/{pairingId}/confirm", confirmation, ContractJson.Options);

    private byte[] HostCertificateDer() => _host.Services.GetRequiredService<HostCertificateStore>().GetOrCreate().RawData;
}
