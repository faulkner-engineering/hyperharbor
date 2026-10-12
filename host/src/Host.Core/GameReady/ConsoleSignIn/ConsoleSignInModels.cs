using System.Security.Principal;

namespace HyperHarbor.Host.Core.GameReady.ConsoleSignIn;

/// <summary>A launcher the Console user starts at sign-in. The values are the wire names used by the tray.</summary>
public static class ConsoleLaunchers
{
    public const string SteamBigPicture = "steamBigPicture";
    public const string Playnite = "playnite";

    public static IReadOnlyList<string> All { get; } = [SteamBigPicture, Playnite];

    public static bool IsKnown(string launcher) => All.Contains(launcher, StringComparer.Ordinal);
}

/// <summary>The command lines written to the Console user's Run key for a launcher.</summary>
/// <param name="Values">Run value name to command line, in start order.</param>
public sealed record LauncherPlan(IReadOnlyDictionary<string, string> Values);

/// <summary>The Winlogon values that decide automatic sign-in. A null value is absent from the registry.</summary>
public sealed record WinlogonSettings(string? AutoAdminLogon, string? DefaultUserName, string? DefaultDomainName)
{
    public bool AutoSignInOn => string.Equals(AutoAdminLogon, "1", StringComparison.Ordinal);
}

/// <summary>
/// The "require sign-in after sleep" setting (CONSOLELOCK) of the active power plan: 0 never, 1 always. Windows keeps
/// it per power plan, not per user. A null value means the plan did not report one (Windows then requires sign-in).
/// </summary>
public sealed record ConsoleLockSetting(uint? OnAc, uint? OnBattery);

/// <summary>Findings about the administrator accounts that matter for automatic sign-in.</summary>
/// <param name="MicrosoftAccountAdministrators">Administrators that sign in with a Microsoft account.</param>
/// <param name="WindowsHelloRequired">True when Windows requires Hello for Microsoft accounts on this PC.</param>
public sealed record AdminAccountReport(IReadOnlyList<string> MicrosoftAccountAdministrators, bool WindowsHelloRequired)
{
    public static AdminAccountReport None { get; } = new([], false);

    public bool BlocksAdminAutoSignIn => WindowsHelloRequired && MicrosoftAccountAdministrators.Count > 0;
}

/// <summary>What is stored about the Console sign-in. The password is only ever in this record, the LSA secret, and the DPAPI vault.</summary>
public sealed record ConsoleSignInRecord(
    string UserName,
    string Password,
    string Launcher,
    bool CreatedUser,
    WinlogonSettings PreviousWinlogon,
    ConsoleLockSetting PreviousConsoleLock,
    IReadOnlyList<string> RunValueNames,
    DateTimeOffset ConfiguredAt)
{
    public override string ToString() => $"ConsoleSignInRecord {{ UserName = {UserName}, Launcher = {Launcher} }}";
}

/// <summary>What the Game ready card shows about the Console sign-in.</summary>
public sealed record ConsoleSignInReading(
    string UserName,
    string Launcher,
    bool Configured,
    bool UserExists,
    bool UserIsStandard,
    bool AutoSignInSet,
    bool LauncherSet,
    bool PlaintextPasswordPresent,
    AdminAccountReport Admins,
    IReadOnlyList<LauncherChoice> Launchers)
{
    public bool Healthy => UserExists && UserIsStandard && AutoSignInSet && LauncherSet && !PlaintextPasswordPresent;
}

/// <summary>A launcher the person can pick. Unavailable when it is not installed for all users on this PC.</summary>
public sealed record LauncherChoice(string Id, string Name, bool Available, string? Reason);

public sealed class ConsoleSignInException(string message) : Exception(message);

/// <summary>Which local groups would let the Console user administer the host. Pure, so the check is tested without an account.</summary>
public static class ConsoleUserPolicy
{
    public const string DefaultUserName = "Console";

    // Administrators, Backup Operators (read any file), Power Users, and Hyper-V Administrators.
    private static readonly SecurityIdentifier[] PrivilegedGroups =
    [
        new("S-1-5-32-544"),
        new("S-1-5-32-551"),
        new("S-1-5-32-547"),
        new("S-1-5-32-578"),
    ];

    private static readonly char[] IllegalNameCharacters = "\"/\\[]:|<>+=;,?*@%".ToCharArray();

    public static bool IsPrivileged(IEnumerable<SecurityIdentifier> groups) =>
        groups.Any(group => PrivilegedGroups.Contains(group));

    public static IEnumerable<SecurityIdentifier> PrivilegedMemberships(IEnumerable<SecurityIdentifier> groups) =>
        groups.Where(group => PrivilegedGroups.Contains(group));

    /// <returns>Why the name cannot be used, or null when it can.</returns>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name for the Console user.";
        }

        if (name.Length > 20)
        {
            return "The name can have at most 20 characters.";
        }

        if (name != name.Trim() || name.EndsWith('.') || name.Any(char.IsControl) || name.IndexOfAny(IllegalNameCharacters) >= 0)
        {
            return "The name cannot contain spaces at the ends, a final period, or any of \" / \\ [ ] : | < > + = ; , ? * @ %.";
        }

        if (name.StartsWith("hh-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("hhc-", StringComparison.OrdinalIgnoreCase))
        {
            return "Names starting with hh- or hhc- are used for virtual machine and console accounts.";
        }

        string[] reserved = ["administrator", "guest", "defaultaccount", "wdagutilityaccount", "system", "users", "administrators"];
        return reserved.Contains(name, StringComparer.OrdinalIgnoreCase) ? "That name belongs to a Windows account." : null;
    }
}
