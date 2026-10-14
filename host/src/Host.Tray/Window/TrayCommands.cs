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

    /// <summary>Reads the PC again for the Game ready section.</summary>
    void GameReadyRefresh();

    /// <param name="cardId">One of <see cref="GameReadyCardIds.Fixable"/>.</param>
    void GameReadyFix(string cardId);

    /// <param name="delaySeconds">5 to 300.</param>
    void GameReadyTestWake(int delaySeconds);

    /// <summary>Sets up the Console sign-in (a standard user signed in automatically, with a launcher).</summary>
    void GameReadyConsoleSignInEnable(string userName, string launcher);

    void GameReadyConsoleSignInDisable(bool deleteUser);

    /// <summary>Applies a power mode: consoleIdle, balanced, or sleepWhenIdle, with its display timeout, schedule, and cost inputs.</summary>
    void GameReadyPowerModeApply(string mode, int displayMinutes, bool scheduleEnabled, string sleepTime, string wakeTime, double watts, double ratePerKwh);

    /// <summary>Saves the Ambient screen choices (idle minutes, widgets, soft lock and its chord).</summary>
    /// <summary>Game ready: where the host finds its Tailscale client (local API port, optional control server URL).</summary>
    void GameReadyTailscaleSettings(int localApiPort, string? controlUrl);

    void GameReadyAmbientApply(bool enabled, int idleMinutes, bool showHostStatus, bool showRunningVms, bool showDevices, bool softLock, string chord);

    /// <summary>Puts this PC to sleep (after the page's confirmation).</summary>
    void GameReadySleepNow();

    /// <summary>Saves the game-aware settings.</summary>
    void GameAwareApply(Shared.Contracts.Hosts.GameAwareSettings settings);

    /// <summary>"This was not a game": ignore the program that started the game state.</summary>
    void GameAwareNotAGame();

    void BackupRunNow();

    /// <summary>Restore page: check the recovery key and list what the location holds.</summary>
    void RestoreWizardAttach(RestoreWizardForm form);

    /// <summary>Restore page: restore a host-state ("host") or VM ("vm") snapshot.</summary>
    void RestoreWizardRestore(string kind, string snapshotId, string? name, bool start);

    void RestoreWizardReset();

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
            case "gameReadyRefresh":
                actions.GameReadyRefresh();
                return true;
            case "gameReadyFix" when Text(message, "cardId") is { } cardId && GameReadyCardIds.Fixable.Contains(cardId):
                actions.GameReadyFix(cardId);
                return true;
            case "gameReadyTestWake" when Number(message, "delaySeconds") is >= 5 and <= 300 and var delay:
                actions.GameReadyTestWake(delay);
                return true;
            case "gameReadyConsoleSignInEnable" when Text(message, "userName") is { Length: > 0 and <= 64 } signInUser && Text(message, "launcher") is { Length: > 0 and <= 32 } signInLauncher:
                actions.GameReadyConsoleSignInEnable(signInUser, signInLauncher);
                return true;
            case "gameReadyConsoleSignInDisable" when Flag(message, "deleteUser") is { } deleteUser:
                actions.GameReadyConsoleSignInDisable(deleteUser);
                return true;
            case "gameReadyPowerModeApply"
                when Text(message, "mode") is { Length: > 0 and <= 20 } powerMode
                    && Number(message, "displayMinutes") is { } displayMinutes
                    && Flag(message, "scheduleEnabled") is { } scheduleEnabled
                    && Text(message, "sleepTime") is { Length: 5 } sleepTime
                    && Text(message, "wakeTime") is { Length: 5 } wakeTime
                    && Decimal(message, "watts") is { } watts
                    && Decimal(message, "ratePerKwh") is { } rate:
                actions.GameReadyPowerModeApply(powerMode, displayMinutes, scheduleEnabled, sleepTime, wakeTime, watts, rate);
                return true;
            case "gameReadyAmbientApply"
                when Flag(message, "enabled") is { } ambientEnabled
                    && Number(message, "idleMinutes") is { } ambientMinutes
                    && Flag(message, "showHostStatus") is { } showHostStatus
                    && Flag(message, "showRunningVms") is { } showRunningVms
                    && Flag(message, "showDevices") is { } showDevices
                    && Flag(message, "softLock") is { } softLock
                    && Text(message, "chord") is { Length: > 0 and <= 40 } chord:
                actions.GameReadyAmbientApply(ambientEnabled, ambientMinutes, showHostStatus, showRunningVms, showDevices, softLock, chord);
                return true;
            case "gameReadyTailscaleSettings" when Number(message, "localApiPort") is { } tailscalePort:
                actions.GameReadyTailscaleSettings(tailscalePort, Text(message, "controlUrl") is { Length: > 0 and <= 300 } controlUrl ? controlUrl : null);
                return true;
            case "gameReadySleepNow":
                actions.GameReadySleepNow();
                return true;
            case "gameAwareApply" when GameAwareSettingsOf(message) is { } gameAware:
                actions.GameAwareApply(gameAware);
                return true;
            case "restoreWizardAttach" when RestoreFormOf(message) is { } form:
                actions.RestoreWizardAttach(form);
                return true;
            case "restoreWizardRestore" when Text(message, "kind") is "host" or "vm" && Text(message, "snapshotId") is { } snapshotId:
                actions.RestoreWizardRestore(Text(message, "kind")!, snapshotId, Text(message, "name"), message.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.True);
                return true;
            case "restoreWizardReset":
                actions.RestoreWizardReset();
                return true;
            case "backupRunNow":
                actions.BackupRunNow();
                return true;
            case "gameAwareNotAGame":
                actions.GameAwareNotAGame();
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

    private static RestoreWizardForm? RestoreFormOf(JsonElement message)
    {
        if (!message.TryGetProperty("form", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var form = value.Deserialize<RestoreWizardForm>(Shared.Contracts.ContractJson.Options);
            return form is { Kind: "local" or "s3" or "azure", RecoveryKey.Length: > 0 } ? form : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Shared.Contracts.Hosts.GameAwareSettings? GameAwareSettingsOf(JsonElement message)
    {
        if (!message.TryGetProperty("settings", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return value.Deserialize<Shared.Contracts.Hosts.GameAwareSettings>(Shared.Contracts.ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Flag(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) ? value.GetBoolean() : null;

    private static int? Number(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? Decimal(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

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
