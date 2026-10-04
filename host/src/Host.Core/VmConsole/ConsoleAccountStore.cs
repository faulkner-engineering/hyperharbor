using System.Security.Cryptography;
using System.Text.Json;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.VmConsole;

/// <summary>
/// Builds host console account names: "hhc-" plus the user name, within the 20-character limit of
/// Windows local account names.
/// </summary>
public static class ConsoleAccountName
{
    public const string Prefix = "hhc-";

    public static string For(string userName) => VmAccountName.Build(Prefix, userName);
}

/// <summary>The current password of a User's host console account. ToString never includes the password.</summary>
public sealed record ConsoleCredential(string AccountName, string Password)
{
    public override string ToString() => $"ConsoleCredential {{ AccountName = {AccountName} }}";
}

/// <summary>
/// Host console account credentials, one per User. The elevated setup command writes them and the
/// service updates them on every rotation. The whole file is encrypted with machine-scope DPAPI and
/// written with a restricted ACL; it is never stored in plaintext.
/// </summary>
public sealed class ConsoleAccountStore
{
    public const string FileName = ConsoleSetupHelper.AccountsFileName;
    private static readonly byte[] Entropy = "HyperHarbor host console accounts v1"u8.ToArray();

    private readonly string _path;
    private readonly object _gate = new();

    public ConsoleAccountStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public ConsoleCredential? Find(Guid userId)
    {
        lock (_gate)
        {
            return Load().TryGetValue(userId, out var credential) ? credential : null;
        }
    }

    public IReadOnlyDictionary<Guid, ConsoleCredential> List()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    public void Save(Guid userId, ConsoleCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        lock (_gate)
        {
            var credentials = Load();
            credentials[userId] = credential;
            Write(credentials);
        }
    }

    public bool Remove(Guid userId)
    {
        lock (_gate)
        {
            var credentials = Load();
            if (!credentials.Remove(userId))
            {
                return false;
            }

            Write(credentials);
            return true;
        }
    }

    private Dictionary<Guid, ConsoleCredential> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<Guid, ConsoleCredential>>(plain) ?? [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void Write(Dictionary<Guid, ConsoleCredential> credentials)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            ProtectedFile.WriteAllBytes(_path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
