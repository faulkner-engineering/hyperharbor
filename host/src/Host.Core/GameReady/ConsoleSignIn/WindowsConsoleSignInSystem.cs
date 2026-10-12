using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using HyperHarbor.Host.Core.VmConsole;
using Microsoft.Win32;

namespace HyperHarbor.Host.Core.GameReady.ConsoleSignIn;

/// <summary>The values of the Winlogon key. Behind an interface so a test can watch which values are written.</summary>
public interface IWinlogonValues
{
    string? Get(string name);

    void Set(string name, string value);

    void Remove(string name);
}

public sealed class RegistryWinlogonValues : IWinlogonValues
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    public string? Get(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// The Windows implementation of <see cref="IConsoleSignInSystem"/>. Writes need administrator rights (the installed
/// service runs as LocalSystem). The password goes only to the LSA secret, never to the registry.
/// </summary>
public sealed class WindowsConsoleSignInSystem : IConsoleSignInSystem
{
    internal const string AutoAdminLogon = "AutoAdminLogon";
    internal const string DefaultUserName = "DefaultUserName";
    internal const string DefaultDomainName = "DefaultDomainName";
    internal const string DefaultPassword = "DefaultPassword";
    internal const string AutoLogonCount = "AutoLogonCount";

    public const string LauncherRunValue = "HyperHarborLauncher";
    public const string SteamRunValue = "HyperHarborSteam";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string HiveName = "HyperHarborConsoleUser";
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    private static readonly Guid SubNone = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly Guid ConsoleLock = new("0e796bdb-100d-47d6-a2d5-f7d2daa51f51");

    private readonly WindowsLocalAccounts _accounts = new();
    private readonly IWinlogonValues _winlogon;
    private readonly ILsaSecrets _secrets;

    public WindowsConsoleSignInSystem()
        : this(new RegistryWinlogonValues(), new WindowsLsaSecrets())
    {
    }

    internal WindowsConsoleSignInSystem(IWinlogonValues winlogon, ILsaSecrets secrets)
    {
        _winlogon = winlogon;
        _secrets = secrets;
    }

    public bool UserExists(string userName) => _accounts.Exists(userName);

    public IReadOnlyList<SecurityIdentifier> GroupsOf(string userName)
    {
        const int includeIndirect = 1;
        var status = NativeMethods.NetUserGetLocalGroups(null, userName, 0, includeIndirect, out var buffer, -1, out var count, out _);
        if (status != 0)
        {
            throw new Win32Exception(status, $"Could not read the groups of {userName} (error {status}).");
        }

        try
        {
            var groups = new List<SecurityIdentifier>();
            for (var i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                if (name is not null && TryTranslate(name) is { } sid)
                {
                    groups.Add(sid);
                }
            }

            return groups;
        }
        finally
        {
            NativeMethods.NetApiBufferFree(buffer);
        }
    }

    public void CreateUser(string userName, string password, string comment) => _accounts.Create(userName, password, comment);

    public void DeleteUser(string userName, bool deleteProfile)
    {
        if (!deleteProfile)
        {
            throw new NotSupportedException("The account is deleted together with its profile.");
        }

        // The profile goes first: it fails while the user is signed in, and the account must still exist then, so the
        // person can sign the user out and run Disable again.
        var sid = WindowsLocalAccounts.Sid(userName).Value;
        if (!NativeMethods.DeleteProfile(sid, null, null))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 2)
            {
                throw new Win32Exception(error, $"Could not delete the profile of {userName} (error {error}); sign the user out first.");
            }
        }

        _accounts.Delete(userName);
    }

    public void CreateProfile(string userName)
    {
        var sid = WindowsLocalAccounts.Sid(userName).Value;
        var path = new StringBuilder(260);
        var result = NativeMethods.CreateProfile(sid, userName, path, (uint)path.Capacity);
        const int alreadyExists = unchecked((int)0x800700B7);
        if (result != 0 && result != alreadyExists)
        {
            throw new Win32Exception(result & 0xFFFF, $"Could not create the profile of {userName} (HRESULT 0x{result:X8}).");
        }
    }

    public WinlogonSettings ReadWinlogon() =>
        new(_winlogon.Get(AutoAdminLogon), _winlogon.Get(DefaultUserName), _winlogon.Get(DefaultDomainName));

    public void WriteWinlogon(WinlogonSettings settings)
    {
        Write(AutoAdminLogon, settings.AutoAdminLogon);
        Write(DefaultUserName, settings.DefaultUserName);
        Write(DefaultDomainName, settings.DefaultDomainName);

        // A sign-in count would end automatic sign-in after that many boots.
        _winlogon.Remove(AutoLogonCount);
    }

    public bool HasPlaintextPassword() => _winlogon.Get(DefaultPassword) is not null;

    public void RemovePlaintextPassword() => _winlogon.Remove(DefaultPassword);

    public void StoreAutoSignInPassword(string? password) => _secrets.Store(WindowsLsaSecrets.DefaultPasswordName, password);

    public bool HasAutoSignInPassword() => _secrets.Exists(WindowsLsaSecrets.DefaultPasswordName);

    public ConsoleLockSetting ReadConsoleLock()
    {
        if (!TryActiveScheme(out var scheme))
        {
            return new ConsoleLockSetting(null, null);
        }

        var subgroup = SubNone;
        var setting = ConsoleLock;
        uint? ac = NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var acValue) == 0 ? acValue : null;
        uint? dc = NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var dcValue) == 0 ? dcValue : null;
        return new ConsoleLockSetting(ac, dc);
    }

    public void WriteConsoleLock(ConsoleLockSetting setting)
    {
        if (!TryActiveScheme(out var scheme))
        {
            throw new InvalidOperationException("Windows did not report the active power plan.");
        }

        // A plan that never reported a value requires sign-in after sleep, which is 1.
        var plan = scheme.ToString("D");
        RunPowerCfg("/setacvalueindex", plan, SubNone.ToString("D"), ConsoleLock.ToString("D"), (setting.OnAc ?? 1).ToString());
        RunPowerCfg("/setdcvalueindex", plan, SubNone.ToString("D"), ConsoleLock.ToString("D"), (setting.OnBattery ?? 1).ToString());
        RunPowerCfg("/setactive", plan);
    }

    public (LauncherPlan? Plan, string? Reason) PlanLauncher(string launcher)
    {
        var steam = SteamExecutable();
        switch (launcher)
        {
            case ConsoleLaunchers.SteamBigPicture:
                return steam is null
                    ? (null, "Steam was not found. Install Steam for this PC first.")
                    : (new LauncherPlan(new Dictionary<string, string>
                    {
                        [LauncherRunValue] = $"\"{steam}\" -start steam://open/bigpicture",
                    }), null);
            case ConsoleLaunchers.Playnite:
                var playnite = PlayniteFullscreen();
                if (playnite is null)
                {
                    return (null, "Playnite was not found in Program Files. Install the machine-wide version: a per-user install belongs to one account.");
                }

                var values = new Dictionary<string, string> { [LauncherRunValue] = $"\"{playnite}\"" };
                if (steam is not null)
                {
                    // Playnite lists Steam games, and Steam must be running for them to start.
                    values[SteamRunValue] = $"\"{steam}\" -silent";
                }

                return (new LauncherPlan(values), null);
            default:
                return (null, "Unknown launcher.");
        }
    }

    public void ConfigureUserHive(string userName, LauncherPlan plan)
    {
        var opened = WithHive(userName, writable: true, root =>
        {
            using (var run = root.CreateSubKey(RunKey, writable: true))
            {
                foreach (var (name, command) in plan.Values)
                {
                    run.SetValue(name, command, RegistryValueKind.String);
                }
            }

            // No screen saver password: a locked screen on a couch PC has nobody to unlock it.
            using var desktop = root.CreateSubKey(DesktopKey, writable: true);
            desktop.SetValue("ScreenSaverIsSecure", "0", RegistryValueKind.String);
            return true;
        });

        if (!opened)
        {
            throw new InvalidOperationException($"{userName} has no profile to configure.");
        }
    }

    public IReadOnlyList<string> ReadRunValueNames(string userName) =>
        WithHive(userName, writable: false, root =>
        {
            using var run = root.OpenSubKey(RunKey);
            return (IReadOnlyList<string>)(run?.GetValueNames() ?? []);
        }) ?? [];

    public void RemoveRunValues(string userName, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }

        WithHive(userName, writable: true, root =>
        {
            using var run = root.OpenSubKey(RunKey, writable: true);
            foreach (var name in names)
            {
                run?.DeleteValue(name, throwOnMissingValue: false);
            }

            return true;
        });
    }

    public AdminAccountReport InspectAdministrators()
    {
        var microsoftAccounts = new List<string>();
        try
        {
            var group = ((NTAccount)new SecurityIdentifier("S-1-5-32-544").Translate(typeof(NTAccount))).Value.Split('\\').Last();
            var resume = IntPtr.Zero;
            var status = NativeMethods.NetLocalGroupGetMembers(null, group, 3, out var buffer, -1, out var count, out _, ref resume);
            if (status == 0)
            {
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var member = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                        if (member is not null && member.StartsWith("MicrosoftAccount\\", StringComparison.OrdinalIgnoreCase))
                        {
                            microsoftAccounts.Add(member["MicrosoftAccount\\".Length..]);
                        }
                    }
                }
                finally
                {
                    NativeMethods.NetApiBufferFree(buffer);
                }
            }
        }
        catch (IdentityNotMappedException)
        {
            // Without the group's name nothing can be said about its members.
        }

        // "Require Windows Hello sign-in for Microsoft accounts" is DevicePasswordLessBuildVersion 0; 2 is off.
        using var passwordless = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device");
        var helloRequired = passwordless?.GetValue("DevicePasswordLessBuildVersion") is int and 0;
        return new AdminAccountReport(microsoftAccounts, helloRequired);
    }

    private void Write(string name, string? value)
    {
        if (value is null)
        {
            _winlogon.Remove(name);
        }
        else
        {
            _winlogon.Set(name, value);
        }
    }

    private static string? SteamExecutable()
    {
        foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue("InstallPath") is string folder && Path.Combine(folder, "steam.exe") is { } exe && File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    private static string? PlayniteFullscreen() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Playnite", "Playnite.FullscreenApp.exe"))
            .FirstOrDefault(File.Exists);

    private static SecurityIdentifier? TryTranslate(string groupName)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(groupName).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    private static bool TryActiveScheme(out Guid scheme)
    {
        scheme = default;
        if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0)
        {
            return false;
        }

        try
        {
            scheme = Marshal.PtrToStructure<Guid>(pointer);
            return true;
        }
        finally
        {
            NativeMethods.LocalFree(pointer);
        }
    }

    private static void RunPowerCfg(params string[] arguments)
    {
        var start = new ProcessStartInfo("powercfg.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"powercfg exited with code {process.ExitCode}: {output.Trim()}");
        }
    }

    /// <summary>
    /// Opens the user's registry hive (the loaded one while the user is signed in, else NTUSER.DAT loaded under a
    /// temporary key), runs <paramref name="action"/>, and unloads the hive. Null when the user has no profile.
    /// </summary>
    private static T? WithHive<T>(string userName, bool writable, Func<RegistryKey, T> action)
    {
        string sid;
        try
        {
            sid = WindowsLocalAccounts.Sid(userName).Value;
        }
        catch (IdentityNotMappedException)
        {
            return default;
        }

        if (Registry.Users.GetSubKeyNames().Contains(sid, StringComparer.OrdinalIgnoreCase))
        {
            using var loaded = Registry.Users.OpenSubKey(sid, writable);
            return loaded is null ? default : action(loaded);
        }

        string? profile;
        using (var list = Registry.LocalMachine.OpenSubKey($@"{ProfileListKey}\{sid}"))
        {
            profile = list?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        var file = profile is null ? null : Path.Combine(Environment.ExpandEnvironmentVariables(profile), "NTUSER.DAT");
        if (file is null || !File.Exists(file))
        {
            return default;
        }

        RunReg("load", $@"HKU\{HiveName}", file);
        try
        {
            using var root = Registry.Users.OpenSubKey(HiveName, writable);
            return root is null ? default : action(root);
        }
        finally
        {
            Unload();
        }
    }

    /// <summary>A hive left loaded would give the account a temporary profile at its next sign-in, so the unload is retried.</summary>
    private static void Unload()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (TryReg("unload", $@"HKU\{HiveName}"))
            {
                return;
            }

            Thread.Sleep(300);
        }

        throw new IOException("The user's registry hive could not be unloaded. Restart the PC before signing in as that user.");
    }

    private static void RunReg(params string[] arguments)
    {
        if (!TryReg(arguments))
        {
            throw new InvalidOperationException($"reg.exe {arguments[0]} failed.");
        }
    }

    private static bool TryReg(params string[] arguments)
    {
        var start = new ProcessStartInfo("reg.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static class NativeMethods
    {
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int NetUserGetLocalGroups(string? serverName, string userName, int level, int flags, out IntPtr buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level, out IntPtr buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

        [DllImport("netapi32.dll", ExactSpelling = true)]
        public static extern int NetApiBufferFree(IntPtr buffer);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true, EntryPoint = "DeleteProfileW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteProfile(string sid, string? profilePath, string? computerName);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "CreateProfile")]
        public static extern int CreateProfile(string userSid, string userName, StringBuilder profilePath, uint profilePathLength);

        [DllImport("powrprof.dll")]
        public static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        public static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

        [DllImport("powrprof.dll")]
        public static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr memory);
    }
}
