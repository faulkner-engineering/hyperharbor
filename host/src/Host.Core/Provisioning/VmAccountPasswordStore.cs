using System.Security.Cryptography;
using System.Text.Json;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// The current password of each User's VM account (hh-&lt;username&gt;), so the next rotation can change it rather than
/// reset it. A reset by an administrator cuts the account off from the data Windows protected with its old password
/// (DPAPI): browsers then lose their encryption key, sign-ins and saved passwords, and Brave disables extensions as
/// "changed by another program". A change keeps that data readable. The whole file is encrypted with machine-scope
/// DPAPI and written with a restricted ACL; passwords are never logged or stored in plaintext.
/// </summary>
public sealed class VmAccountPasswordStore
{
    private const string FileName = "vm-account-passwords.json.protected";
    private static readonly byte[] Entropy = "HyperHarbor VM account passwords v1"u8.ToArray();

    private readonly string _path;
    private readonly object _gate = new();

    public VmAccountPasswordStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public string? Find(Guid vmId, Guid userId)
    {
        lock (_gate)
        {
            return Load().TryGetValue(Key(vmId, userId), out var password) ? password : null;
        }
    }

    public void Save(Guid vmId, Guid userId, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        lock (_gate)
        {
            var passwords = Load();
            passwords[Key(vmId, userId)] = password;
            Write(passwords);
        }
    }

    /// <summary>Forgets every User's password for a VM (deleted, or provisioned again).</summary>
    public void RemoveVm(Guid vmId)
    {
        lock (_gate)
        {
            var passwords = Load();
            var prefix = $"{vmId:N}:";
            if (passwords.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList() is { Count: > 0 } keys)
            {
                keys.ForEach(key => passwords.Remove(key));
                Write(passwords);
            }
        }
    }

    private static string Key(Guid vmId, Guid userId) => $"{vmId:N}:{userId:N}";

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void Write(Dictionary<string, string> passwords)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(passwords);
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
