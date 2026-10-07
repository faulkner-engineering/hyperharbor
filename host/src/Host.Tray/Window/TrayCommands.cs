using System.Text.Json;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray.Window;

/// <summary>Where a folder the user chooses is used.</summary>
public enum TrayFolder
{
    Vm,
    Iso,
    Backup,
}

/// <summary>What the page can ask the tray to do. TrayApplicationContext does it; tests use a fake.</summary>
public interface ITrayActions
{
    /// <summary>The plaintext stays in the tray: it is checked, hashed, and only the hash goes to the service.</summary>
    void SetPassphrase(string passphrase);

    void RemoveDevice(Guid deviceId);

    void ChooseFolder(TrayFolder folder);

    void SetUpConsole();

    void SetUpPackageSearch();

    void CheckForUpdate();

    void InstallUpdate();

    void SetUpdateChannel(string channel);

    /// <param name="source">lean or undo (<see cref="HostLeanSources"/>).</param>
    void HostLeanDryRun(string source);

    /// <param name="source">lean or undo (<see cref="HostLeanSources"/>).</param>
    void HostLeanApply(string source);

    void SetHostLeanSchedule(bool enabled);

    /// <param name="target">logs or audit.</param>
    void Open(string target);

    void OpenUrl(string url);

    void CancelPairing(Guid pairingId);

    void CloseWindow();
}

/// <summary>
/// Reads one message from the page and calls <see cref="ITrayActions"/>. The page is the tray's own embedded app,
/// but its input is still checked: an unknown or malformed message is ignored and reported as false.
/// </summary>
public static class TrayCommands
{
    /// <returns>False when the message is not one the tray knows (or is missing what it needs).</returns>
    public static bool Dispatch(JsonElement message, ITrayActions actions)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var typeProperty) || typeProperty.GetString() is not { } type)
        {
            return false;
        }

        switch (type)
        {
            case "ready":
                return true;
            case "setPassphrase" when Text(message, "passphrase") is { } passphrase:
                actions.SetPassphrase(passphrase);
                return true;
            case "removeDevice" when Id(message, "deviceId") is { } device:
                actions.RemoveDevice(device);
                return true;
            case "chooseFolder" when Text(message, "folder") is { } folder && Folder(folder) is { } which:
                actions.ChooseFolder(which);
                return true;
            case "setUpConsole":
                actions.SetUpConsole();
                return true;
            case "setUpPackageSearch":
                actions.SetUpPackageSearch();
                return true;
            case "checkUpdate":
                actions.CheckForUpdate();
                return true;
            case "installUpdate":
                actions.InstallUpdate();
                return true;
            case "setChannel" when Text(message, "channel") is { Length: > 0 } channel:
                actions.SetUpdateChannel(channel);
                return true;
            case "hostLeanDryRun" when Text(message, "source") is { } dryRunSource && HostLeanSources.IsKnown(dryRunSource):
                actions.HostLeanDryRun(dryRunSource);
                return true;
            case "hostLeanApply" when Text(message, "source") is { } applySource && HostLeanSources.IsKnown(applySource):
                actions.HostLeanApply(applySource);
                return true;
            case "setHostLeanSchedule" when Flag(message, "enabled") is { } enabled:
                actions.SetHostLeanSchedule(enabled);
                return true;
            case "open" when Text(message, "target") is "logs" or "audit":
                actions.Open(Text(message, "target")!);
                return true;
            case "openUrl" when Text(message, "url") is { } url:
                actions.OpenUrl(url);
                return true;
            case "cancelPairing" when Id(message, "pairingId") is { } pairing:
                actions.CancelPairing(pairing);
                return true;
            case "close":
                actions.CloseWindow();
                return true;
            default:
                return false;
        }
    }

    private static string? Text(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Flag(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) ? value.GetBoolean() : null;

    private static Guid? Id(JsonElement message, string name) =>
        Text(message, name) is { } text && Guid.TryParse(text, out var id) ? id : null;

    private static TrayFolder? Folder(string text) => text switch
    {
        "vm" => TrayFolder.Vm,
        "iso" => TrayFolder.Iso,
        "backup" => TrayFolder.Backup,
        _ => null,
    };
}
