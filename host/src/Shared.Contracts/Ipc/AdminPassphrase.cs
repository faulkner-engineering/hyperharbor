using System.Security.Cryptography;

namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>
/// PBKDF2-SHA256 hashing of the host's admin passphrase. The tray hashes the passphrase itself and
/// sends only the salt and hash over the pipe, so the passphrase never leaves the tray process; the
/// service verifies elevation requests against the stored hash.
/// </summary>
public static class AdminPassphrase
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 256;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    /// <summary>Iterations for new hashes, following the OWASP guidance for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>The service refuses weaker hashes from the tray.</summary>
    public const int MinimumIterations = DefaultIterations;

    /// <returns>Null when the passphrase is acceptable, else a message for the person setting it.</returns>
    public static string? Validate(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        if (passphrase.Length < MinimumLength)
        {
            return $"Use at least {MinimumLength} characters.";
        }

        if (passphrase.Length > MaximumLength)
        {
            return $"Use at most {MaximumLength} characters.";
        }

        return string.IsNullOrWhiteSpace(passphrase) ? "The passphrase cannot be only spaces." : null;
    }

    /// <summary>Hashes <paramref name="passphrase"/> with a new random salt.</summary>
    public static SetAdminPassphraseMessage CreateHash(string passphrase, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new SetAdminPassphraseMessage(salt, Derive(passphrase, salt, iterations), iterations);
    }

    public static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    /// <summary>Compares in constant time.</summary>
    public static bool Verify(string passphrase, byte[] salt, byte[] hash, int iterations) =>
        CryptographicOperations.FixedTimeEquals(Derive(passphrase, salt, iterations), hash);
}
