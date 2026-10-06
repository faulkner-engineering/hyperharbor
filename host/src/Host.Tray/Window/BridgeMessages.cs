using System.Text.Json.Serialization;
using HyperHarbor.Shared.Contracts.Hosts;

namespace HyperHarbor.Host.Tray.Window;

/// <summary>
/// What the tray's HTML app shows, sent whole on every change (it is small). Serialized with the contract's JSON
/// options, so <see cref="Update"/> has the same shape as GET /host/update. Mirrored in webui/src/bridge.ts;
/// TrayBridgeFixtureTests keep the two in step.
/// </summary>
/// <param name="PassphraseConfigured">Null while the service is not connected.</param>
/// <param name="Busy">
/// Work this tray started that has not finished: "passphrase", "console", "packageSearch", "folder:vm", "folder:iso",
/// "folder:backup", "device:&lt;id&gt;", "update". The page shows each as in progress.
/// </param>
/// <param name="Pairing">The pairing request waiting for its PIN, if any.</param>
public sealed record TrayViewState(
    bool Connected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? PassphraseConfigured,
    IReadOnlyList<TrayViewDevice> Devices,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] TrayViewFolder? VmFolder,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? IsoFolder,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BackupFolder,
    bool ConsoleReady,
    bool PackageSearchReady,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] HostUpdateStatus? Update,
    IReadOnlyList<string> Busy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] TrayViewPairing? Pairing,
    string DataDirectory);

public sealed record TrayViewDevice(Guid Id, string Name, string Fingerprint, DateTimeOffset PairedAt);

public sealed record TrayViewFolder(string Folder, bool IsDefault);

public sealed record TrayViewPairing(Guid PairingId, string DeviceName, string Pin, DateTimeOffset ExpiresAt);

/// <summary>The messages the tray sends to the page.</summary>
public static class HostToPage
{
    public sealed record State(TrayViewState Value)
    {
        public string Type => "state";
    }

    /// <param name="Kind">info, success, or error.</param>
    public sealed record Toast(string Kind, string Title, string Text)
    {
        public string Type => "toast";
    }

    /// <param name="Page">overview, devices, storage, updates, logs, or passphrase (the passphrase dialog).</param>
    public sealed record Navigate(string Page)
    {
        public string Type => "navigate";
    }
}
