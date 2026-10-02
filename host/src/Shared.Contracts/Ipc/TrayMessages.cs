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
public abstract record TrayMessage;

// Service to tray.

/// <summary>Show the PIN for a pairing request.</summary>
public sealed record PairingStartedMessage(Guid PairingId, string DeviceName, string Pin, DateTimeOffset ExpiresAt) : TrayMessage;

/// <param name="Outcome">paired, expired, tooManyAttempts, or cancelled.</param>
public sealed record PairingEndedMessage(Guid PairingId, string DeviceName, string Outcome) : TrayMessage;

/// <summary>Current paired devices. Sent on request and whenever the list changes.</summary>
public sealed record DeviceListMessage(IReadOnlyList<TrayDevice> Devices) : TrayMessage;

public sealed record TrayDevice(Guid DeviceId, string Name, string CertificateFingerprint, DateTimeOffset PairedAt);

// Tray to service.

public sealed record ListDevicesMessage : TrayMessage;

public sealed record RemoveDeviceMessage(Guid DeviceId) : TrayMessage;

public sealed record CancelPairingMessage : TrayMessage;

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
