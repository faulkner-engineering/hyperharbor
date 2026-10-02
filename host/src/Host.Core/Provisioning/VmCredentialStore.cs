using System.Security.Cryptography;
using System.Text.Json;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// Guest administrator credentials used for provisioning and password rotation. The whole file is
/// encrypted with machine-scope DPAPI and written with a restricted ACL; it is never stored in plaintext.
/// </summary>
public sealed class VmCredentialStore
{
    private const string FileName = "vm-credentials.json.protected";
    private static readonly byte[] Entropy = "HyperHarbor VM administrator credentials v1"u8.ToArray();

    private readonly string _path;
    private readonly object _gate = new();

    public VmCredentialStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public GuestCredential? Find(Guid vmId)
    {
        lock (_gate)
        {
            return Load().TryGetValue(vmId, out var credential) ? credential : null;
        }
    }

    public void Save(Guid vmId, GuestCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        lock (_gate)
        {
            var credentials = Load();
            credentials[vmId] = credential;
            Write(credentials);
        }
    }

    private Dictionary<Guid, GuestCredential> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<Guid, GuestCredential>>(plain) ?? [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void Write(Dictionary<Guid, GuestCredential> credentials)
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
