using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Pairing;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class PairingApiTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly X509Certificate2 _clientCertificate = TestHost.CreateClientCertificate();

    public void Dispose()
    {
        _clientCertificate.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task ProtectedEndpoints_RejectMissingOrUnpairedCertificates()
    {
        using var anonymous = _host.CreateClient();
        using var unpaired = _host.CreateClient(_clientCertificate);

        foreach (var client in new[] { anonymous, unpaired })
        {
            foreach (var path in new[] { "/api/v1/vms", "/api/v1/host" })
            {
                var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            }
        }
    }

    [Fact]
    public async Task FullPairing_GrantsAccess_AndUnpairRevokesIt()
    {
        using var client = _host.CreateClient(_clientCertificate);

        var result = await PairAsync(client, pinOverride: null);

        Assert.Equal(_host.Services.GetRequiredService<HostCertificateStore>().GetOrCreate().ExportCertificatePem(), result.HostCertificatePem);
        Assert.Equal(_host.Services.GetRequiredService<Core.Users.UserStore>().GetOrCreateDefault().UserId, result.UserId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/vms")).StatusCode);

        var info = await client.GetFromJsonAsync<JsonObject>("/api/v1/host");
        Assert.Equal(result.HostId.ToString(), (string?)info!["hostId"]);
        Assert.Equal(ContractInfo.ApiVersion, (string?)info["apiVersion"]);
        Assert.Equal(
            CertificateFingerprint.Of(X509Certificate2.CreateFromPem(result.HostCertificatePem)),
            (string?)info["certificateFingerprint"]);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/pairing/devices/self")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/vms")).StatusCode);
    }

    [Fact]
    public async Task WrongPin_Returns401_ThenGoneAfterAttemptLimit()
    {
        using var client = _host.CreateClient();
        var created = await CreateAsync(client);
        var wrongPin = _host.Tray.Pin == "000000" ? "000001" : "000000";
        var exchange = TestPairingClient.Compute(created.PairingId, wrongPin, created.HostShare, _clientCertificate, HostCertificateDer());

        for (var attempt = 1; attempt < 5; attempt++)
        {
            var response = await ConfirmAsync(client, created.PairingId, exchange.Confirmation);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Gone, (await ConfirmAsync(client, created.PairingId, exchange.Confirmation)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await ConfirmAsync(client, created.PairingId, exchange.Confirmation)).StatusCode);
    }

    [Fact]
    public async Task RequestFromAnotherDeviceWhilePending_Returns429()
    {
        using var client = _host.CreateClient();
        await CreateAsync(client);
        using var other = TestHost.CreateClientCertificate("Other");

        var response = await client.PostAsJsonAsync(
            "/api/v1/pairing/requests",
            new PairingRequest("Other", other.ExportCertificatePem()));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task RequestFromSameDeviceWhilePending_ReplacesIt()
    {
        using var client = _host.CreateClient();
        var first = await CreateAsync(client);

        var second = await CreateAsync(client);

        Assert.NotEqual(first.PairingId, second.PairingId);
        Assert.Equal([PairingOutcome.Cancelled], _host.Tray.Outcomes);
        var stale = TestPairingClient.Compute(first.PairingId, _host.Tray.Pin!, first.HostShare, _clientCertificate, HostCertificateDer());
        Assert.Equal(HttpStatusCode.Gone, (await ConfirmAsync(client, first.PairingId, stale.Confirmation)).StatusCode);
    }

    [Fact]
    public async Task Cancel_EndsRequest_AndFreesHostForOtherDevices()
    {
        using var client = _host.CreateClient();
        var created = await CreateAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/pairing/requests/{created.PairingId}")).StatusCode);
        Assert.Equal([PairingOutcome.Cancelled], _host.Tray.Outcomes);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/pairing/requests/{created.PairingId}")).StatusCode);

        using var other = TestHost.CreateClientCertificate("Other");
        var response = await client.PostAsJsonAsync(
            "/api/v1/pairing/requests",
            new PairingRequest("Other", other.ExportCertificatePem()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_UnknownId_Returns404()
    {
        using var client = _host.CreateClient();
        await CreateAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/pairing/requests/{Guid.NewGuid()}")).StatusCode);
        Assert.Empty(_host.Tray.Outcomes);
    }

    [Fact]
    public async Task NoTray_Returns503()
    {
        _host.Tray.CanDisplayPin = false;
        using var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/pairing/requests", Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("", "valid")]
    [InlineData("Laptop", "not a certificate")]
    public async Task InvalidRequest_Returns400(string deviceName, string certificate)
    {
        using var client = _host.CreateClient();
        var pem = certificate == "valid" ? _clientCertificate.ExportCertificatePem() : certificate;

        var response = await client.PostAsJsonAsync("/api/v1/pairing/requests", new { deviceName, clientCertificatePem = pem });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownPairingId_Returns404()
    {
        using var client = _host.CreateClient();
        var exchange = new PairingConfirmation(new byte[384], new byte[32]);

        Assert.Equal(HttpStatusCode.NotFound, (await ConfirmAsync(client, Guid.NewGuid(), exchange)).StatusCode);
    }

    private async Task<PairingResult> PairAsync(HttpClient client, string? pinOverride)
    {
        var created = await CreateAsync(client);
        var exchange = TestPairingClient.Compute(
            created.PairingId,
            pinOverride ?? _host.Tray.Pin!,
            created.HostShare,
            _clientCertificate,
            HostCertificateDer());

        var response = await ConfirmAsync(client, created.PairingId, exchange.Confirmation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PairingResult>(ContractJson.Options))!;
        Assert.Equal(exchange.ExpectedHostConfirmation, result.HostConfirmation);
        return result;
    }

    private async Task<PairingRequestCreated> CreateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/pairing/requests", Request());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PairingRequestCreated>(ContractJson.Options))!;
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Guid pairingId, PairingConfirmation confirmation) =>
        client.PostAsJsonAsync($"/api/v1/pairing/requests/{pairingId}/confirm", confirmation, ContractJson.Options);

    private PairingRequest Request() => new("Laptop", _clientCertificate.ExportCertificatePem());

    private byte[] HostCertificateDer() => _host.Services.GetRequiredService<HostCertificateStore>().GetOrCreate().RawData;
}
