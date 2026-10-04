using System.ComponentModel;
using HyperHarbor.Shared.Contracts;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace HyperHarbor.Host.Core.VmConsole;

/// <summary>Changes a host console account's password without administrator rights.</summary>
public interface IConsolePasswordChanger
{
    /// <exception cref="ConsoleConflictException">The account is missing or its stored password is wrong (setup required).</exception>
    void ChangePassword(string accountName, string oldPassword, string newPassword);
}

/// <summary>
/// Host local account operations for console accounts. Only <see cref="ChangePassword"/> works
/// unelevated; everything else is for the elevated setup command.
/// </summary>
public sealed class WindowsLocalAccounts : IConsolePasswordChanger, ILocalAccountAdmin
{
    /// <summary>
    /// Logon rights denied to console accounts: they may only authenticate over the network, which is
    /// what the Hyper-V console uses.
    /// </summary>
    public static readonly string[] DeniedLogonRights =
    [
        "SeDenyInteractiveLogonRight",
        "SeDenyRemoteInteractiveLogonRight",
        "SeDenyBatchLogonRight",
        "SeDenyServiceLogonRight",
    ];

    private const int NerrSuccess = 0;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidPassword = 86;
    private const int ErrorFileNotFound = 2;
    private const int NerrUserNotFound = 2221;
    private const int NerrPasswordTooShort = 2245;

    private const uint UserPrivUser = 1;
    private const uint UfScript = 0x0001;
    private const uint UfDontExpirePasswd = 0x10000;

    private const string SpecialAccountsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    public void ChangePassword(string accountName, string oldPassword, string newPassword)
    {
        // A null domain makes NetUserChangePassword look in the caller's logon domain, which fails with
        // NERR_UserNotFound for a Microsoft account; the local computer name selects the local SAM.
        var status = NativeMethods.NetUserChangePassword(Environment.MachineName, accountName, oldPassword, newPassword);
        switch (status)
        {
            case NerrSuccess:
                return;
            case NerrUserNotFound:
                throw ConsoleConflictException.SetupRequired($"The host console account {accountName} does not exist.");
            case ErrorInvalidPassword:
                throw ConsoleConflictException.SetupRequired($"The stored password for the host console account {accountName} is out of date.");
            case NerrPasswordTooShort:
                throw new ConsoleConflictException(
                    $"The host's password policy refused a new password for {accountName} (minimum age, history, or complexity).",
                    ContractInfo.ProblemCodes.ConsolePasswordPolicy);
            default:
                throw new Win32Exception(status, $"Could not change the password of {accountName} (error {status}).");
        }
    }

    /// <summary>True when a local account with this name exists.</summary>
    public bool Exists(string accountName)
    {
        var status = NativeMethods.NetUserGetInfo(null, accountName, 0, out var buffer);
        if (buffer != IntPtr.Zero)
        {
            NativeMethods.NetApiBufferFree(buffer);
        }

        return status switch
        {
            NerrSuccess => true,
            NerrUserNotFound => false,
            _ => throw new Win32Exception(status, $"Could not look up the account {accountName} (error {status})."),
        };
    }

    /// <summary>Creates a standard local account (a member of Users only) whose password never expires. Elevated.</summary>
    public void Create(string accountName, string password, string comment)
    {
        var info = new NativeMethods.UserInfo1
        {
            Name = accountName,
            Password = password,
            Privilege = UserPrivUser,
            Comment = comment,
            Flags = UfScript | UfDontExpirePasswd,
        };
        Check(NativeMethods.NetUserAdd(null, 1, ref info, out _), $"create the account {accountName}");
    }

    /// <summary>Sets a new password and the never-expires flag on an existing account. Elevated.</summary>
    public void Reset(string accountName, string password)
    {
        var passwordInfo = new NativeMethods.UserInfo1003 { Password = password };
        Check(NativeMethods.NetUserSetInfo(null, accountName, 1003, ref passwordInfo, out _), $"reset the password of {accountName}");

        var flags = new NativeMethods.UserInfo1008 { Flags = UfScript | UfDontExpirePasswd };
        Check(NativeMethods.NetUserSetInfo(null, accountName, 1008, ref flags, out _), $"update the flags of {accountName}");
    }

    /// <summary>Denies every logon type except network, and hides the account from the sign-in screen. Elevated.</summary>
    public void Restrict(string accountName)
    {
        var sid = Sid(accountName);
        using (var policy = LsaPolicy.Open())
        {
            policy.AddRights(sid, DeniedLogonRights);
        }

        using var key = Registry.LocalMachine.CreateSubKey(SpecialAccountsKey, writable: true);
        key.SetValue(accountName, 0, RegistryValueKind.DWord);
    }

    /// <summary>Deletes the account, its logon rights, its sign-in screen entry, and its profile. Elevated.</summary>
    public void Delete(string accountName)
    {
        var sid = Sid(accountName);
        using (var policy = LsaPolicy.Open())
        {
            policy.RemoveAllRights(sid);
        }

        using (var key = Registry.LocalMachine.OpenSubKey(SpecialAccountsKey, writable: true))
        {
            key?.DeleteValue(accountName, throwOnMissingValue: false);
        }

        Check(NativeMethods.NetUserDel(null, accountName), $"delete the account {accountName}");

        // A console logon creates a profile, and deleting the account leaves it behind.
        if (!NativeMethods.DeleteProfile(sid.Value, null, null))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorFileNotFound)
            {
                throw new Win32Exception(error, $"Could not delete the profile of {accountName} (error {error}).");
            }
        }
    }

    public static SecurityIdentifier Sid(string accountName) =>
        (SecurityIdentifier)new NTAccount(Environment.MachineName, accountName).Translate(typeof(SecurityIdentifier));

    private static void Check(int status, string action)
    {
        if (status == ErrorAccessDenied)
        {
            throw new UnauthorizedAccessException($"Administrator rights are required to {action}.");
        }

        if (status != NerrSuccess)
        {
            throw new Win32Exception(status, $"Could not {action} (error {status}).");
        }
    }

    private sealed class LsaPolicy : IDisposable
    {
        private const uint PolicyCreateAccount = 0x00000010;
        private const uint PolicyLookupNames = 0x00000800;
        private const uint StatusObjectNameNotFound = 0xC0000034;

        private readonly IntPtr _handle;

        private LsaPolicy(IntPtr handle) => _handle = handle;

        public static LsaPolicy Open()
        {
            var attributes = new NativeMethods.LsaObjectAttributes { Length = Marshal.SizeOf<NativeMethods.LsaObjectAttributes>() };
            var status = NativeMethods.LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateAccount | PolicyLookupNames, out var handle);
            ThrowOnFailure(status, "open the local security policy");
            return new LsaPolicy(handle);
        }

        public void AddRights(SecurityIdentifier sid, IReadOnlyList<string> rights)
        {
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            var strings = rights.Select(NativeMethods.LsaUnicodeString.From).ToArray();
            try
            {
                ThrowOnFailure(NativeMethods.LsaAddAccountRights(_handle, bytes, strings, (uint)strings.Length), "set logon rights");
            }
            finally
            {
                foreach (var value in strings)
                {
                    Marshal.FreeHGlobal(value.Buffer);
                }
            }
        }

        public void RemoveAllRights(SecurityIdentifier sid)
        {
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            var status = NativeMethods.LsaRemoveAccountRights(_handle, bytes, true, null, 0);
            if (status != StatusObjectNameNotFound)
            {
                ThrowOnFailure(status, "remove logon rights");
            }
        }

        public void Dispose() => NativeMethods.LsaClose(_handle);

        private static void ThrowOnFailure(uint status, string action)
        {
            if (status != 0)
            {
                var error = NativeMethods.LsaNtStatusToWinError(status);
                throw new Win32Exception(error, $"Could not {action} (error {error}).");
            }
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct UserInfo1
        {
            public string Name;
            public string Password;
            public uint PasswordAge;
            public uint Privilege;
            public string? HomeDirectory;
            public string? Comment;
            public uint Flags;
            public string? ScriptPath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct UserInfo1003
        {
            public string Password;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct UserInfo1008
        {
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LsaObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LsaUnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;

            public static LsaUnicodeString From(string value) => new()
            {
                Length = (ushort)(value.Length * sizeof(char)),
                MaximumLength = (ushort)((value.Length + 1) * sizeof(char)),
                Buffer = Marshal.StringToHGlobalUni(value),
            };
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int NetUserChangePassword(string domainName, string userName, string oldPassword, string newPassword);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int NetUserGetInfo(string? serverName, string userName, int level, out IntPtr buffer);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int NetUserAdd(string? serverName, int level, ref UserInfo1 buffer, out int parameterError);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int NetUserSetInfo(string? serverName, string userName, int level, ref UserInfo1003 buffer, out int parameterError);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int NetUserSetInfo(string? serverName, string userName, int level, ref UserInfo1008 buffer, out int parameterError);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int NetUserDel(string? serverName, string userName);

        [DllImport("netapi32.dll", ExactSpelling = true)]
        public static extern int NetApiBufferFree(IntPtr buffer);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true, EntryPoint = "DeleteProfileW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteProfile(string sid, string? profilePath, string? computerName);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes objectAttributes, uint desiredAccess, out IntPtr policyHandle);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint LsaAddAccountRights(IntPtr policyHandle, byte[] accountSid, LsaUnicodeString[] userRights, uint countOfRights);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint LsaRemoveAccountRights(
            IntPtr policyHandle,
            byte[] accountSid,
            [MarshalAs(UnmanagedType.U1)] bool allRights,
            LsaUnicodeString[]? userRights,
            uint countOfRights);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint LsaClose(IntPtr policyHandle);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern int LsaNtStatusToWinError(uint status);
    }
}
