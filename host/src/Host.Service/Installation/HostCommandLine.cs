using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Service.VmConsole;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>What HyperHarbor.Host.exe was started to do.</summary>
internal enum HostMode
{
    /// <summary>Run the host: as a Windows service, or in a console window (portable and development runs).</summary>
    Host,

    /// <summary>The notification area app.</summary>
    Tray,

    /// <summary>Double-clicked: install, update, or open the tray.</summary>
    Launcher,

    Install,
    Uninstall,
    ListVms,
    ApplyWakeFixes,
    ConsoleSetup,

    /// <summary>--setup-package-search [result file]: installs PowerShell 7 and the WinGet module (elevated, from the tray).</summary>
    PackageSearchSetup,
    SaveWakeDiagnostics,

    /// <summary>--self-test &lt;data copy&gt; &lt;result file&gt;: the check a new version passes before it is installed.</summary>
    SelfTest,

    /// <summary>update-run: the update helper, started as SYSTEM by the "HyperHarbor\Update" task.</summary>
    UpdateRun,

    /// <summary>write-update-manifest: latest.json for a release of this executable (release tooling).</summary>
    WriteUpdateManifest,
    Help,
}

/// <summary>Chooses the mode from the command line. A Windows service is always <see cref="HostMode.Host"/>.</summary>
internal static class HostCommandLine
{
    public const string TraySwitch = "--tray";
    public const string RunVerb = "run";
    public const string InstallVerb = "install";
    public const string UninstallVerb = "uninstall";
    public const string SaveWakeDiagnosticsVerb = "save-wake-diagnostics";
    public const string UpdateRunVerb = "update-run";
    public const string WriteUpdateManifestVerb = "write-update-manifest";

    public const string Usage =
        """
        HyperHarbor host

          HyperHarbor.Host.exe                    Install, update, or open the tray (from Explorer)
          HyperHarbor.Host.exe install [--port N] Install as the HyperHarborHost service (asks for elevation)
          HyperHarbor.Host.exe uninstall [--remove-data]
                                                  Remove the service; --remove-data also deletes %ProgramData%\HyperHarbor
          HyperHarbor.Host.exe run [settings]     Run the host in a console window without installing it
          HyperHarbor.Host.exe --tray             Run the tray
          HyperHarbor.Host.exe --list-vms         Print the VM inventory as JSON
          HyperHarbor.Host.exe save-wake-diagnostics [folder]
                                                  Write Diagnose-Wake.ps1, which collects what Wake-on-LAN checks cannot see

        Settings use the configuration syntax, for example --Api:Port=48444.
        """;

    /// <param name="startedFromConsole">
    /// True when started from a terminal or a script (a parent console, or redirected output), false when
    /// started from Explorer. Without arguments, a terminal runs the host and Explorer opens the launcher.
    /// </param>
    /// <returns>The mode, and the arguments that remain for it.</returns>
    public static (HostMode Mode, string[] Arguments) Parse(string[] args, bool startedFromConsole)
    {
        if (args.Length == 0)
        {
            return (startedFromConsole ? HostMode.Host : HostMode.Launcher, args);
        }

        var rest = args[1..];
        return args[0] switch
        {
            TraySwitch => (HostMode.Tray, rest),
            RunVerb => (HostMode.Host, rest),
            InstallVerb => (HostMode.Install, rest),
            UninstallVerb => (HostMode.Uninstall, rest),
            SaveWakeDiagnosticsVerb => (HostMode.SaveWakeDiagnostics, rest),
            UpdateRunVerb => (HostMode.UpdateRun, rest),
            WriteUpdateManifestVerb => (HostMode.WriteUpdateManifest, rest),
            "help" or "--help" or "-h" or "/?" => (HostMode.Help, rest),
            WakeFixHelper.Switch when args.Length is 2 or 3 => (HostMode.ApplyWakeFixes, args),
            SelfTestGate.Switch when args.Length == 3 => (HostMode.SelfTest, args),
            _ when ConsoleSetupCommand.Matches(args) => (HostMode.ConsoleSetup, args),
            _ when PackageSearchSetupCommand.Matches(args) => (HostMode.PackageSearchSetup, args),
            _ when args.Contains(ListVmsCommand.Switch, StringComparer.OrdinalIgnoreCase) => (HostMode.ListVms, args),
            _ => (HostMode.Host, args),
        };
    }

    /// <summary>Modes that print to the terminal they were started from.</summary>
    public static bool WritesToConsole(HostMode mode) =>
        mode is HostMode.Install or HostMode.Uninstall or HostMode.ListVms or HostMode.SaveWakeDiagnostics or HostMode.Help or HostMode.WriteUpdateManifest;
}
