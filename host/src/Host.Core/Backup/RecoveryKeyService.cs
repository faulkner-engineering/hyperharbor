using System.Security.Cryptography;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>What the user sees once: the setup id to confirm with, and the recovery key.</summary>
public sealed record RecoveryKeyOffer(string SetupId, string RecoveryKey)
{
    public override string ToString() => $"RecoveryKeyOffer {{ SetupId = {SetupId}, RecoveryKey = ***** }}";
}

public sealed class RecoveryKeyException(string message) : Exception(message);

/// <summary>
/// Generates repository passphrases and holds them in memory only until the user proves they saved the key by
/// typing back its last four groups. Only then is the passphrase stored (DPAPI vault) and the caller may create the
/// repository. The key is never returned again, so an unconfirmed setup cannot run.
/// </summary>
public sealed class RecoveryKeyService(IBackupSecretStore secrets, TimeProvider time)
{
    public const int GroupSize = 4;
    public const int ConfirmGroups = 4;
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(30);
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = [];

    /// <summary>The secret store reference of a repository's passphrase.</summary>
    public static string PassphraseReference(string repoId) => $"repo:{repoId}:passphrase";

    /// <summary>Generates a key for <paramref name="repoId"/>; nothing is stored until <see cref="Confirm"/>.</summary>
    public RecoveryKeyOffer Begin(string repoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        var key = GenerateKey();
        var setupId = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            Purge();
            _pending[setupId] = new Pending(repoId, key, time.GetUtcNow() + OfferLifetime);
        }

        return new RecoveryKeyOffer(setupId, key);
    }

    /// <summary>
    /// Stores the passphrase when <paramref name="typedBack"/> matches the last groups of the key. A wrong entry
    /// keeps the offer open; an expired or unknown setup is an error.
    /// </summary>
    /// <returns>The repository id the key belongs to.</returns>
    public string Confirm(string setupId, string typedBack)
    {
        lock (_gate)
        {
            Purge();
            if (!_pending.TryGetValue(setupId, out var pending))
            {
                throw new RecoveryKeyException("This setup has expired. Start the target setup again to get a new recovery key.");
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Normalize(typedBack)),
                    System.Text.Encoding.ASCII.GetBytes(Tail(pending.Key))))
            {
                throw new RecoveryKeyException($"That does not match the last {ConfirmGroups} groups of the recovery key. Check the saved copy and try again.");
            }

            secrets.Set(PassphraseReference(pending.RepoId), pending.Key);
            _pending.Remove(setupId);
            return pending.RepoId;
        }
    }

    /// <summary>Drops an offer without storing anything (the user cancelled).</summary>
    public void Cancel(string setupId)
    {
        lock (_gate)
        {
            _pending.Remove(setupId);
        }
    }

    /// <summary>
    /// The stored form of a key typed by hand: upper case, groups of four. A key copied without its dashes or in lower case
    /// opens the same repository.
    /// </summary>
    public static string Canonical(string typed)
    {
        var plain = Normalize(typed);
        return string.Join('-', Enumerable.Range(0, (plain.Length + GroupSize - 1) / GroupSize)
            .Select(i => plain.Substring(i * GroupSize, Math.Min(GroupSize, plain.Length - i * GroupSize))));
    }

    internal static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var chars = new char[52];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5 && index < chars.Length)
            {
                chars[index++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (index < chars.Length)
        {
            chars[index] = Alphabet[(buffer << (5 - bits)) & 31];
        }

        var plain = new string(chars);
        return string.Join('-', Enumerable.Range(0, plain.Length / GroupSize).Select(i => plain.Substring(i * GroupSize, GroupSize)));
    }

    private static string Normalize(string text) =>
        new(text.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());

    private static string Tail(string key) => Normalize(key)[^(GroupSize * ConfirmGroups)..];

    private void Purge()
    {
        var now = time.GetUtcNow();
        foreach (var expired in _pending.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList())
        {
            _pending.Remove(expired);
        }
    }

    private sealed record Pending(string RepoId, string Key, DateTimeOffset Expires);
}
