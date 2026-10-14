using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Tailscale;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class TailscaleApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private const string TailnetAddress = "100.64.0.7";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public TailscaleApiTests()
    {
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task Get_ReportsNotInstalled_WhenThereIsNoClient()
    {
        var state = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/host/tailscale"))!;

        Assert.Equal("notInstalled", (string?)state["state"]);
        Assert.False((bool?)state["tagged"]);
        Assert.Equal("tag:hyperharbor-host", (string?)state["tag"]);
        Assert.Equal("tailscale up --advertise-tags=tag:hyperharbor-host", (string?)state["upCommand"]);
        Assert.Equal(41112, (int?)state["config"]!["localApiPort"]);
    }

    [Fact]
    public async Task Get_ReportsAddressNameAndTag_WhenRunning()
    {
        _host.Tailscale.Status = TailscaleReply.Ok("""
            {"BackendState":"Running","Self":{"ID":"nSELF","HostName":"pc","DNSName":"pc.tail1.ts.net.","TailscaleIPs":["100.64.0.1"],"Tags":["tag:hyperharbor-host"]}}
            """);

        var state = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/host/tailscale"))!;

        Assert.Equal("running", (string?)state["state"]);
        Assert.Equal("100.64.0.1", (string?)state["ipv4"]);
        Assert.Equal("pc.tail1.ts.net", (string?)state["dnsName"]);
        Assert.True((bool?)state["tagged"]);
    }

    [Fact]
    public async Task Settings_RequireElevation_AndChangeThePortAndTheUpCommand()
    {
        var body = new { localApiPort = 5555, controlUrl = "https://hs.example.com" };
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PutAsJsonAsync("/api/v1/host/tailscale/settings", body)).StatusCode);

        await ElevateAsync();
        var response = await _client.PutAsJsonAsync("/api/v1/host/tailscale/settings", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(5555, (int?)state["config"]!["localApiPort"]);
        Assert.Equal(
            "tailscale up --advertise-tags=tag:hyperharbor-host --login-server=https://hs.example.com",
            (string?)state["upCommand"]);
        Assert.Equal(5555, _host.Services.GetRequiredService<TailscaleSettingsStore>().Load().LocalApiPort);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(41112, "javascript:alert(1)")]
    [InlineData(41112, "https://hs.example.com; calc")]
    public async Task Settings_RejectAnInvalidPortOrUrl(int port, string? url)
    {
        await ElevateAsync();

        var response = await _client.PutAsJsonAsync("/api/v1/host/tailscale/settings", new { localApiPort = port, controlUrl = url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ARequestFromTheLan_IsNeverCheckedAgainstANode()
    {
        _host.Services.GetRequiredService<PairedDeviceStore>().SetTailscaleNode(DeviceId(), "nA", "a");

        using var request = Get("192.168.0.20");

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task TheFirstRequestOverTheTailnet_RecordsTheNode()
    {
        _host.Tailscale.Nodes[TailnetAddress] = "nPHONE";

        using var request = Get(TailnetAddress);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);

        var device = _host.Services.GetRequiredService<PairedDeviceStore>().List().Single();
        Assert.Equal("nPHONE", device.TailscaleNodeId);
    }

    [Fact]
    public async Task ARequestFromAnotherNode_IsRefusedWithAClearError()
    {
        _host.Services.GetRequiredService<PairedDeviceStore>().SetTailscaleNode(DeviceId(), "nLAPTOP", "laptop");
        _host.Tailscale.Nodes[TailnetAddress] = "nOTHER";

        using var request = Get(TailnetAddress);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(ContractInfo.ProblemCodes.TailscaleNodeMismatch, (string?)problem["code"]);
    }

    [Fact]
    public async Task ATiedDevice_IsRefused_WhenTailscaleCannotSayWhoCalled()
    {
        _host.Services.GetRequiredService<PairedDeviceStore>().SetTailscaleNode(DeviceId(), "nLAPTOP", "laptop");

        using var request = Get(TailnetAddress);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(ContractInfo.ProblemCodes.TailscaleNodeUnverified, (string?)problem["code"]);
    }

    [Fact]
    public async Task TheRecordedNode_IsAccepted()
    {
        _host.Services.GetRequiredService<PairedDeviceStore>().SetTailscaleNode(DeviceId(), "nLAPTOP", "laptop");
        _host.Tailscale.Nodes[TailnetAddress] = "nLAPTOP";

        using var request = Get(TailnetAddress);

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Relay_SendsOnTheHostsOwnNetwork_FromThatNetwork()
    {
        _host.RelayNetwork.LocalNetworks.Add(new LocalNetwork(IPAddress.Parse("192.168.0.10"), IPAddress.Parse("255.255.255.0")));

        var response = await _client.PostAsJsonAsync("/api/v1/wake/relay", Relay("192.168.0.235", "10.9.9.255"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, (int?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["sent"]);
        Assert.All(_host.RelayNetwork.Sent, sent => Assert.Equal(IPAddress.Parse("192.168.0.10"), sent.Source));
        // The broadcast address comes from the host's network, not from the request.
        Assert.Equal([IPAddress.Parse("192.168.0.255"), IPAddress.Broadcast], _host.RelayNetwork.Sent.Select(sent => sent.Target));
    }

    [Fact]
    public async Task Relay_RefusesAnAdapterThatIsNotOnTheHostsNetworks()
    {
        _host.RelayNetwork.LocalNetworks.Add(new LocalNetwork(IPAddress.Parse("192.168.0.10"), IPAddress.Parse("255.255.255.0")));

        var response = await _client.PostAsJsonAsync("/api/v1/wake/relay", Relay("172.16.5.5", "172.16.5.255"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.WakeTargetNotOnLan, (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
        Assert.Empty(_host.RelayNetwork.Sent);
    }

    private Guid DeviceId() => _host.Services.GetRequiredService<PairedDeviceStore>().List().Single().DeviceId;

    private static HttpRequestMessage Get(string remote)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/host");
        request.Headers.Add(TestHost.RemoteIpHeader, remote);
        return request;
    }

    private static object Relay(string ipv4, string broadcast) => new
    {
        adapters = new[] { new { name = "Ethernet", macAddress = "90-2E-16-66-C5-AE", ipv4Address = ipv4, broadcastAddress = broadcast } },
    };

    private async Task ElevateAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
        _client.DefaultRequestHeaders.Add(ContractInfo.ElevationHeader, token);
    }
}
