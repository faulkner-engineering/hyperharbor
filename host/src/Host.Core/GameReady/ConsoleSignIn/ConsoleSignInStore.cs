using System.Security.Cryptography;
using System.Text.Json;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.GameReady.ConsoleSignIn;

/// <summary>
/// The Console sign-in record, encrypted with machine-scope DPAPI and written with the restricted ACL of
/// <see cref="ProtectedFile"/>: only SYSTEM, Administrators, and the service account can read the file, so the Console
/// user cannot. The password is never shown anywhere and never stored in plaintext.
/// </summary>
public sealed class ConsoleSignInStore : IConsoleSignInStore
{
    public const string FileName = "console-signin.json.protected";
    private static readonly byte[] Entropy = "HyperHarbor console sign-in v1"u8.ToArray();

    private readonly string _path;
    private readonly object _gate = new();

    public ConsoleSignInStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public ConsoleSignInRecord? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine);
            try
            {
                return JsonSerializer.Deserialize<ConsoleSignInRecord>(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    public void Save(ConsoleSignInRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(record);
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

    public void Delete()
    {
        lock (_gate)
        {
            File.Delete(_path);
        }
    }
}
