using System.Text.Json;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.Elevation;

/// <summary>
/// Persists the PBKDF2 hash of the admin passphrase. The passphrase grants elevation, so the file is
/// written with the restricted ACL from <see cref="ProtectedFile"/>. It is set only from the host tray.
/// </summary>
public sealed class AdminPassphraseStore
{
    public const string FileName = "admin-passphrase.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private StoredHash? _hash;
    private bool _loaded;

    public AdminPassphraseStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public bool IsConfigured => Current() is not null;

    /// <exception cref="ArgumentException">The salt or hash has the wrong length, or there are no iterations.</exception>
    public void Set(byte[] salt, byte[] hash, int iterations)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(hash);
        if (salt.Length != AdminPassphrase.SaltBytes || hash.Length != AdminPassphrase.HashBytes || iterations < 1)
        {
            throw new ArgumentException("The passphrase hash is not in the expected format.");
        }

        var stored = new StoredHash(salt.ToArray(), hash.ToArray(), iterations);
        lock (_gate)
        {
            ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(stored, JsonOptions));
            _hash = stored;
            _loaded = true;
        }
    }

    /// <summary>False when no passphrase is set.</summary>
    public bool Verify(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        if (Current() is not { } stored || passphrase.Length > AdminPassphrase.MaximumLength)
        {
            return false;
        }

        return AdminPassphrase.Verify(passphrase, stored.Salt, stored.Hash, stored.Iterations);
    }

    private StoredHash? Current()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                _hash = File.Exists(_path)
                    ? JsonSerializer.Deserialize<StoredHash>(File.ReadAllBytes(_path), JsonOptions)
                        ?? throw new InvalidDataException($"The admin passphrase file '{_path}' is invalid.")
                    : null;
                _loaded = true;
            }

            return _hash;
        }
    }

    private sealed record StoredHash(byte[] Salt, byte[] Hash, int Iterations);
}
