using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.Tray;

/// <summary>
/// Serves tray apps over a named pipe. Shows pairing PINs and lets the tray list and revoke
/// paired devices. The pipe ACL admits SYSTEM, Administrators, the service account, and
/// interactively logged-on users.
/// </summary>
public sealed class TrayPipeServer : BackgroundService, IPairingNotifier, Wake.IWakeFixApprover
{
    private readonly PairedDeviceStore _devices;
    private readonly IServiceProvider _services;
    private readonly ILogger<TrayPipeServer> _logger;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    public TrayPipeServer(PairedDeviceStore devices, IServiceProvider services, ILogger<TrayPipeServer> logger)
    {
        _devices = devices;
        _services = services;
        _logger = logger;
        _devices.Changed += (_, _) => Broadcast(DeviceList());
    }

    public bool CanDisplayPin => !_connections.IsEmpty;

    public bool CanRequestApproval => !_connections.IsEmpty;

    public void PairingStarted(Guid pairingId, string deviceName, string pin, DateTimeOffset expiresAt) =>
        Broadcast(new PairingStartedMessage(pairingId, deviceName, pin, expiresAt));

    public void PairingEnded(Guid pairingId, string deviceName, PairingOutcome outcome) =>
        Broadcast(new PairingEndedMessage(pairingId, deviceName, JsonNamingPolicyCamel(outcome)));

    /// <summary>Sends a fix approval request to one tray, so the user sees a single UAC prompt.</summary>
    public void RequestApproval(WakeFixRequestedMessage request)
    {
        if (_connections.Values.FirstOrDefault() is { } connection)
        {
            _ = connection.SendAsync(request);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    TrayPipe.Name,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    CreatePipeSecurity());
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Could not create the tray pipe; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }

            _ = HandleConnectionAsync(pipe, stoppingToken);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var connection = new Connection(pipe);
        _connections[connection.Id] = connection;
        _logger.LogInformation("Tray app connected.");

        try
        {
            await connection.SendAsync(DeviceList());
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (!stoppingToken.IsCancellationRequested && await reader.ReadLineAsync(stoppingToken) is { } line)
            {
                await HandleMessageAsync(connection, line);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The tray disconnected or the service is stopping.
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            await connection.DisposeAsync();
            _logger.LogInformation("Tray app disconnected.");
        }
    }

    private async Task HandleMessageAsync(Connection connection, string line)
    {
        TrayMessage? message;
        try
        {
            message = TrayPipe.Deserialize(line);
        }
        catch (System.Text.Json.JsonException)
        {
            _logger.LogWarning("Ignoring malformed tray message.");
            return;
        }

        switch (message)
        {
            case ListDevicesMessage:
                await connection.SendAsync(DeviceList());
                break;
            case RemoveDeviceMessage remove:
                if (_devices.Remove(remove.DeviceId))
                {
                    _logger.LogInformation("Device {DeviceId} was unpaired from the tray.", remove.DeviceId);
                }

                break;
            case CancelPairingMessage:
                _services.GetRequiredService<PairingService>().Cancel();
                break;
            case WakeFixCompletedMessage completed:
                _services.GetRequiredService<Wake.WakeFixCoordinator>().OnCompleted(completed);
                break;
        }
    }

    private DeviceListMessage DeviceList() => new(_devices.List()
        .Select(device => new TrayDevice(device.DeviceId, device.Name, device.CertificateFingerprint, device.PairedAt))
        .ToList());

    private void Broadcast(TrayMessage message)
    {
        foreach (var connection in _connections.Values)
        {
            _ = connection.SendAsync(message);
        }
    }

    private static string JsonNamingPolicyCamel(PairingOutcome outcome) =>
        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString());

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        void Allow(IdentityReference identity, PipeAccessRights rights) =>
            security.AddAccessRule(new PipeAccessRule(identity, rights, AccessControlType.Allow));

        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl);
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl);
        Allow(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite);

        using var current = WindowsIdentity.GetCurrent();
        if (current.User is { } user)
        {
            Allow(user, PipeAccessRights.FullControl);
        }

        return security;
    }

    /// <summary>One tray connection. Writes are serialized so broadcast messages do not interleave.</summary>
    private sealed class Connection(NamedPipeServerStream pipe) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        public Guid Id { get; } = Guid.NewGuid();

        public async Task SendAsync(TrayMessage message)
        {
            await _writeLock.WaitAsync();
            try
            {
                await _writer.WriteLineAsync(TrayPipe.Serialize(message));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The connection is closing; the read loop removes it.
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
            await pipe.DisposeAsync();
            _writeLock.Dispose();
        }
    }
}
