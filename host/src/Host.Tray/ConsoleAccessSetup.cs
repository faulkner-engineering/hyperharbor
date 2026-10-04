using System.ComponentModel;
using System.Diagnostics;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Runs the host service executable elevated in its console account mode, which creates (or resets)
/// the standard local account that paired devices use to open VM consoles.
/// </summary>
internal static class ConsoleAccessSetup
{
    // ERROR_CANCELLED: the user declined the UAC prompt.
    private const int ErrorCancelled = 1223;

    /// <summary>True when the setup helper has written console credentials into the data directory.</summary>
    public static bool IsSetUp(string dataDirectory) => File.Exists(Path.Combine(dataDirectory, ConsoleSetupHelper.AccountsFileName));

    /// <returns>Whether the helper succeeded, and what to tell the user.</returns>
    public static async Task<(bool Succeeded, string Message)> RunAsync(string? serviceExecutable, string dataDirectory)
    {
        if (serviceExecutable is null || !File.Exists(serviceExecutable))
        {
            return (false, "The host service executable could not be located.");
        }

        // The elevated helper writes a summary here, since its output cannot be captured.
        var resultFile = Path.Combine(Path.GetTempPath(), $"hyperharbor-console-setup-{Guid.NewGuid():N}.txt");
        var start = new ProcessStartInfo(serviceExecutable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(ConsoleSetupHelper.SetupSwitch);
        start.ArgumentList.Add(dataDirectory);
        start.ArgumentList.Add(resultFile);

        try
        {
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            var summary = File.Exists(resultFile) ? (await File.ReadAllTextAsync(resultFile)).Trim() : null;
            return process.ExitCode == 0
                ? (true, summary ?? "Console access is set up.")
                : (false, summary ?? $"The setup helper exited with code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (false, "The administrator prompt was cancelled.");
        }
        finally
        {
            File.Delete(resultFile);
        }
    }
}
