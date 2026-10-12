using System.Diagnostics;
using System.Reflection;
using HyperHarbor.Shared.Contracts.Hosts;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Restarts the tray after a host update. The update helper replaces the service, but the tray runs in the
/// user's session from the old version's folder, so it would keep showing the old window until it was closed
/// by hand. The tray notices that the service reports another version and starts the current copy instead.
/// </summary>
internal static class TrayRestart
{
    /// <summary>Argument that tells a new tray to wait for the old one to release the single-instance mutex.</summary>
    public const string Switch = "--restarted";

    private const string VersionsFolder = "versions";
    private const string CurrentFolder = "current";
    private const string ExecutableName = "HyperHarbor.Host.exe";

    /// <summary>This tray's version, without build metadata, in the form the service reports.</summary>
    public static string OwnVersion { get; } =
        (typeof(TrayRestart).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>
    /// True when the installed service runs another version than this tray. A host that is not installed reports
    /// no support for updates and never triggers a restart.
    /// </summary>
    public static bool IsStale(HostUpdateStatus status, string ownVersion) =>
        status.Supported
        && !string.IsNullOrWhiteSpace(status.CurrentVersion)
        && !string.Equals(status.CurrentVersion, ownVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The executable the current junction points at, when <paramref name="processPath"/> is in
    /// root\versions\&lt;version&gt;. Null for any other layout (a portable or development run), which is never restarted.
    /// </summary>
    public static string? CurrentExecutable(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        var version = Path.GetDirectoryName(processPath);
        var versions = version is null ? null : Path.GetDirectoryName(version);
        if (versions is null || !string.Equals(Path.GetFileName(versions), VersionsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var root = Path.GetDirectoryName(versions);
        return root is null ? null : Path.Combine(root, CurrentFolder, ExecutableName);
    }

    /// <summary>Starts the current tray. Returns false when there is nothing to start or it could not be started.</summary>
    public static bool TryStartCurrent()
    {
        var executable = CurrentExecutable(Environment.ProcessPath);
        if (executable is null || !File.Exists(executable))
        {
            return false;
        }

        try
        {
            // Shell execute, so the new tray inherits no handles from this one.
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.ArgumentList.Add("--tray");
            start.ArgumentList.Add(Switch);
            using var _ = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
