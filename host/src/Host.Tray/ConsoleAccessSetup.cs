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
    /// <summary>True when the setup helper has written console credentials into the data directory.</summary>
    public static bool IsSetUp(string dataDirectory) => File.Exists(Path.Combine(dataDirectory, ConsoleSetupHelper.AccountsFileName));

    /// <returns>Whether the helper succeeded, and what to tell the user.</returns>
    public static Task<(bool Succeeded, string Message)> RunAsync(string? serviceExecutable, string dataDirectory) =>
        ElevatedHelper.RunAsync(serviceExecutable, [ConsoleSetupHelper.SetupSwitch, dataDirectory], "Console access is set up.");
}

/// <summary>
/// Installs what package search needs on the host (PowerShell 7 and the WinGet PowerShell module, for all users)
/// with the elevated helper.
/// </summary>
internal static class PackageSearchSetup
{
    public static Task<(bool Succeeded, string Message)> RunAsync(string? serviceExecutable) =>
        ElevatedHelper.RunAsync(serviceExecutable, [PackageSearchSetupHelper.Switch], "Package search is set up.");
}

/// <summary>Starts the host executable elevated (one UAC prompt) with a helper switch and reads its one-line result.</summary>
internal static class ElevatedHelper
{
    // ERROR_CANCELLED: the user declined the UAC prompt.
    private const int ErrorCancelled = 1223;

    /// <param name="arguments">The helper switch and its arguments; the result file is added last.</param>
    public static async Task<(bool Succeeded, string Message)> RunAsync(string? serviceExecutable, IReadOnlyList<string> arguments, string successMessage)
    {
        if (serviceExecutable is null || !File.Exists(serviceExecutable))
        {
            return (false, "The host service executable could not be located.");
        }

        // The elevated helper writes a summary here, since its output cannot be captured.
        var resultFile = Path.Combine(Path.GetTempPath(), $"hyperharbor-helper-{Guid.NewGuid():N}.txt");
        var start = new ProcessStartInfo(serviceExecutable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(resultFile);

        try
        {
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            var summary = File.Exists(resultFile) ? (await File.ReadAllTextAsync(resultFile)).Trim() : null;
            return process.ExitCode == 0
                ? (true, summary ?? successMessage)
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
