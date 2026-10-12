using System.Security.Principal;

namespace HyperHarbor.Host.Core.GameReady.ConsoleSignIn;

/// <summary>
/// The operating system side of the Console sign-in: accounts, Winlogon, the LSA secret, the user's registry hive and the
/// power plan. Everything here needs administrator rights except the reads, so tests use a fake.
/// </summary>
public interface IConsoleSignInSystem
{
    bool UserExists(string userName);

    /// <summary>The local groups the user belongs to, directly or through another group.</summary>
    IReadOnlyList<SecurityIdentifier> GroupsOf(string userName);

    /// <summary>Creates a standard local account (a member of Users only).</summary>
    void CreateUser(string userName, string password, string comment);

    /// <summary>Deletes the account, and its profile when asked.</summary>
    void DeleteUser(string userName, bool deleteProfile);

    /// <summary>Creates the account's profile without a sign-in, so the launcher is set up for the next boot.</summary>
    void CreateProfile(string userName);

    WinlogonSettings ReadWinlogon();

    void WriteWinlogon(WinlogonSettings settings);

    /// <summary>True when a plaintext DefaultPassword value exists in the Winlogon key.</summary>
    bool HasPlaintextPassword();

    void RemovePlaintextPassword();

    /// <summary>Stores the password as the LSA secret Winlogon reads for automatic sign-in. A null password deletes the secret.</summary>
    void StoreAutoSignInPassword(string? password);

    bool HasAutoSignInPassword();

    ConsoleLockSetting ReadConsoleLock();

    void WriteConsoleLock(ConsoleLockSetting setting);

    /// <summary>The command lines for a launcher, or null with the reason when it is not installed.</summary>
    (LauncherPlan? Plan, string? Reason) PlanLauncher(string launcher);

    /// <summary>Writes the launcher's Run values and the "no screen saver password" setting into the user's registry hive.</summary>
    void ConfigureUserHive(string userName, LauncherPlan plan);

    /// <summary>The names of the Run values in the user's hive, or empty when the user has no profile.</summary>
    IReadOnlyList<string> ReadRunValueNames(string userName);

    void RemoveRunValues(string userName, IReadOnlyList<string> names);

    AdminAccountReport InspectAdministrators();
}

/// <summary>Where the Console sign-in record is kept: encrypted for this machine and readable only by the service.</summary>
public interface IConsoleSignInStore
{
    ConsoleSignInRecord? Load();

    void Save(ConsoleSignInRecord record);

    void Delete();
}
