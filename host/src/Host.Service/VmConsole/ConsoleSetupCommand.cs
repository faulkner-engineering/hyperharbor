using System.Security.Principal;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Host.Service.Installation;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.VmConsole;

/// <summary>
/// The elevated console account helper:
/// HyperHarbor.Host.exe --setup-console|--remove-console [--for-user=SID] [data directory] [result file].
/// The tray starts it with a UAC prompt, and the installer runs it in process. An elevated process started with
/// ShellExecute cannot share the caller's console, so a one-line summary is written to the optional
/// result file. Exit code 0 means success.
/// </summary>
/// <remarks>
/// --for-user names the account a portable host runs as. The helper runs as whichever administrator approved the
/// UAC prompt, so without it a standard user's files would belong to that administrator and lock the host out.
/// With it, the files the helper writes give that account full control, and setup first restores its access to
/// the files already in the data directory. The installed service runs as LocalSystem, so the option is refused
/// while it is installed: it would give the tray user full control of the service's secrets.
/// </remarks>
internal static class ConsoleSetupCommand
{
    public const int ExitSucceeded = 0;
    public const int ExitFailed = 1;
    public const int ExitNotElevated = 2;
    public const string ForUserOption = ConsoleSetupHelper.ForUserOption;

    /// <param name="ForUser">The portable host's account, or null to write files as the current account.</param>
    internal sealed record Arguments(bool SetUp, SecurityIdentifier? ForUser, string DataDirectory, string? ResultFile);

    /// <summary>The helper's switch comes first; RunAsync rejects the rest if it is malformed, rather than starting a host.</summary>
    public static bool Matches(string[] args) =>
        args.Length >= 1 && args[0] is ConsoleSetupHelper.SetupSwitch or ConsoleSetupHelper.RemoveSwitch;

    /// <returns>Null when the arguments are malformed.</returns>
    internal static Arguments? Parse(string[] args)
    {
        if (!Matches(args))
        {
            return null;
        }

        SecurityIdentifier? forUser = null;
        var positional = new List<string>();
        foreach (var argument in args.Skip(1))
        {
            if (argument.StartsWith(ForUserOption, StringComparison.OrdinalIgnoreCase))
            {
                if (forUser is not null || !TryParseSid(argument[ForUserOption.Length..], out forUser))
                {
                    return null;
                }
            }
            else
            {
                positional.Add(argument);
            }
        }

        if (positional.Count > 2)
        {
            return null;
        }

        var dataDirectory = positional.Count >= 1 && positional[0].Length > 0 ? positional[0] : HostIdentityStore.DefaultDataDirectory;
        var resultFile = positional.Count == 2 ? positional[1] : null;
        return new Arguments(args[0] == ConsoleSetupHelper.SetupSwitch, forUser, dataDirectory, resultFile);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (Parse(args) is not { } arguments)
        {
            return await ReportAsync(null, ExitFailed, $"Usage: {ConsoleSetupHelper.SetupSwitch}|{ConsoleSetupHelper.RemoveSwitch} [{ForUserOption}SID] [data directory] [result file]");
        }

        var resultFile = arguments.ResultFile;

        if (!Environment.IsPrivilegedProcess)
        {
            return await ReportAsync(resultFile, ExitNotElevated, "Setting up console access requires administrator rights.");
        }

        if (arguments.ForUser is not null && ServiceRegistration.Exists(HostService.Name))
        {
            return await ReportAsync(resultFile, ExitFailed, "The HyperHarbor service is installed, so its files must not be given to another account.");
        }

        var setup = new ConsoleAccountSetup(
            new UserStore(arguments.DataDirectory, hostAccount: arguments.ForUser),
            new ConsoleAccountStore(arguments.DataDirectory, arguments.ForUser),
            new WindowsLocalAccounts(),
            new CimConsoleAccess());
        try
        {
            if (arguments.SetUp)
            {
                var repaired = arguments.ForUser is null ? [] : DataDirectoryAccess.GrantHostAccount(arguments.DataDirectory, arguments.ForUser);
                var results = setup.SetUp();
                var summary = string.Join(" ", results.Select(result =>
                    $"{(result.Created ? "Created" : "Reset")} the console account {result.AccountName} for {result.UserName}."));
                if (repaired.Count > 0)
                {
                    summary = $"Restored the host's access to {repaired.Count} {(repaired.Count == 1 ? "file" : "files")} in {arguments.DataDirectory}. {summary}";
                }

                return await ReportAsync(resultFile, ExitSucceeded, summary);
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

    private static bool TryParseSid(string value, out SecurityIdentifier? sid)
    {
        try
        {
            sid = new SecurityIdentifier(value);
            return true;
        }
        catch (ArgumentException)
        {
            sid = null;
            return false;
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
