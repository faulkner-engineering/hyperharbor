using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.Tray;

/// <summary>
/// Serves tray apps over a named pipe. Shows pairing PINs and lets the tray list and revoke
/// paired devices. The pipe ACL admits only SYSTEM, Administrators, and the account the service runs
/// as: anyone who can connect sees pairing PINs, can revoke devices, and can set the admin passphrase.
/// </summary>
public sealed class TrayPipeServer : BackgroundService, IPairingNotifier, Wake.IWakeFixApprover
{
    private readonly PairedDeviceStore _devices;
    private readonly IServiceProvider _services;
    private readonly ILogger<TrayPipeServer> _logger;
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    /// <summary>Longest message accepted from a tray, in characters. Longer input closes the connection.</summary>
    public const int MaxMessageLength = 64 * 1024;

    public TrayPipeServer(PairedDeviceStore devices, IServiceProvider services, ILogger<TrayPipeServer> logger, string pipeName = TrayPipe.Name)
    {
        _pipeName = pipeName;
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
                    _pipeName,
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
            if (Elevation is { } elevation)
            {
                await connection.SendAsync(new AdminPassphraseStatusMessage(elevation.IsConfigured));
            }

            if (_services.GetService<IsoLibrary>() is { } isos)
            {
                await connection.SendAsync(new IsoFolderMessage(isos.Folder));
            }

            if (_services.GetService<VmStorageLocation>() is { } location)
            {
                await connection.SendAsync(await VmFolderAsync(location));
            }

            if (_services.GetService<BackupLocation>() is { } backups)
            {
                await connection.SendAsync(new BackupFolderMessage(backups.Folder));
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var lines = new BoundedLineReader(reader, MaxMessageLength);
            while (!stoppingToken.IsCancellationRequested && await lines.ReadLineAsync(stoppingToken) is { } line)
            {
                await HandleMessageAsync(connection, line);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The tray disconnected or the service is stopping.
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("Closing a tray connection that sent a message longer than {MaxLength} characters.", MaxMessageLength);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray connection failed.");
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
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
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
                var removed = _devices.List().FirstOrDefault(device => device.DeviceId == remove.DeviceId);
                if (_devices.Remove(remove.DeviceId))
                {
                    _logger.LogInformation("Device {DeviceId} was unpaired from the tray.", remove.DeviceId);
                    AuditTrayAction("trayRemoveDevice", removed?.UserId, remove.DeviceId, removed?.Name);
                }

                break;
            case CancelPairingMessage:
                _services.GetRequiredService<PairingService>().Cancel();
                break;
            case WakeFixCompletedMessage completed:
                AuditTrayAction(
                    "trayWakeFix",
                    null,
                    null,
                    null,
                    $"requestId={completed.RequestId}, outcome={completed.Outcome}",
                    completed.Outcome == "applied" ? AuditOutcome.Succeeded : AuditOutcome.Failed);
                _services.GetRequiredService<Wake.WakeFixCoordinator>().OnCompleted(completed);
                break;
            case SetVmFolderMessage vmFolder:
                await SetVmFolderAsync(vmFolder);
                break;
            case SetIsoFolderMessage isoFolder:
                SetIsoFolder(isoFolder);
                break;
            case SetBackupFolderMessage backupFolder:
                SetBackupFolder(backupFolder);
                break;
            case SetAdminPassphraseMessage set:
                SetAdminPassphrase(set);
                break;
        }
    }

    private ElevationService? Elevation => _services.GetService<ElevationService>();

    /// <summary>
    /// Moves the ISO library to a local folder chosen at the host. Network paths are refused: Hyper-V needs
    /// extra delegation to attach images from a share. Every tray hears the folder in use afterwards.
    /// </summary>
    private void SetIsoFolder(SetIsoFolderMessage request)
    {
        if (_services.GetService<HostSettingsStore>() is not { } settings || _services.GetService<IsoLibrary>() is not { } isos)
        {
            return;
        }

        var error = UseLocalFolder(request.Folder, @"D:\ISOs", folder =>
        {
            settings.SetIsoFolder(folder);
            _logger.LogInformation("The ISO library folder was changed to {Folder} from the tray.", folder);
            AuditTrayAction("traySetIsoFolder", null, null, null, $"folder={folder}");
        });
        Broadcast(new IsoFolderMessage(isos.Folder, error));
    }

    /// <summary>Sends disk exports that name no folder to a local folder chosen at the host. Earlier exports stay where they are.</summary>
    private void SetBackupFolder(SetBackupFolderMessage request)
    {
        if (_services.GetService<HostSettingsStore>() is not { } settings || _services.GetService<BackupLocation>() is not { } backups)
        {
            return;
        }

        var error = UseLocalFolder(request.Folder, @"D:\Backups", folder =>
        {
            settings.SetBackupFolder(folder);
            _logger.LogInformation("The backup folder was changed to {Folder} from the tray.", folder);
            AuditTrayAction("traySetBackupFolder", null, null, null, $"folder={folder}");
        });
        Broadcast(new BackupFolderMessage(backups.Folder, error));
    }

    /// <summary>
    /// Creates new VMs under a local folder chosen at the host. Existing VMs stay where they are. Network
    /// paths are refused: Hyper-V needs extra delegation to run VMs from a share.
    /// </summary>
    private async Task SetVmFolderAsync(SetVmFolderMessage request)
    {
        if (_services.GetService<HostSettingsStore>() is not { } settings || _services.GetService<VmStorageLocation>() is not { } location)
        {
            return;
        }

        var error = UseLocalFolder(request.Folder, @"D:\VMs", folder =>
        {
            settings.SetVmFolder(folder);
            _logger.LogInformation("The folder for new VMs was changed to {Folder} from the tray.", folder);
            AuditTrayAction("traySetVmFolder", null, null, null, $"folder={folder}");
        });
        Broadcast(await VmFolderAsync(location, error));
    }

    private static async Task<VmFolderMessage> VmFolderAsync(VmStorageLocation location, string? error = null)
    {
        try
        {
            return new VmFolderMessage(await location.DisplayFolderAsync(CancellationToken.None), location.RootFolder is null, error);
        }
        catch (Core.HyperV.HyperVUnavailableException ex)
        {
            return new VmFolderMessage(location.RootFolder ?? "Hyper-V default (Hyper-V is not available)", location.RootFolder is null, error ?? ex.Message);
        }
    }

    /// <summary>Checks that <paramref name="requested"/> is a local folder, creates it, and saves it.</summary>
    /// <returns>Null on success, else why the folder was refused.</returns>
    private static string? UseLocalFolder(string? requested, string example, Action<string> save)
    {
        var folder = requested?.Trim() ?? string.Empty;
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return $"Choose a folder on a local drive, for example {example}.";
        }

        try
        {
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            save(folder);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"The folder could not be used: {ex.Message}";
        }
    }

    private void SetAdminPassphrase(SetAdminPassphraseMessage set)
    {
        if (Elevation is not { } elevation)
        {
            return;
        }

        if (set.Iterations < AdminPassphrase.MinimumIterations)
        {
            _logger.LogWarning("Ignoring an admin passphrase hash with {Iterations} iterations; at least {Minimum} are required.", set.Iterations, AdminPassphrase.MinimumIterations);
            return;
        }

        try
        {
            elevation.SetPassphrase(set.Salt, set.Hash, set.Iterations);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("Ignoring an admin passphrase hash that is not in the expected format.");
            return;
        }

        AuditTrayAction("traySetAdminPassphrase", null, null, null);
        Broadcast(new AdminPassphraseStatusMessage(Configured: true));
    }

    /// <summary>
    /// Records an action taken at the host. Tray actions already happened when this runs, and the
    /// person at the host is trusted, so a failure is logged instead of undoing the action.
    /// </summary>
    private void AuditTrayAction(
        string action,
        Guid? userId,
        Guid? deviceId,
        string? deviceName,
        string? detail = null,
        AuditOutcome outcome = AuditOutcome.Succeeded)
    {
        if (_services.GetService<IAuditLog>() is not { } audit)
        {
            return;
        }

        var time = _services.GetService<TimeProvider>() ?? TimeProvider.System;
        try
        {
            audit.Write(new AuditEntry(
                time.GetUtcNow(),
                action,
                outcome,
                userId,
                userId is { } id ? _services.GetService<UserStore>()?.Find(id)?.Name : null,
                deviceId,
                deviceName,
                Detail: detail ?? "Host tray"));
        }
        catch (AuditUnavailableException ex)
        {
            _logger.LogError(ex, "Could not write the audit entry for tray action {Action}.", action);
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

    /// <summary>SYSTEM, Administrators, and the service account. Exposed for tests.</summary>
    internal static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        void Allow(IdentityReference identity, PipeAccessRights rights) =>
            security.AddAccessRule(new PipeAccessRule(identity, rights, AccessControlType.Allow));

        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl);
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl);

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
