using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.GameReady.ConsoleSignIn;

/// <summary>
/// Sets up and removes the Console sign-in: a standard local user that Windows signs in automatically, with a launcher
/// that starts at sign-in. Setup undoes every step it finished when a later step fails. Disable continues past a failed
/// step, reports it, and keeps the saved record so it can be run again.
/// </summary>
public sealed class ConsoleSignInService(
    IConsoleSignInSystem system,
    IConsoleSignInStore store,
    ILogger<ConsoleSignInService> logger)
{
    public const string AccountComment = "Created by HyperHarbor for local play. Signs in automatically.";

    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!#$%&()*+-<=>?@^_~";

    public ConsoleSignInReading Read(string? userName = null, string? launcher = null)
    {
        var record = store.Load();
        var name = record?.UserName ?? (string.IsNullOrWhiteSpace(userName) ? ConsoleUserPolicy.DefaultUserName : userName.Trim());
        var chosen = record?.Launcher ?? (launcher is not null && ConsoleLaunchers.IsKnown(launcher) ? launcher : ConsoleLaunchers.SteamBigPicture);

        var exists = ConsoleUserPolicy.ValidateName(name) is null && system.UserExists(name);
        var standard = exists && !ConsoleUserPolicy.IsPrivileged(system.GroupsOf(name));
        var winlogon = system.ReadWinlogon();
        var autoSignIn = record is not null
            && winlogon.AutoSignInOn
            && string.Equals(winlogon.DefaultUserName, name, StringComparison.OrdinalIgnoreCase)
            && system.HasAutoSignInPassword();
        var runValues = exists ? system.ReadRunValueNames(name) : [];
        var launcherSet = record is not null && record.RunValueNames.Count > 0 && record.RunValueNames.All(value => runValues.Contains(value, StringComparer.OrdinalIgnoreCase));

        return new ConsoleSignInReading(
            name,
            chosen,
            record is not null,
            exists,
            standard,
            autoSignIn,
            launcherSet,
            system.HasPlaintextPassword(),
            system.InspectAdministrators(),
            Launchers());
    }

    public IReadOnlyList<LauncherChoice> Launchers() =>
        ConsoleLaunchers.All.Select(id =>
        {
            var (plan, reason) = system.PlanLauncher(id);
            return new LauncherChoice(id, LauncherName(id), plan is not null, reason);
        }).ToList();

    public static string LauncherName(string launcher) => launcher == ConsoleLaunchers.Playnite ? "Playnite (full screen)" : "Steam Big Picture";

    /// <exception cref="ConsoleSignInException">Nothing was changed, or every change was undone.</exception>
    public void Enable(string userName, string launcher)
    {
        userName = userName?.Trim() ?? string.Empty;
        if (ConsoleUserPolicy.ValidateName(userName) is { } invalid)
        {
            throw new ConsoleSignInException(invalid);
        }

        if (!ConsoleLaunchers.IsKnown(launcher))
        {
            throw new ConsoleSignInException("Choose Steam Big Picture or Playnite.");
        }

        var existing = store.Load();
        if (existing is not null && !string.Equals(existing.UserName, userName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConsoleSignInException($"The Console sign-in is already set up for {existing.UserName}. Disable it first to use another name.");
        }

        var (plan, reason) = system.PlanLauncher(launcher);
        if (plan is null)
        {
            throw new ConsoleSignInException(reason ?? "That launcher is not installed.");
        }

        var winlogon = system.ReadWinlogon();
        var exists = system.UserExists(userName);
        if (existing is null)
        {
            if (winlogon.AutoSignInOn || system.HasPlaintextPassword())
            {
                throw new ConsoleSignInException(
                    $"Automatic sign-in is already set up on this PC for {winlogon.DefaultUserName ?? "another account"}. Turn that off first (for example with Sysinternals Autologon), so HyperHarbor does not replace it.");
            }

            if (exists)
            {
                throw new ConsoleSignInException($"An account named {userName} already exists and was not created by HyperHarbor. Choose another name.");
            }
        }

        var createUser = !exists;
        var record = new ConsoleSignInRecord(
            userName,
            existing?.Password ?? GeneratePassword(),
            launcher,
            existing?.CreatedUser ?? createUser,
            existing?.PreviousWinlogon ?? winlogon,
            existing?.PreviousConsoleLock ?? system.ReadConsoleLock(),
            plan.Values.Keys.ToList(),
            DateTimeOffset.UtcNow);

        var undo = new Stack<(string Step, Action Action)>();
        try
        {
            // The record goes first, so an interrupted setup still has the password and the earlier values to go back to.
            store.Save(record);
            undo.Push(("remove the saved record", () => { if (existing is null) { store.Delete(); } else { store.Save(existing); } }));

            if (createUser)
            {
                system.CreateUser(userName, record.Password, AccountComment);
                undo.Push(("delete the account", () => system.DeleteUser(userName, deleteProfile: true)));
            }

            // A standard user: never an administrator, and never a member of a group that reads or changes the host.
            var privileged = ConsoleUserPolicy.PrivilegedMemberships(system.GroupsOf(userName)).ToList();
            if (privileged.Count > 0)
            {
                throw new ConsoleSignInException($"{userName} is a member of a privileged group ({string.Join(", ", privileged)}). The Console user must be a standard user.");
            }

            system.CreateProfile(userName);

            var previousRuns = system.ReadRunValueNames(userName);
            system.ConfigureUserHive(userName, plan);
            undo.Push(("remove the launcher entries", () => system.RemoveRunValues(userName, plan.Values.Keys.Where(name => !previousRuns.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList())));

            system.WriteConsoleLock(new ConsoleLockSetting(0, 0));
            undo.Push(("restore the sign-in requirement after sleep", () => system.WriteConsoleLock(record.PreviousConsoleLock)));

            system.StoreAutoSignInPassword(record.Password);
            undo.Push(("delete the automatic sign-in password", () => system.StoreAutoSignInPassword(null)));

            system.WriteWinlogon(new WinlogonSettings("1", userName, Environment.MachineName));
            undo.Push(("restore the Winlogon values", () => system.WriteWinlogon(record.PreviousWinlogon)));

            system.RemovePlaintextPassword();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Setting up the Console sign-in failed; undoing {Count} step(s).", undo.Count);
            var problems = Unwind(undo);
            var message = ex is ConsoleSignInException ? ex.Message : $"Setting up the Console sign-in failed: {ex.Message}";
            throw new ConsoleSignInException(problems.Count == 0 ? message : $"{message} Some changes could not be undone: {string.Join("; ", problems)}.");
        }

        logger.LogInformation("The Console sign-in is set up for {UserName} with {Launcher}.", userName, launcher);
    }

    /// <summary>Turns automatic sign-in off and restores what setup changed. The user and profile go only when asked.</summary>
    /// <exception cref="ConsoleSignInException">Nothing was set up, or a step failed (the record is kept, so it can be run again).</exception>
    public void Disable(bool deleteUser)
    {
        var record = store.Load() ?? throw new ConsoleSignInException("HyperHarbor has not set up a Console sign-in on this PC.");
        var problems = new List<string>();

        Attempt(problems, "turn off automatic sign-in", () => system.WriteWinlogon(record.PreviousWinlogon));
        Attempt(problems, "delete the automatic sign-in password", () => system.StoreAutoSignInPassword(null));
        Attempt(problems, "remove the plaintext password value", system.RemovePlaintextPassword);
        Attempt(problems, "restore the sign-in requirement after sleep", () => system.WriteConsoleLock(record.PreviousConsoleLock));

        if (system.UserExists(record.UserName))
        {
            if (deleteUser && record.CreatedUser)
            {
                Attempt(problems, "delete the account and its profile", () => system.DeleteUser(record.UserName, deleteProfile: true));
            }
            else
            {
                Attempt(problems, "remove the launcher entries", () => system.RemoveRunValues(record.UserName, record.RunValueNames));
            }
        }

        if (problems.Count > 0)
        {
            throw new ConsoleSignInException($"Some steps failed: {string.Join("; ", problems)}. Try again once they are resolved (a signed-in Console user keeps its profile in use).");
        }

        store.Delete();
        logger.LogInformation("The Console sign-in was turned off for {UserName}; the account was {Outcome}.", record.UserName, deleteUser && record.CreatedUser ? "deleted" : "kept");
    }

    private List<string> Unwind(Stack<(string Step, Action Action)> undo)
    {
        var problems = new List<string>();
        while (undo.TryPop(out var item))
        {
            Attempt(problems, item.Step, item.Action);
        }

        return problems;
    }

    private void Attempt(List<string> problems, string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not {Step}.", step);
            problems.Add($"could not {step} ({ex.Message})");
        }
    }

    /// <summary>A 32 character random password with all four character kinds. It is never shown.</summary>
    public static string GeneratePassword()
    {
        var all = Upper + Lower + Digits + Symbols;
        var characters = new List<char>
        {
            Pick(Upper),
            Pick(Lower),
            Pick(Digits),
            Pick(Symbols),
        };
        while (characters.Count < 32)
        {
            characters.Add(Pick(all));
        }

        // Shuffle, so the first four are not always one of each kind.
        for (var i = characters.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (characters[i], characters[j]) = (characters[j], characters[i]);
        }

        return new string(characters.ToArray());
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
