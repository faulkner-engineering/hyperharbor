using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.VmConsole;

/// <summary>
/// The elevated console account helper:
/// HyperHarbor.Host.exe --setup-console|--remove-console [data directory] [result file].
/// The tray starts it with a UAC prompt, and the installer runs it in process. An elevated process started with
/// ShellExecute cannot share the caller's console, so a one-line summary is written to the optional
/// result file. Exit code 0 means success.
/// </summary>
internal static class ConsoleSetupCommand
{
    public const int ExitSucceeded = 0;
    public const int ExitFailed = 1;
    public const int ExitNotElevated = 2;

    public static bool Matches(string[] args) =>
        args.Length is >= 1 and <= 3 && args[0] is ConsoleSetupHelper.SetupSwitch or ConsoleSetupHelper.RemoveSwitch;

    public static async Task<int> RunAsync(string[] args)
    {
        var dataDirectory = args.Length >= 2 && args[1].Length > 0 ? args[1] : HostIdentityStore.DefaultDataDirectory;
        var resultFile = args.Length == 3 ? args[2] : null;

        if (!Environment.IsPrivilegedProcess)
        {
            return await ReportAsync(resultFile, ExitNotElevated, "Setting up console access requires administrator rights.");
        }

        var setup = new ConsoleAccountSetup(
            new UserStore(dataDirectory),
            new ConsoleAccountStore(dataDirectory),
            new WindowsLocalAccounts(),
            new CimConsoleAccess());
        try
        {
            if (args[0] == ConsoleSetupHelper.SetupSwitch)
            {
                var results = setup.SetUp();
                return await ReportAsync(resultFile, ExitSucceeded, string.Join(" ", results.Select(result =>
                    $"{(result.Created ? "Created" : "Reset")} the console account {result.AccountName} for {result.UserName}.")));
            }

            var removed = await setup.RemoveAsync(CancellationToken.None);
            return await ReportAsync(resultFile, ExitSucceeded, removed.Count == 0
                ? "There were no console accounts to remove."
                : $"Removed {string.Join(", ", removed)}.");
        }
        catch (Exception ex)
        {
            return await ReportAsync(resultFile, ExitFailed, ex.Message);
        }
    }

    private static async Task<int> ReportAsync(string? resultFile, int exitCode, string message)
    {
        (exitCode == ExitSucceeded ? Console.Out : Console.Error).WriteLine(message);
        if (resultFile is not null)
        {
            await File.WriteAllTextAsync(resultFile, message);
        }

        return exitCode;
    }
}
