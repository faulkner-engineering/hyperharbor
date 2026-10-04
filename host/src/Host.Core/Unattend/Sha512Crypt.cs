using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>
/// SHA-512 crypt ("$6$"), the password hash Ubuntu's installer and /etc/shadow accept. Follows Ulrich
/// Drepper's specification ("Unix crypt using SHA-256 and SHA-512"), including its test vectors.
/// </summary>
public static class Sha512Crypt
{
    public const int DefaultRounds = 5000;
    private const int MinRounds = 1000;
    private const int MaxRounds = 999_999_999;
    private const int MaxSaltLength = 16;
    private const string Alphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>The order in which digest bytes are encoded, three at a time.</summary>
    private static readonly (int, int, int)[] Triples =
    [
        (0, 21, 42), (22, 43, 1), (44, 2, 23), (3, 24, 45), (25, 46, 4), (47, 5, 26), (6, 27, 48),
        (28, 49, 7), (50, 8, 29), (9, 30, 51), (31, 52, 10), (53, 11, 32), (12, 33, 54), (34, 55, 13),
        (56, 14, 35), (15, 36, 57), (37, 58, 16), (59, 17, 38), (18, 39, 60), (40, 61, 19), (62, 20, 41),
    ];

    /// <summary>Hashes <paramref name="password"/> with a random 16-character salt.</summary>
    public static string Hash(string password) =>
        Hash(password, new string(RandomNumberGenerator.GetItems<char>(Alphabet, MaxSaltLength)), DefaultRounds);

    /// <summary>
    /// Hashes with a given salt, as crypt(3) does. The salt may start with "$6$" and "rounds=N$"; it is
    /// truncated to 16 characters. Rounds are written into the result only when they differ from 5000
    /// or were given explicitly in <paramref name="salt"/>.
    /// </summary>
    public static string Hash(string password, string salt, int rounds = DefaultRounds)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);

        var explicitRounds = rounds != DefaultRounds;
        if (salt.StartsWith("$6$", StringComparison.Ordinal))
        {
            salt = salt[3..];
        }

        if (salt.StartsWith("rounds=", StringComparison.Ordinal) && salt.IndexOf('$') is var end and > 7)
        {
            rounds = int.Parse(salt[7..end], NumberStyles.None, CultureInfo.InvariantCulture);
            salt = salt[(end + 1)..];
            explicitRounds = true;
        }

        rounds = Math.Clamp(rounds, MinRounds, MaxRounds);
        if (salt.IndexOf('$') is var dollar and >= 0)
        {
            salt = salt[..dollar];
        }

        if (salt.Length > MaxSaltLength)
        {
            salt = salt[..MaxSaltLength];
        }

        var key = Encoding.UTF8.GetBytes(password);
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        try
        {
            var digest = Compute(key, saltBytes, rounds);
            var result = new StringBuilder("$6$");
            if (explicitRounds)
            {
                result.Append(CultureInfo.InvariantCulture, $"rounds={rounds}$");
            }

            result.Append(salt).Append('$');
            foreach (var (a, b, c) in Triples)
            {
                Encode(result, digest[a], digest[b], digest[c], 4);
            }

            Encode(result, 0, 0, digest[63], 2);
            CryptographicOperations.ZeroMemory(digest);
            return result.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Compute(byte[] key, byte[] salt, int rounds)
    {
        // Digest B: key, salt, key.
        var b = SHA512.HashData([.. key, .. salt, .. key]);

        // Digest A: key, salt, B repeated for the key's length, then B or the key for each bit of the length.
        using var a = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        a.AppendData(key);
        a.AppendData(salt);
        AppendRepeated(a, b, key.Length);
        for (var length = key.Length; length > 0; length >>= 1)
        {
            a.AppendData((length & 1) != 0 ? b : key);
        }

        var digest = a.GetHashAndReset();

        // P: the key hashed once per key byte, cut to the key's length. S: the salt hashed 16 + A[0] times.
        using var dp = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < key.Length; i++)
        {
            dp.AppendData(key);
        }

        var p = Stretch(dp.GetHashAndReset(), key.Length);

        using var ds = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < 16 + digest[0]; i++)
        {
            ds.AppendData(salt);
        }

        var s = Stretch(ds.GetHashAndReset(), salt.Length);

        using var c = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var round = 0; round < rounds; round++)
        {
            c.AppendData((round & 1) != 0 ? p : digest);
            if (round % 3 != 0)
            {
                c.AppendData(s);
            }

            if (round % 7 != 0)
            {
                c.AppendData(p);
            }

            c.AppendData((round & 1) != 0 ? digest : p);
            digest = c.GetHashAndReset();
        }

        CryptographicOperations.ZeroMemory(p);
        return digest;
    }

    private static void AppendRepeated(IncrementalHash hash, byte[] block, int length)
    {
        for (; length > block.Length; length -= block.Length)
        {
            hash.AppendData(block);
        }

        hash.AppendData(block, 0, length);
    }

    /// <summary>The 64-byte <paramref name="digest"/> repeated to <paramref name="length"/> bytes.</summary>
    private static byte[] Stretch(byte[] digest, int length)
    {
        var result = new byte[length];
        for (var offset = 0; offset < length; offset += digest.Length)
        {
            Array.Copy(digest, 0, result, offset, Math.Min(digest.Length, length - offset));
        }

        return result;
    }

    private static void Encode(StringBuilder output, byte high, byte middle, byte low, int characters)
    {
        var value = (high << 16) | (middle << 8) | low;
        for (var i = 0; i < characters; i++)
        {
            output.Append(Alphabet[value & 0x3f]);
            value >>= 6;
        }
    }
}
