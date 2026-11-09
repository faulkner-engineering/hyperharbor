namespace HyperHarbor.Shared.Contracts.Ipc;

// The tray window calls the host's API for the VM page, as a paired device of its own host
// (docs/host-vm-management.md). The pipe admits only SYSTEM, Administrators, the service account, and the tray's
// user, so a registration that arrives on it needs no pairing PIN.

/// <summary>
/// Tray to service: makes the tray's client certificate a paired device ("Host tray") of the default User.
/// </summary>
/// <param name="CertificateFingerprint">SHA-256 of the tray's client certificate DER, hex.</param>
/// <param name="ReplacesDeviceId">The tray's previous device, removed when it is a tray device: the tray made a new
/// certificate, so the old entry would only clutter the device list.</param>
public sealed record TrayApiRegisterMessage(string CertificateFingerprint, Guid? ReplacesDeviceId) : TrayMessage;

/// <summary>Service to tray: the answer to <see cref="TrayApiRegisterMessage"/>.</summary>
/// <param name="DeviceId">The tray's device; null when <paramref name="Problem"/> says why it was not added.</param>
/// <param name="Address">Where the API listens, as an IP address the tray can reach (127.0.0.1 unless the API is bound
/// to one address).</param>
/// <param name="HostCertificateFingerprint">SHA-256 of the host certificate DER, hex, for the tray to pin.</param>
public sealed record TrayApiRegisteredMessage(
    Guid? DeviceId,
    string? Address,
    int Port,
    string? HostCertificateFingerprint,
    string? Problem) : TrayMessage;

/// <summary>The name of the tray's device in the device list.</summary>
public static class TrayApiDevice
{
    public const string Name = "Host tray";
}
