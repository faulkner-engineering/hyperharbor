using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>
/// Messages exchanged between the host service and the tray app over the named pipe
/// <see cref="TrayPipe.Name"/>. Each message is one line of JSON with a "type" discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PairingStartedMessage), "pairingStarted")]
[JsonDerivedType(typeof(PairingEndedMessage), "pairingEnded")]
[JsonDerivedType(typeof(DeviceListMessage), "deviceList")]
[JsonDerivedType(typeof(ListDevicesMessage), "listDevices")]
[JsonDerivedType(typeof(RemoveDeviceMessage), "removeDevice")]
[JsonDerivedType(typeof(CancelPairingMessage), "cancelPairing")]
[JsonDerivedType(typeof(WakeFixRequestedMessage), "wakeFixRequested")]
[JsonDerivedType(typeof(WakeFixCompletedMessage), "wakeFixCompleted")]
[JsonDerivedType(typeof(AdminPassphraseStatusMessage), "adminPassphraseStatus")]
[JsonDerivedType(typeof(SetAdminPassphraseMessage), "setAdminPassphrase")]
[JsonDerivedType(typeof(IsoFolderMessage), "isoFolder")]
[JsonDerivedType(typeof(SetIsoFolderMessage), "setIsoFolder")]
[JsonDerivedType(typeof(VmFolderMessage), "vmFolder")]
[JsonDerivedType(typeof(SetVmFolderMessage), "setVmFolder")]
[JsonDerivedType(typeof(BackupFolderMessage), "backupFolder")]
[JsonDerivedType(typeof(SetBackupFolderMessage), "setBackupFolder")]
public abstract record TrayMessage;

// Service to tray.

/// <summary>Show the PIN for a pairing request. ToString never includes the PIN.</summary>
public sealed record PairingStartedMessage(Guid PairingId, string DeviceName, string Pin, DateTimeOffset ExpiresAt) : TrayMessage
{
    public override string ToString() =>
        $"PairingStartedMessage {{ PairingId = {PairingId}, DeviceName = {DeviceName}, ExpiresAt = {ExpiresAt:u} }}";
}

/// <param name="Outcome">paired, expired, tooManyAttempts, or cancelled.</param>
public sealed record PairingEndedMessage(Guid PairingId, string DeviceName, string Outcome) : TrayMessage;

/// <summary>Current paired devices. Sent on request and whenever the list changes.</summary>
public sealed record DeviceListMessage(IReadOnlyList<TrayDevice> Devices) : TrayMessage;

public sealed record TrayDevice(Guid DeviceId, string Name, string CertificateFingerprint, DateTimeOffset PairedAt);

/// <summary>
/// A paired device asked to fix Wake-on-LAN settings and the service lacks administrator rights.
/// The tray asks the user, then runs the service executable elevated with --apply-wake-fixes.
/// </summary>
public sealed record WakeFixRequestedMessage(Guid RequestId, string RequestedBy, IReadOnlyList<WakeFixItem> Fixes) : TrayMessage;

public sealed record WakeFixItem(string CheckId, string Title);

/// <summary>Whether an admin passphrase is set. Sent on connect and after it changes.</summary>
public sealed record AdminPassphraseStatusMessage(bool Configured) : TrayMessage;

/// <summary>The ISO library folder in use. Sent on connect and after every change request.</summary>
/// <param name="Error">Why the last change request was refused, if it was.</param>
public sealed record IsoFolderMessage(string Folder, string? Error = null) : TrayMessage;

/// <summary>Where new VMs are created. Sent on connect and after every change request.</summary>
/// <param name="IsDefault">True when no folder is chosen and Hyper-V's default folders are used.</param>
/// <param name="Error">Why the last change request was refused, if it was.</param>
public sealed record VmFolderMessage(string Folder, bool IsDefault, string? Error = null) : TrayMessage;

/// <summary>Where disk exports go when a client names no folder. Sent on connect and after every change request.</summary>
/// <param name="Error">Why the last change request was refused, if it was.</param>
public sealed record BackupFolderMessage(string Folder, string? Error = null) : TrayMessage;

// Tray to service.

public sealed record ListDevicesMessage : TrayMessage;

public sealed record RemoveDeviceMessage(Guid DeviceId) : TrayMessage;

public sealed record CancelPairingMessage : TrayMessage;

/// <summary>Moves the ISO library to a local folder, which is created if missing. Existing images are not moved.</summary>
public sealed record SetIsoFolderMessage(string Folder) : TrayMessage;

/// <summary>Creates new VMs under a local folder, which is created if missing. Existing VMs are not moved.</summary>
public sealed record SetVmFolderMessage(string Folder) : TrayMessage;

/// <summary>Sends disk exports to a local folder, which is created if missing. Earlier exports are not moved.</summary>
public sealed record SetBackupFolderMessage(string Folder) : TrayMessage;

/// <param name="Outcome">applied, declined, or failed.</param>
public sealed record WakeFixCompletedMessage(Guid RequestId, string Outcome, string? Detail) : TrayMessage;

/// <summary>
/// Sets or replaces the admin passphrase used for elevation. Carries only a PBKDF2 hash made by
/// <see cref="AdminPassphrase.CreateHash"/>; the passphrase itself never crosses the pipe.
/// </summary>
public sealed record SetAdminPassphraseMessage(byte[] Salt, byte[] Hash, int Iterations) : TrayMessage
{
    public override string ToString() => $"SetAdminPassphraseMessage {{ Iterations = {Iterations} }}";
}

/// <summary>Command-line switch for the elevated fix helper: --apply-wake-fixes id1,id2.</summary>
public static class WakeFixHelper
{
    public const string Switch = "--apply-wake-fixes";
}

/// <summary>
/// Command-line switches for the elevated console account helper:
/// --setup-console [data directory] [result file] and --remove-console [data directory] [result file].
/// </summary>
public static class ConsoleSetupHelper
{
    public const string SetupSwitch = "--setup-console";
    public const string RemoveSwitch = "--remove-console";

    /// <summary>The DPAPI-protected credential file the helper writes in the data directory; its presence means console access is set up.</summary>
    public const string AccountsFileName = "console-accounts.json.protected";
}

public static class TrayPipe
{
    public const string Name = "HyperHarbor.Host.Tray";

    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static string Serialize(TrayMessage message) => JsonSerializer.Serialize(message, JsonOptions);

    public static TrayMessage? Deserialize(string line) => JsonSerializer.Deserialize<TrayMessage>(line, JsonOptions);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
