using System.IO.Pipes;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Tests.Api;
using HyperHarbor.Host.Tray.Api;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>
/// The tray window's API requests (phase 33), end to end without Hyper-V: the real service executable on a loopback
/// port, the tray's registration over its pipe, and the requests over TLS with the tray's certificate.
/// </summary>
public sealed class TrayApiForwarderTests : IClassFixture<RealTlsTests.ServiceProcess>, IDisposable
{
    private readonly RealTlsTests.ServiceProcess _service;
    private readonly string _identityDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));

    public TrayApiForwarderTests(RealTlsTests.ServiceProcess service)
    {
        _service = service;
    }

    public void Dispose()
    {
        if (Directory.Exists(_identityDirectory))
        {
            Directory.Delete(_identityDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ARequest_RegistersTheTray_AndReachesTheApiWithItsCertificate()
    {
        using var forwarder = new TrayApiForwarder(new TrayApiIdentity(_identityDirectory), RegisterAsync);

        var response = await forwarder.SendAsync("GET", "/auth/elevation", null);

        Assert.Null(response.Error);
        Assert.Equal(200, response.Status);
        Assert.Equal(false, (bool?)JsonNode.Parse(response.Body!)!["active"]);
    }

    [Fact]
    public async Task Elevating_KeepsTheTokenInTheTray_AndSendsItWithLaterRequests()
    {
        using var forwarder = new TrayApiForwarder(new TrayApiIdentity(_identityDirectory), RegisterAsync);

        var elevated = await forwarder.SendAsync(
            "POST", "/auth/elevation", new JsonObject { ["passphrase"] = RealTlsTests.ServiceProcess.Passphrase }.ToJsonString());
        var status = await forwarder.SendAsync("GET", "/auth/elevation", null);

        Assert.Equal(200, elevated.Status);
        var grant = JsonNode.Parse(elevated.Body!)!.AsObject();
        Assert.Null(grant["token"]);
        Assert.NotNull(grant["expiresAt"]);
        Assert.Equal(true, (bool?)JsonNode.Parse(status.Body!)!["active"]);
    }

    [Fact]
    public async Task ARouteOutsideTheList_IsNotSent()
    {
        var registrations = 0;
        using var forwarder = new TrayApiForwarder(new TrayApiIdentity(_identityDirectory), message =>
        {
            registrations++;
            return RegisterAsync(message);
        });

        var response = await forwarder.SendAsync("GET", "/pairing/devices", null);

        Assert.Equal(0, response.Status);
        Assert.NotNull(response.Error);
        Assert.Equal(0, registrations);
    }

    [Fact]
    public async Task AfterTheDeviceIsRemoved_TheTrayRegistersAgain()
    {
        var identity = new TrayApiIdentity(_identityDirectory);
        using var forwarder = new TrayApiForwarder(identity, RegisterAsync);
        Assert.Equal(200, (await forwarder.SendAsync("GET", "/auth/elevation", null)).Status);

        await using (var tray = await ConnectAsync())
        {
            // Read while sending, as in RegisterAsync.
            var removed = Task.Run(async () =>
            {
                while (await tray.ReceiveAsync() is not DeviceListMessage list || list.Devices.Any(device => device.DeviceId == identity.DeviceId))
                {
                }
            });
            await tray.SendAsync(new RemoveDeviceMessage(identity.DeviceId!.Value));
            await removed;
        }

        var response = await forwarder.SendAsync("GET", "/auth/elevation", null);

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task AHostWithAnotherCertificate_IsNotTrusted()
    {
        using var forwarder = new TrayApiForwarder(new TrayApiIdentity(_identityDirectory), async message =>
            await RegisterAsync(message) with { HostCertificateFingerprint = new string('0', 64) });

        var response = await forwarder.SendAsync("GET", "/auth/elevation", null);

        Assert.Equal(0, response.Status);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public async Task WithoutTheService_TheProblemIsReported()
    {
        using var forwarder = new TrayApiForwarder(new TrayApiIdentity(_identityDirectory), _ =>
            Task.FromResult(new TrayApiRegisteredMessage(null, null, 0, null, "The HyperHarbor service is not running.")));

        var response = await forwarder.SendAsync("GET", "/vms", null);

        Assert.Equal(0, response.Status);
        Assert.Equal("The HyperHarbor service is not running.", response.Error);
    }

    private async Task<TrayApiRegisteredMessage> RegisterAsync(TrayApiRegisterMessage message)
    {
        await using var tray = await ConnectAsync();
        // Read while sending: the service writes its status messages on connect and waits for them to be read.
        var answer = ReceiveAnswerAsync(tray);
        await tray.SendAsync(message);
        return await answer;
    }

    private static async Task<TrayApiRegisteredMessage> ReceiveAnswerAsync(TrayPipeServerTests.TrayClient tray)
    {
        while (true)
        {
            if (await tray.ReceiveAsync() is TrayApiRegisteredMessage answer)
            {
                return answer;
            }
        }
    }

    private async Task<TrayPipeServerTests.TrayClient> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", _service.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TrayPipeServerTests.Timeout);
        await pipe.ConnectAsync(timeout.Token);
        return new TrayPipeServerTests.TrayClient(pipe);
    }
}
