using System.Diagnostics;
using System.Security.Principal;
using HyperHarbor.Host.Core.Installation;
using Microsoft.Win32;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>Starts the tray at sign-in for the user who installed the host, from the stable current path.</summary>
internal static class TrayStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HyperHarbor Tray";

    /// <summary>Registers the tray for the current user (run unelevated, as that user).</summary>
    public static void Register(InstallLayout layout)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{layout.CurrentExecutable}\" {HostCommandLine.TraySwitch}");
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Removes the entry from another user's registry while they are signed in (elevated uninstall).</summary>
    public static void Unregister(SecurityIdentifier user)
    {
        using var key = Registry.Users.OpenSubKey($@"{user.Value}\{RunKey}", writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Starts the tray, or brings the running tray's window forward.</summary>
    public static void Start(InstallLayout layout)
    {
        // Shell execute, so the tray inherits no handles: it would otherwise hold the installer's redirected
        // output open, and a script waiting for "install" to finish would wait until the tray exits.
        var start = new ProcessStartInfo(layout.CurrentExecutable) { UseShellExecute = true, WorkingDirectory = layout.Current };
        start.ArgumentList.Add(HostCommandLine.TraySwitch);
        using var _ = Process.Start(start);
    }
}
