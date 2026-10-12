using System.Runtime.InteropServices;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Win32;

namespace HyperHarbor.Host.Tray.Ambient;

/// <summary>The screensaver settings of the signed-in user.</summary>
/// <param name="PolicyLocked">Group Policy sets the screensaver; changing the user's own values would have no effect.</param>
internal sealed record ScreensaverSettings(bool Active, string? Path, int TimeoutSeconds, bool Secure, bool PolicyLocked);

internal interface IScreensaverSettings
{
    ScreensaverSettings Read();

    /// <summary>Writes the user's screensaver settings and tells Windows. A null path removes the screensaver.</summary>
    void Write(bool active, string? path, int timeoutSeconds, bool secure);
}

/// <summary>Keeps the user's own screensaver settings while HyperHarbor's replaces them.</summary>
internal interface IScreensaverBackup
{
    ScreensaverSettings? Load();

    void Save(ScreensaverSettings settings);

    void Clear();
}

/// <summary>
/// Makes the desktop's screensaver the ambient screen while the screen is on, and gives the user's own screensaver
/// back when it is off. The user's settings are saved first and restored only while the ambient screensaver is still the
/// one set (a screensaver the person chose since stays). Idempotent: the tray calls it on every status.
/// </summary>
internal sealed class AmbientScreensaverApplier(IScreensaverSettings settings, IScreensaverBackup backup)
{
    /// <summary>The screensaver state to report. <paramref name="path"/> is null when the ambient screen is off or not installed.</summary>
    public AmbientScreensaverState Sync(string? path, int timeoutSeconds)
    {
        var current = settings.Read();
        if (!current.PolicyLocked)
        {
            if (path is not null)
            {
                var desired = new ScreensaverSettings(true, path, timeoutSeconds, false, false);
                if (current != desired)
                {
                    if (!IsAmbient(current.Path) && backup.Load() is null)
                    {
                        backup.Save(current);
                    }

                    settings.Write(true, path, timeoutSeconds, false);
                    current = settings.Read();
                }
            }
            else if (backup.Load() is { } saved)
            {
                if (IsAmbient(current.Path))
                {
                    settings.Write(saved.Active, saved.Path, saved.TimeoutSeconds, saved.Secure);
                    current = settings.Read();
                }

                backup.Clear();
            }
            else if (IsAmbient(current.Path))
            {
                // Left behind without a backup (for example the backup file was lost): turn the screensaver off rather than keep ours.
                settings.Write(false, null, current.TimeoutSeconds, current.Secure);
                current = settings.Read();
            }
        }

        return new AmbientScreensaverState(current.Active, current.Path, current.TimeoutSeconds, current.PolicyLocked);
    }

    internal static bool IsAmbient(string? path) =>
        path is not null && string.Equals(Path.GetFileName(path), AmbientScreensaver.FileName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>HKCU\Control Panel\Desktop, applied with SystemParametersInfo so it takes effect without signing out.</summary>
internal sealed class WindowsScreensaverSettings : IScreensaverSettings
{
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string PolicyKey = @"Software\Policies\Microsoft\Windows\Control Panel\Desktop";
    private const uint SetScreenSaveTimeout = 0x000F;
    private const uint SetScreenSaveActive = 0x0011;
    private const uint UpdateIniFileAndNotify = 0x0003;

    public ScreensaverSettings Read()
    {
        using var desktop = Registry.CurrentUser.OpenSubKey(DesktopKey);
        var path = desktop?.GetValue("SCRNSAVE.EXE") as string;
        var active = Text(desktop, "ScreenSaveActive") == "1";
        var timeout = int.TryParse(Text(desktop, "ScreenSaveTimeOut"), out var seconds) ? seconds : 0;
        var secure = Text(desktop, "ScreenSaverIsSecure") == "1";

        using var policy = Registry.CurrentUser.OpenSubKey(PolicyKey);
        var locked = policy is not null && (policy.GetValue("SCRNSAVE.EXE") is not null || policy.GetValue("ScreenSaveActive") is not null || policy.GetValue("ScreenSaveTimeOut") is not null);
        return new ScreensaverSettings(active && !string.IsNullOrEmpty(path), string.IsNullOrEmpty(path) ? null : path, timeout, secure, locked);
    }

    public void Write(bool active, string? path, int timeoutSeconds, bool secure)
    {
        using (var desktop = Registry.CurrentUser.CreateSubKey(DesktopKey))
        {
            if (path is null)
            {
                desktop.DeleteValue("SCRNSAVE.EXE", throwOnMissingValue: false);
            }
            else
            {
                desktop.SetValue("SCRNSAVE.EXE", path, RegistryValueKind.String);
            }

            desktop.SetValue("ScreenSaverIsSecure", secure ? "1" : "0", RegistryValueKind.String);
        }

        _ = SystemParametersInfo(SetScreenSaveTimeout, (uint)Math.Max(timeoutSeconds, 0), IntPtr.Zero, UpdateIniFileAndNotify);
        _ = SystemParametersInfo(SetScreenSaveActive, active ? 1u : 0u, IntPtr.Zero, UpdateIniFileAndNotify);
    }

    private static string? Text(RegistryKey? key, string name) => key?.GetValue(name)?.ToString();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, IntPtr value, uint flags);
}

/// <summary>The saved settings as a JSON file in the ambient folder.</summary>
internal sealed class FileScreensaverBackup(string path) : IScreensaverBackup
{
    public ScreensaverSettings? Load() => AmbientFiles.Read<ScreensaverSettings>(path);

    public void Save(ScreensaverSettings settings) => AmbientFiles.Write(path, settings);

    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It is replaced the next time settings are saved.
        }
    }
}
