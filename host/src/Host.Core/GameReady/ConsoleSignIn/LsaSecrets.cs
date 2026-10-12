using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HyperHarbor.Host.Core.GameReady.ConsoleSignIn;

/// <summary>
/// The LSA private data store ("secrets"), where Winlogon reads the automatic sign-in password from. This is how
/// Sysinternals Autologon keeps it: the registry holds no password.
/// </summary>
public interface ILsaSecrets
{
    /// <summary>Stores or replaces the secret. A null value deletes it.</summary>
    void Store(string name, string? value);

    bool Exists(string name);
}

public sealed class WindowsLsaSecrets : ILsaSecrets
{
    /// <summary>The secret Winlogon reads for automatic sign-in.</summary>
    public const string DefaultPasswordName = "DefaultPassword";

    private const uint PolicyGetPrivateInformation = 0x00000004;
    private const uint PolicyCreateSecret = 0x00000020;
    private const uint StatusObjectNameNotFound = 0xC0000034;

    public void Store(string name, string? value)
    {
        using var policy = Policy.Open(PolicyCreateSecret);
        var key = LsaString.From(name);
        var data = value is null ? default : LsaString.From(value);
        var dataPointer = IntPtr.Zero;
        try
        {
            if (value is not null)
            {
                dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<LsaString>());
                Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);
            }

            var status = NativeMethods.LsaStorePrivateData(policy.Handle, ref key, dataPointer);

            // Deleting a secret that is not there is not a failure.
            if (value is null && status == StatusObjectNameNotFound)
            {
                return;
            }

            Policy.ThrowOnFailure(status, "store the automatic sign-in password");
        }
        finally
        {
            if (dataPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dataPointer);
            }

            data.Free(clear: true);
            key.Free(clear: false);
        }
    }

    public bool Exists(string name)
    {
        using var policy = Policy.Open(PolicyGetPrivateInformation);
        var key = LsaString.From(name);
        try
        {
            var status = NativeMethods.LsaRetrievePrivateData(policy.Handle, ref key, out var data);
            if (status == StatusObjectNameNotFound)
            {
                return false;
            }

            Policy.ThrowOnFailure(status, "read the automatic sign-in password");

            // Only its presence matters; the returned copy holds the password, so it is wiped before it is freed.
            var secret = Marshal.PtrToStructure<LsaString>(data);
            if (secret.Buffer != IntPtr.Zero && secret.MaximumLength > 0)
            {
                Marshal.Copy(new byte[secret.MaximumLength], 0, secret.Buffer, secret.MaximumLength);
            }

            NativeMethods.LsaFreeMemory(data);
            return true;
        }
        finally
        {
            key.Free(clear: false);
        }
    }

    private sealed class Policy : IDisposable
    {
        private Policy(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }

        public static Policy Open(uint access)
        {
            var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
            ThrowOnFailure(NativeMethods.LsaOpenPolicy(IntPtr.Zero, ref attributes, access, out var handle), "open the local security policy");
            return new Policy(handle);
        }

        public static void ThrowOnFailure(uint status, string action)
        {
            if (status != 0)
            {
                var error = NativeMethods.LsaNtStatusToWinError(status);
                throw new Win32Exception(error, $"Could not {action} (error {error}).");
            }
        }

        public void Dispose() => NativeMethods.LsaClose(Handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;

        public static LsaString From(string value) => new()
        {
            Length = (ushort)(value.Length * sizeof(char)),
            MaximumLength = (ushort)((value.Length + 1) * sizeof(char)),
            Buffer = Marshal.StringToHGlobalUni(value),
        };

        public readonly void Free(bool clear)
        {
            if (Buffer == IntPtr.Zero)
            {
                return;
            }

            if (clear)
            {
                Marshal.Copy(new byte[MaximumLength], 0, Buffer, MaximumLength);
            }

            Marshal.FreeHGlobal(Buffer);
        }
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes objectAttributes, uint desiredAccess, out IntPtr policyHandle);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint LsaStorePrivateData(IntPtr policyHandle, ref LsaString keyName, IntPtr privateData);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint LsaRetrievePrivateData(IntPtr policyHandle, ref LsaString keyName, out IntPtr privateData);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint LsaFreeMemory(IntPtr buffer);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint LsaClose(IntPtr policyHandle);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern int LsaNtStatusToWinError(uint status);
    }
}
