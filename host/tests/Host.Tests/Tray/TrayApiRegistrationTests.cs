using System.IO.Pipes;
using System.Security.Cryptography;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Service.Api;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>The tray asking the service to make its certificate a device, for the VM page (phase 33).</summary>
public sealed class TrayApiRegistrationTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "HyperHarbor.Tests." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private readonly ApiOptions _api = new() { Port = 48555 };
    private UserStore _users = null!;
    private PairedDeviceStore _devices = null!;
    private HostCertificateStore _hostCertificate = null!;
    private TrayPipeServer _server = null!;

    public async Task InitializeAsync()
    {
        _users = new UserStore(_dataDirectory);
        _devices = new PairedDeviceStore(_dataDirectory, _users);
        _hostCertificate = new HostCertificateStore(_dataDirectory, "TestHost");
        var services = new ServiceCollection()
            .AddSingleton(_users)
            .AddSingleton(_hostCertificate)
            .AddSingleton(Options.Create(_api))
            .AddSingleton<IAuditLog>(new FileAuditLog(_dataDirectory))
            .BuildServiceProvider();
        _server = new TrayPipeServer(_devices, services, NullLogger<TrayPipeServer>.Instance, _pipeName);
        await _server.StartAsync(_stop.Token);
    }

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
        _stop.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Register_AddsAHostTrayDevice_ForTheDefaultUser_AndSaysWhereTheApiIs()
    {
        var fingerprint = Fingerprint();

        var answer = await RegisterAsync(new TrayApiRegisterMessage(fingerprint, null));

        Assert.Null(answer.Problem);
        var device = Assert.Single(_devices.List());
        Assert.Equal(device.DeviceId, answer.DeviceId);
        Assert.Equal(TrayApiDevice.Name, device.Name);
        Assert.Equal(fingerprint, device.CertificateFingerprint);
        Assert.Equal(_users.GetOrCreateDefault().UserId, device.UserId);
        Assert.Equal("127.0.0.1", answer.Address);
        Assert.Equal(48555, answer.Port);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(_hostCertificate.GetOrCreate().RawData)), answer.HostCertificateFingerprint);
    }

    [Fact]
    public async Task Register_IsAudited()
    {
        var answer = await RegisterAsync(new TrayApiRegisterMessage(Fingerprint(), null));

        var path = Path.Combine(_dataDirectory, FileAuditLog.FileName);
        var entry = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllLines(path).Single())!;
        Assert.Equal("trayApiRegister", (string?)entry["action"]);
        Assert.Equal(answer.DeviceId.ToString(), (string?)entry["deviceId"]);
        Assert.Equal(TrayApiDevice.Name, (string?)entry["deviceName"]);
    }

    [Fact]
    public async Task Register_AgainWithTheSameCertificate_KeepsTheDevice()
    {
        var fingerprint = Fingerprint();
        var first = await RegisterAsync(new TrayApiRegisterMessage(fingerprint, null));

        var second = await RegisterAsync(new TrayApiRegisterMessage(fingerprint, first.DeviceId));

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Single(_devices.List());
    }

    [Fact]
    public async Task Register_WithANewCertificate_ReplacesTheTraysPreviousDevice()
    {
        var first = await RegisterAsync(new TrayApiRegisterMessage(Fingerprint(), null));

        var second = await RegisterAsync(new TrayApiRegisterMessage(Fingerprint(), first.DeviceId));

        Assert.NotEqual(first.DeviceId, second.DeviceId);
        Assert.Equal(second.DeviceId, Assert.Single(_devices.List()).DeviceId);
    }

    [Fact]
    public async Task Register_NeverRemovesAClientDevice()
    {
        var laptop = _devices.Add(_users.GetOrCreateDefault().UserId, "Laptop", Fingerprint(), DateTimeOffset.UtcNow);

        await RegisterAsync(new TrayApiRegisterMessage(Fingerprint(), laptop.DeviceId));

        Assert.Contains(_devices.List(), device => device.DeviceId == laptop.DeviceId);
        Assert.Equal(2, _devices.List().Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD")]
    [InlineData("ZZ00000000000000000000000000000000000000000000000000000000000000")]
    public async Task Register_RefusesAFingerprintThatIsNotASha256Hash(string fingerprint)
    {
        var answer = await RegisterAsync(new TrayApiRegisterMessage(fingerprint, null));

        Assert.Null(answer.DeviceId);
        Assert.NotNull(answer.Problem);
        Assert.Empty(_devices.List());
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData("0.0.0.0", "127.0.0.1")]
    [InlineData("::", "127.0.0.1")]
    public async Task Register_AnswersAnAddressTheTrayCanReach(string listenAddress, string expected)
    {
        _api.ListenAddress = listenAddress;

        var answer = await RegisterAsync(new TrayApiRegisterMessage(Fingerprint(), null));

        Assert.Equal(expected, answer.Address);
    }

    private static string Fingerprint() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private async Task<TrayApiRegisteredMessage> RegisterAsync(TrayApiRegisterMessage message)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var timeout = new CancellationTokenSource(TrayPipeServerTests.Timeout))
        {
            await pipe.ConnectAsync(timeout.Token);
        }

        await using var tray = new TrayPipeServerTests.TrayClient(pipe);
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
}
