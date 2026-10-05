using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>The tray pipe server on a uniquely named pipe, driven by a raw pipe client.</summary>
public sealed class TrayPipeServerTests : IAsyncLifetime
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "HyperHarbor.Tests." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private PairedDeviceStore _devices = null!;
    private TrayPipeServer _server = null!;

    public async Task InitializeAsync()
    {
        var users = new UserStore(_dataDirectory);
        users.GetOrCreateDefault();
        _devices = new PairedDeviceStore(_dataDirectory, users);
        var services = new ServiceCollection()
            .AddSingleton(users)
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
    public async Task Connect_SendsDeviceList()
    {
        var device = AddDevice("Laptop");
        await using var tray = await ConnectAsync();

        var list = Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());

        Assert.Equal(device.DeviceId, Assert.Single(list.Devices).DeviceId);
    }

    [Fact]
    public async Task RemoveDevice_RemovesIt_AndBroadcastsTheNewList()
    {
        var device = AddDevice("Laptop");
        await using var tray = await ConnectAsync();
        await tray.ReceiveAsync();

        await tray.SendAsync(new RemoveDeviceMessage(device.DeviceId));

        var list = Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());
        Assert.Empty(list.Devices);
        Assert.Empty(_devices.List());
    }

    [Fact]
    public async Task RemoveDevice_IsAudited()
    {
        var device = AddDevice("Laptop");
        await using var tray = await ConnectAsync();
        await tray.ReceiveAsync();

        await tray.SendAsync(new RemoveDeviceMessage(device.DeviceId));

        // The new device list is broadcast while the device is removed, before the entry is written.
        var path = Path.Combine(_dataDirectory, FileAuditLog.FileName);
        await WaitUntil(() => File.Exists(path) && new FileInfo(path).Length > 0);
        var entry = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("trayRemoveDevice", (string?)entry["action"]);
        Assert.Equal("succeeded", (string?)entry["outcome"]);
        Assert.Equal(device.DeviceId.ToString(), (string?)entry["deviceId"]);
        Assert.Equal("Laptop", (string?)entry["deviceName"]);
        Assert.Equal(UserStore.DefaultUserName, (string?)entry["userName"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"noSuchMessage\"}")]
    [InlineData("{\"type\":\"removeDevice\",\"deviceId\":\"not-a-guid\"}")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public async Task InvalidMessages_AreIgnored_AndTheConnectionStaysUsable(string line)
    {
        await using var tray = await ConnectAsync();
        await tray.ReceiveAsync();

        await tray.SendLineAsync(line);
        await tray.SendAsync(new ListDevicesMessage());

        Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());
    }

    [Fact]
    public async Task OversizedMessage_ClosesTheConnection()
    {
        await using var tray = await ConnectAsync();
        await tray.ReceiveAsync();

        await tray.SendLineAsync(new string('x', TrayPipeServer.MaxMessageLength + 1));

        Assert.Null(await tray.ReceiveLineAsync());
    }

    [Fact]
    public async Task PairingStarted_IsBroadcastToEveryTray()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        await first.ReceiveAsync();
        await second.ReceiveAsync();
        await WaitUntil(() => _server.CanDisplayPin);

        _server.PairingStarted(Guid.NewGuid(), "Laptop", "123456", DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.Equal("123456", Assert.IsType<PairingStartedMessage>(await first.ReceiveAsync()).Pin);
        Assert.Equal("123456", Assert.IsType<PairingStartedMessage>(await second.ReceiveAsync()).Pin);
    }

    [Fact]
    public void PairingStartedMessage_ToStringHidesPin()
    {
        var message = new PairingStartedMessage(Guid.NewGuid(), "Laptop", "987654", DateTimeOffset.UtcNow);

        Assert.DoesNotContain("987654", message.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PipeAcl_AdmitsOnlySystemAdministratorsAndTheServiceAccount()
    {
        var security = TrayPipeServer.CreatePipeSecurity();

        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();
        Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));

        using var current = WindowsIdentity.GetCurrent();
        var allowed = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            current.User!,
        };
        Assert.All(rules, rule => Assert.Contains((SecurityIdentifier)rule.IdentityReference, allowed));

        // Spelled out so a regression names the group that was let in.
        var forbidden = new[]
        {
            WellKnownSidType.WorldSid,
            WellKnownSidType.BuiltinUsersSid,
            WellKnownSidType.AuthenticatedUserSid,
            WellKnownSidType.InteractiveSid,
            WellKnownSidType.NetworkSid,
            WellKnownSidType.AnonymousSid,
        };
        foreach (var sid in forbidden.Select(type => new SecurityIdentifier(type, null)))
        {
            Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(sid));
        }
    }

    [Fact]
    public void PipeAcl_AdmitsTheTrayUser_WithoutCreatingInstancesOrChangingTheAcl()
    {
        var trayUser = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");

        var rule = Assert.Single(
            TrayPipeServer.CreatePipeSecurity(trayUser)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>(),
            rule => rule.IdentityReference.Equals(trayUser));

        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.True(rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
        Assert.False(rule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.False(rule.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(rule.PipeAccessRights.HasFlag(PipeAccessRights.TakeOwnership));
    }

    private PairedDevice AddDevice(string name) =>
        _devices.Add(new UserStore(_dataDirectory).GetOrCreateDefault().UserId, name, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

    private async Task<TrayClient> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(Timeout);
        await pipe.ConnectAsync(timeout.Token);
        return new TrayClient(pipe);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the server.");
            await Task.Delay(20);
        }
    }

    internal sealed class TrayClient(NamedPipeClientStream pipe) : IAsyncDisposable
    {
        private readonly StreamReader _reader = new(pipe, Encoding.UTF8, leaveOpen: true);
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        public Task SendAsync(TrayMessage message) => SendLineAsync(TrayPipe.Serialize(message));

        public async Task SendLineAsync(string line)
        {
            try
            {
                await _writer.WriteLineAsync(line);
            }
            catch (IOException)
            {
                // The server closed the pipe; the next read reports it.
            }
        }

        public async Task<TrayMessage?> ReceiveAsync()
        {
            var line = await ReceiveLineAsync();
            Assert.NotNull(line);
            return TrayPipe.Deserialize(line!);
        }

        public async Task<string?> ReceiveLineAsync()
        {
            using var timeout = new CancellationTokenSource(Timeout);
            try
            {
                return await _reader.ReadLineAsync(timeout.Token);
            }
            catch (IOException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            try
            {
                // Flushes anything still buffered, which fails when the server has closed the pipe.
                await _writer.DisposeAsync();
            }
            catch (IOException)
            {
            }

            await pipe.DisposeAsync();
        }
    }
}
