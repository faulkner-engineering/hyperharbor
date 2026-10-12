using System.Security.Principal;
using HyperHarbor.Host.Core.GameReady.ConsoleSignIn;

namespace HyperHarbor.Host.Tests.GameReady;

internal sealed class MemoryConsoleSignInStore : IConsoleSignInStore
{
    public ConsoleSignInRecord? Record { get; set; }

    public ConsoleSignInRecord? Load() => Record;

    public void Save(ConsoleSignInRecord record) => Record = record;

    public void Delete() => Record = null;
}

/// <summary>An in-memory PC. Every call is logged, and a call named in <see cref="FailOn"/> throws.</summary>
internal sealed class FakeConsoleSignInSystem : IConsoleSignInSystem
{
    public static readonly SecurityIdentifier UsersGroup = new("S-1-5-32-545");
    public static readonly SecurityIdentifier Administrators = new("S-1-5-32-544");
    public static readonly SecurityIdentifier HyperVAdministrators = new("S-1-5-32-578");

    public List<string> Calls { get; } = [];

    public HashSet<string> FailOn { get; } = [];

    public HashSet<string> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<SecurityIdentifier> Groups { get; set; } = [UsersGroup];

    public WinlogonSettings Winlogon { get; set; } = new(null, null, null);

    public bool Plaintext { get; set; }

    public string? Secret { get; private set; }

    public ConsoleLockSetting Lock { get; set; } = new(1, 1);

    public HashSet<string> RunValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public LauncherPlan? Plan { get; set; } = new(new Dictionary<string, string> { ["HyperHarborLauncher"] = "steam.exe -start steam://open/bigpicture" });

    public AdminAccountReport Admins { get; set; } = AdminAccountReport.None;

    private void Step(string call)
    {
        Calls.Add(call);
        if (FailOn.Contains(call))
        {
            throw new InvalidOperationException($"{call} failed");
        }
    }

    public bool UserExists(string userName) => Accounts.Contains(userName);

    public IReadOnlyList<SecurityIdentifier> GroupsOf(string userName) => Groups;

    public void CreateUser(string userName, string password, string comment)
    {
        Step("createUser");
        Accounts.Add(userName);
    }

    public void DeleteUser(string userName, bool deleteProfile)
    {
        Step("deleteUser");
        Accounts.Remove(userName);
    }

    public void CreateProfile(string userName) => Step("createProfile");

    public WinlogonSettings ReadWinlogon() => Winlogon;

    public void WriteWinlogon(WinlogonSettings settings)
    {
        Step("writeWinlogon");
        Winlogon = settings;
    }

    public bool HasPlaintextPassword() => Plaintext;

    public void RemovePlaintextPassword()
    {
        Step("removePlaintext");
        Plaintext = false;
    }

    public void StoreAutoSignInPassword(string? password)
    {
        Step(password is null ? "deleteSecret" : "storeSecret");
        Secret = password;
    }

    public bool HasAutoSignInPassword() => Secret is not null;

    public ConsoleLockSetting ReadConsoleLock() => Lock;

    public void WriteConsoleLock(ConsoleLockSetting setting)
    {
        Step("writeLock");
        Lock = setting;
    }

    public (LauncherPlan? Plan, string? Reason) PlanLauncher(string launcher) =>
        Plan is null ? (null, "Not installed.") : (Plan, null);

    public void ConfigureUserHive(string userName, LauncherPlan plan)
    {
        Step("configureHive");
        foreach (var name in plan.Values.Keys)
        {
            RunValues.Add(name);
        }
    }

    public IReadOnlyList<string> ReadRunValueNames(string userName) => RunValues.ToList();

    public void RemoveRunValues(string userName, IReadOnlyList<string> names)
    {
        Step("removeRunValues");
        foreach (var name in names)
        {
            RunValues.Remove(name);
        }
    }

    public AdminAccountReport InspectAdministrators() => Admins;
}
