using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// HyperHarbor.Host.exe install|uninstall. The command runs as the signed-in user, starts itself elevated
/// (one UAC prompt) for the machine-wide steps in <see cref="HostInstaller"/>, and then does the per-user
/// steps: the tray's sign-in entry and starting the tray. The elevated copy reports progress through a
/// result file, one line per step, ending with <see cref="Succeeded"/> or <see cref="FailedPrefix"/>.
/// </summary>
internal static class InstallCommand
{
    public const int ExitSucceeded = 0;
    public const int ExitFailed = 1;
    public const int ExitCancelled = 2;

    private const string Succeeded = "OK";
    private const string FailedPrefix = "FAILED: ";
    private const int ErrorCancelled = 1223;

    public static async Task<int> InstallAsync(string[] args, IInstallUi ui)
    {
        var options = Options.Parse(args);
        var layout = new InstallLayout(options.Root ?? UninstallEntry.FindInstalled()?.Root ?? InstallLayout.DefaultRoot);
        var version = HostVersion.Current;

        if (options.Elevated)
        {
            return await RunElevatedPartAsync(options.ResultFile!, progress =>
                new HostInstaller(layout, progress).InstallAsync(
                    version,
                    options.Port,
                    TrayUser.Parse(options.TrayUser ?? throw new ArgumentException("The elevated installer needs --tray-user.")),
                    options.ParentProcessId));
        }

        var installed = layout.CurrentVersion;
        var (heading, text, action, progressHeading) = installed switch
        {
            null => ($"Install HyperHarbor Host {version}?",
                $"HyperHarbor will be installed in {layout.Root} and run as a Windows service that starts with Windows. " +
                $"It adds a firewall rule for TCP {options.Port} on Private networks and a local account that paired devices use for VM consoles. " +
                "The tray starts when you sign in.",
                "Install",
                $"Installing HyperHarbor {version}"),
            _ when installed == version => ($"Reinstall HyperHarbor Host {version}?",
                "The service restarts. Pairings and settings are kept.",
                "Reinstall",
                $"Reinstalling HyperHarbor {version}"),
            _ when installed < version => ($"Update HyperHarbor Host from {installed} to {version}?",
                $"The service restarts on the new version. Version {installed} stays on disk. Pairings and settings are kept.",
                "Update",
                $"Updating HyperHarbor to {version}"),
            _ => ($"Replace HyperHarbor Host {installed} with the older {version}?",
                $"Version {installed} stays on disk. An older version may not read data a newer one wrote.",
                "Replace",
                $"Installing HyperHarbor {version}"),
        };
        if (!options.Quiet && !ui.Confirm(heading, text, action))
        {
            return ExitCancelled;
        }

        var identity = WindowsIdentity.GetCurrent().User!;
        var arguments = new List<string> { HostCommandLine.InstallVerb, "--tray-user", identity.Value, "--port", options.Port.ToString(CultureInfo.InvariantCulture) };
        if (options.Root is not null)
        {
            arguments.AddRange(["--root", options.Root]);
        }

        var (succeeded, message) = await ui.RunAsync(progressHeading, progress => RunElevatedAsync(arguments, progress));
        if (!succeeded)
        {
            ui.Report("HyperHarbor was not installed", message, succeeded: false);
            return message == CancelledMessage ? ExitCancelled : ExitFailed;
        }

        TrayStartup.Register(layout);
        TrayStartup.Start(layout);
        ui.Report(
            $"HyperHarbor {version} is installed",
            $"The service runs from {layout.Current}. Pair a device from the HyperHarbor client; the PIN appears from the tray icon.",
            succeeded: true);
        return ExitSucceeded;
    }

    public static async Task<int> UninstallAsync(string[] args, IInstallUi ui)
    {
        var options = Options.Parse(args);
        var layout = new InstallLayout(options.Root ?? UninstallEntry.FindInstalled()?.Root ?? InstallLayout.DefaultRoot);

        if (options.Elevated)
        {
            return await RunElevatedPartAsync(options.ResultFile!, progress =>
                new HostInstaller(layout, progress).UninstallAsync(options.RemoveData, options.ParentProcessId));
        }

        // The installed executable cannot delete its own folder, so a temporary copy does the work.
        var self = Environment.ProcessPath!;
        if (layout.Contains(self))
        {
            var copy = Path.Combine(Path.GetTempPath(), $"HyperHarbor.Host-uninstall-{Guid.NewGuid():N}.exe");
            File.Copy(self, copy);
            var start = new ProcessStartInfo(copy) { UseShellExecute = false };
            foreach (var argument in args.Prepend(HostCommandLine.UninstallVerb))
            {
                start.ArgumentList.Add(argument);
            }

            using var _ = Process.Start(start);
            return ExitSucceeded;
        }

        var text = options.RemoveData
            ? "The service, the console account, the firewall rule, and all HyperHarbor data (pairings, settings, the audit trail) will be removed."
            : "The service, the console account, and the firewall rule will be removed. Pairings and settings stay in %ProgramData%\\HyperHarbor.";
        if (!options.Quiet && !ui.Confirm("Remove HyperHarbor Host?", text, "Remove"))
        {
            return ExitCancelled;
        }

        var arguments = new List<string> { HostCommandLine.UninstallVerb, "--root", layout.Root };
        if (options.RemoveData)
        {
            arguments.Add("--remove-data");
        }

        var (succeeded, message) = await ui.RunAsync("Removing HyperHarbor", progress => RunElevatedAsync(arguments, progress));
        if (succeeded)
        {
            TrayStartup.Unregister();
        }

        ui.Report(succeeded ? "HyperHarbor is removed" : "HyperHarbor was not fully removed", succeeded ? string.Empty : message, succeeded);
        return succeeded ? ExitSucceeded : message == CancelledMessage ? ExitCancelled : ExitFailed;
    }

    private const string CancelledMessage = "Administrator permission was not given.";

    /// <summary>Starts this executable elevated with <paramref name="arguments"/> and relays its progress lines.</summary>
    private static async Task<(bool Succeeded, string Message)> RunElevatedAsync(List<string> arguments, IProgress<string> progress)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), $"hyperharbor-install-{Guid.NewGuid():N}.txt");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in arguments.Concat(["--elevated", "--result-file", resultFile, "--parent-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)]))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return (false, CancelledMessage);
            }

            using (process)
            {
                var relayed = 0;
                string? last = null;
                while (true)
                {
                    var exited = process!.HasExited;
                    foreach (var line in ReadLines(resultFile).Skip(relayed))
                    {
                        relayed++;
                        last = line;
                        if (line != Succeeded && !line.StartsWith(FailedPrefix, StringComparison.Ordinal))
                        {
                            progress.Report(line);
                        }
                    }

                    if (exited)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                }

                return last switch
                {
                    Succeeded => (true, string.Empty),
                    { } failed when failed.StartsWith(FailedPrefix, StringComparison.Ordinal) => (false, failed[FailedPrefix.Length..]),
                    _ => (false, "The elevated installer stopped without reporting a result."),
                };
            }
        }
        finally
        {
            File.Delete(resultFile);
        }
    }

    /// <summary>The elevated copy: runs the steps and writes each progress line, then the outcome, to the result file.</summary>
    private static async Task<int> RunElevatedPartAsync(string resultFile, Func<IProgress<string>, Task> steps)
    {
        void Append(string line) => File.AppendAllLines(resultFile, [line.ReplaceLineEndings(" ")]);

        try
        {
            await steps(new FileProgress(Append));
            Append(Succeeded);
            return ExitSucceeded;
        }
        catch (Exception ex)
        {
            Append(FailedPrefix + ex.Message);
            return ExitFailed;
        }
    }

    private static string[] ReadLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException)
        {
            return [];
        }
    }

    private sealed class FileProgress(Action<string> append) : IProgress<string>
    {
        public void Report(string value) => append(value);
    }

    /// <summary>
    /// Command-line options. Public: --port, --root, --quiet, --remove-data. Internal (the elevated copy):
    /// --elevated, --tray-user, --result-file, --parent-pid.
    /// </summary>
    internal sealed record Options(
        int Port = HostInstaller.DefaultPort,
        string? Root = null,
        bool Quiet = false,
        bool RemoveData = false,
        bool Elevated = false,
        string? TrayUser = null,
        string? ResultFile = null,
        int? ParentProcessId = null)
    {
        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                options = args[i] switch
                {
                    "--port" => options with { Port = int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : throw new ArgumentException("--port needs a port number.") },
                    "--root" => options with { Root = Path.GetFullPath(Value()) },
                    "--quiet" => options with { Quiet = true },
                    "--remove-data" => options with { RemoveData = true },
                    "--elevated" => options with { Elevated = true },
                    "--tray-user" => options with { TrayUser = Value() },
                    "--result-file" => options with { ResultFile = Value() },
                    "--parent-pid" => options with { ParentProcessId = int.Parse(Value(), CultureInfo.InvariantCulture) },
                    var unknown => throw new ArgumentException($"Unknown option {unknown}. Run HyperHarbor.Host.exe --help."),
                };
            }

            if (options.Elevated && options.ResultFile is null)
            {
                throw new ArgumentException("The elevated installer needs --result-file.");
            }

            return options;
        }
    }
}
