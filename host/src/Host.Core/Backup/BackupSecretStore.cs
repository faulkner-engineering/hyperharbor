using System.Security.Cryptography;
using System.Text.Json;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>Repository passphrases and target keys, looked up by reference.</summary>
public interface IBackupSecretStore
{
    string? Get(string reference);

    void Set(string reference, string secret);

    void Remove(string reference);
}

/// <summary>
/// Backup secrets (repository passphrases, S3 and Azure keys) in one file encrypted with machine-scope DPAPI and
/// written with the restricted ACL of <see cref="ProtectedFile"/>. Nothing here is logged or returned by the API.
/// </summary>
public sealed class BackupSecretStore : IBackupSecretStore
{
    public const string FileName = "backup-secrets.json.protected";
    private static readonly byte[] Entropy = "HyperHarbor backup secrets v1"u8.ToArray();

    private readonly string _path;
    private readonly object _gate = new();

    public BackupSecretStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public string? Get(string reference)
    {
        lock (_gate)
        {
            return Load().TryGetValue(reference, out var secret) ? secret : null;
        }
    }

    public void Set(string reference, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        lock (_gate)
        {
            var all = Load();
            all[reference] = secret;
            Save(all);
        }
    }

    public void Remove(string reference)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(reference))
            {
                Save(all);
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void Save(Dictionary<string, string> all)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(all);
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
