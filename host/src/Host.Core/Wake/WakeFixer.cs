using System.Diagnostics;
using Microsoft.Win32;

namespace HyperHarbor.Host.Core.Wake;

/// <param name="Applied">True when the commands for the check ran without error.</param>
public sealed record WakeFixResult(string CheckId, bool Applied, string Detail);

/// <summary>
/// Applies Wake-on-LAN fixes. Requires administrator rights; the host service runs this in a
/// separate elevated process (--apply-wake-fixes) after the user approves it in the tray.
/// </summary>
public static class WakeFixer
{
    private const string ConnectivityInStandbySubgroup = "fea3413e-7e05-4911-9a71-700331f1c294";
    private const string ConnectivityInStandbySetting = "f15576e8-98b7-4186-b944-eafa664402d9";
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    public static IReadOnlyList<WakeFixResult> Apply(WakeEnvironment environment, IEnumerable<string> checkIds)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var wired = WakeInfoBuilder.WiredAdapters(environment).ToList();
        var results = new List<WakeFixResult>();
        foreach (var checkId in checkIds.Distinct(StringComparer.Ordinal))
        {
            results.Add(checkId switch
            {
                WakeCheckIds.NicWakeOnMagicPacket => ForEachAdapter(checkId, wired.Where(a => a.WakeOnMagicPacket == false), adapter =>
                    RunPowerShell($"Set-NetAdapterPowerManagement -Name '{EscapeSingleQuotes(adapter.Name)}' -WakeOnMagicPacket Enabled")),
                WakeCheckIds.NicAllowWake => ForEachAdapter(checkId, wired.Where(a => !environment.WakeArmedDevices.Contains(a.Description)), adapter =>
                    Run("powercfg.exe", "/deviceenablewake", adapter.Description)),
                WakeCheckIds.SleepKeepsNetwork => Single(checkId, () =>
                {
                    Run("powercfg.exe", "/setacvalueindex", "SCHEME_CURRENT", ConnectivityInStandbySubgroup, ConnectivityInStandbySetting, "1");
                    Run("powercfg.exe", "/setactive", "SCHEME_CURRENT");
                    return "Enabled network connectivity in standby on AC power.";
                }),
                WakeCheckIds.FastStartupDisabled => Single(checkId, () =>
                {
                    using var key = Registry.LocalMachine.OpenSubKey(PowerKey, writable: true)
                        ?? throw new InvalidOperationException("The power settings registry key is missing.");
                    key.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
                    return "Turned off fast startup.";
                }),
                _ => new WakeFixResult(checkId, false, "This check cannot be fixed automatically."),
            });
        }

        return results;
    }

    private static WakeFixResult ForEachAdapter(string checkId, IEnumerable<NetworkAdapterState> adapters, Action<NetworkAdapterState> fix)
    {
        var targets = adapters.ToList();
        if (targets.Count == 0)
        {
            return new WakeFixResult(checkId, true, "Already configured.");
        }

        return Single(checkId, () =>
        {
            foreach (var adapter in targets)
            {
                fix(adapter);
            }

            return $"Updated {string.Join(", ", targets.Select(a => a.Name))}.";
        });
    }

    private static WakeFixResult Single(string checkId, Func<string> fix)
    {
        try
        {
            return new WakeFixResult(checkId, true, fix());
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new WakeFixResult(checkId, false, ex.Message);
        }
    }

    private static void RunPowerShell(string command) =>
        Run("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command);

    /// <summary>Runs a tool with arguments passed individually, so no argument is parsed as a command.</summary>
    private static void Run(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {output.Trim()}");
        }
    }

    private static string EscapeSingleQuotes(string value) => value.Replace("'", "''");
}
