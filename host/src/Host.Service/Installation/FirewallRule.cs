using System.Diagnostics;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>The inbound rule for the API port on Private networks, managed with netsh.</summary>
internal static class FirewallRule
{
    public const string Name = "HyperHarbor Host API";

    public static bool Exists() => Netsh("advfirewall", "firewall", "show", "rule", $"name={Name}") == 0;

    public static void Add(int port)
    {
        var exitCode = Netsh("advfirewall", "firewall", "add", "rule", $"name={Name}", "dir=in", "action=allow", "protocol=TCP", $"localport={port}", "profile=private");
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Adding the firewall rule \"{Name}\" failed (netsh exit code {exitCode}).");
        }
    }

    public static void Delete()
    {
        if (Exists())
        {
            Netsh("advfirewall", "firewall", "delete", "rule", $"name={Name}");
        }
    }

    private static int Netsh(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
