using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace HyperHarbor.Host.Core.Pairing;

/// <summary>
/// SPAKE2 over the RFC 3526 3072-bit MODP group, as specified in docs/pairing.md.
/// The client is party A (uses M); the host is party B (uses N).
/// </summary>
public static class Spake2
{
    /// <summary>Length of an encoded group element or scalar in bytes.</summary>
    public const int ElementLength = 384;

    private const string PrimeHex =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718" +
        "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33" +
        "A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864" +
        "D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2" +
        "08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";

    private const string TranscriptLabel = "HyperHarbor-PAKE-v1";

    /// <summary>The RFC 3526 3072-bit prime.</summary>
    public static BigInteger P { get; } = BigInteger.Parse("0" + PrimeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Order of the quadratic-residue subgroup, (P - 1) / 2.</summary>
    public static BigInteger Q { get; } = (P - 1) / 2;

    public static BigInteger G { get; } = 2;

    public static BigInteger M { get; } = HashToGroup("HyperHarbor SPAKE2 MODP-3072 M");

    public static BigInteger N { get; } = HashToGroup("HyperHarbor SPAKE2 MODP-3072 N");

    public enum Role
    {
        /// <summary>Party A, the client. Its share is g^x * M^w.</summary>
        Client,

        /// <summary>Party B, the host. Its share is g^y * N^w.</summary>
        Host,
    }

    /// <summary>The confirmation values cA (client) and cB (host).</summary>
    public sealed record Confirmations(byte[] Client, byte[] Host);

    /// <summary>w = int(SHA-512("HyperHarbor SPAKE2 w" || 0x00 || pairingId || 0x00 || pin)) mod q.</summary>
    public static BigInteger PasswordScalar(Guid pairingId, string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        var input = Concat(
            Encoding.ASCII.GetBytes("HyperHarbor SPAKE2 w"),
            [0],
            Encoding.ASCII.GetBytes(pairingId.ToString("D")),
            [0],
            Encoding.ASCII.GetBytes(pin));
        return ToInteger(SHA512.HashData(input)) % Q;
    }

    /// <summary>A uniform random scalar in [1, q - 1].</summary>
    public static BigInteger RandomScalar()
    {
        var bytes = RandomNumberGenerator.GetBytes(ElementLength + 64);
        return (ToInteger(bytes) % (Q - 1)) + 1;
    }

    /// <summary>Computes this party's share: g^secret * (M or N)^w mod p.</summary>
    public static BigInteger ComputeShare(Role role, BigInteger secret, BigInteger w)
    {
        var mask = role == Role.Client ? M : N;
        return BigInteger.ModPow(G, secret, P) * BigInteger.ModPow(mask, w, P) % P;
    }

    /// <summary>Computes K from the peer's share: (peerShare * peerMask^(q - w))^secret mod p.</summary>
    public static BigInteger ComputeSharedElement(Role role, BigInteger secret, BigInteger w, BigInteger peerShare)
    {
        var peerMask = role == Role.Client ? N : M;
        var unmasked = peerShare * BigInteger.ModPow(peerMask, Q - w, P) % P;
        return BigInteger.ModPow(unmasked, secret, P);
    }

    /// <summary>
    /// Decodes a received share and checks that it is in the order-q subgroup.
    /// </summary>
    /// <returns>The share, or null when it is malformed or outside the subgroup.</returns>
    public static BigInteger? TryDecodeShare(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != ElementLength)
        {
            return null;
        }

        var value = ToInteger(encoded);
        if (value <= 1 || value >= P - 1 || !BigInteger.ModPow(value, Q, P).IsOne)
        {
            return null;
        }

        return value;
    }

    public static Confirmations ComputeConfirmations(
        Guid pairingId,
        ReadOnlySpan<byte> clientCertificateHash,
        ReadOnlySpan<byte> hostCertificateHash,
        BigInteger clientShare,
        BigInteger hostShare,
        BigInteger sharedElement,
        BigInteger w)
    {
        var transcript = BuildTranscript(pairingId, clientCertificateHash, hostCertificateHash, clientShare, hostShare, sharedElement, w);
        var ka = SHA256.HashData(transcript);
        var kcA = HMACSHA256.HashData(ka, Encoding.ASCII.GetBytes("client confirmation"));
        var kcB = HMACSHA256.HashData(ka, Encoding.ASCII.GetBytes("host confirmation"));
        return new Confirmations(HMACSHA256.HashData(kcA, transcript), HMACSHA256.HashData(kcB, transcript));
    }

    /// <summary>Encodes an integer as a 384-byte unsigned big-endian value.</summary>
    public static byte[] Encode(BigInteger value)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length > ElementLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value does not fit in 384 bytes.");
        }

        var encoded = new byte[ElementLength];
        raw.CopyTo(encoded, ElementLength - raw.Length);
        return encoded;
    }

    public static BigInteger ToInteger(ReadOnlySpan<byte> bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    private static byte[] BuildTranscript(
        Guid pairingId,
        ReadOnlySpan<byte> clientCertificateHash,
        ReadOnlySpan<byte> hostCertificateHash,
        BigInteger clientShare,
        BigInteger hostShare,
        BigInteger sharedElement,
        BigInteger w)
    {
        using var stream = new MemoryStream();
        AppendField(stream, Encoding.ASCII.GetBytes(TranscriptLabel));
        AppendField(stream, Encoding.ASCII.GetBytes(pairingId.ToString("D")));
        AppendField(stream, clientCertificateHash);
        AppendField(stream, hostCertificateHash);
        AppendField(stream, Encode(clientShare));
        AppendField(stream, Encode(hostShare));
        AppendField(stream, Encode(sharedElement));
        AppendField(stream, Encode(w));
        return stream.ToArray();
    }

    private static void AppendField(Stream stream, ReadOnlySpan<byte> field)
    {
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)field.Length);
        stream.Write(length);
        stream.Write(field);
    }

    /// <summary>(int(expand(label)) mod p)^2 mod p, where expand concatenates SHA-512(label || i) for i = 0..6.</summary>
    private static BigInteger HashToGroup(string label)
    {
        var labelBytes = Encoding.ASCII.GetBytes(label);
        var expanded = new byte[7 * 64];
        for (byte i = 0; i < 7; i++)
        {
            SHA512.HashData(Concat(labelBytes, [i])).CopyTo(expanded, i * 64);
        }

        var reduced = ToInteger(expanded) % P;
        return BigInteger.ModPow(reduced, 2, P);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
