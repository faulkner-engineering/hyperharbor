using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperHarbor.Host.Core.Pairing;
using static HyperHarbor.Host.Core.Pairing.Spake2;

namespace HyperHarbor.Host.Tests.Pairing;

public class Spake2Tests
{
    private static readonly Guid PairingId = Guid.Parse("3f2a9c1d-4b5e-4f70-8192-a3b4c5d6e7f8");

    [Fact]
    public void GroupParameters_AreConsistent()
    {
        Assert.Equal(3072, (int)P.GetBitLength());
        Assert.Equal(7, (int)(P % 8));
        Assert.True(BigInteger.ModPow(G, Q, P).IsOne);
        Assert.True(IsProbablePrime(Q));
    }

    [Fact]
    public void FixedElements_AreDistinctSubgroupMembers()
    {
        Assert.NotEqual(M, N);
        Assert.True(BigInteger.ModPow(M, Q, P).IsOne);
        Assert.True(BigInteger.ModPow(N, Q, P).IsOne);
        Assert.NotNull(TryDecodeShare(Encode(M)));
    }

    [Fact]
    public void BothParties_DeriveSameConfirmations_WithSamePin()
    {
        var (client, host) = Run("123456", "123456");

        Assert.Equal(client.Client, host.Client);
        Assert.Equal(client.Host, host.Host);
    }

    [Fact]
    public void Confirmations_Differ_WithWrongPin()
    {
        var (client, host) = Run("123456", "123457");

        Assert.NotEqual(client.Client, host.Client);
        Assert.NotEqual(client.Host, host.Host);
    }

    [Fact]
    public void Confirmations_Differ_WhenCertificateHashesDiffer()
    {
        var w = PasswordScalar(PairingId, "123456");
        var x = RandomScalar();
        var y = RandomScalar();
        var bigX = ComputeShare(Role.Client, x, w);
        var bigY = ComputeShare(Role.Host, y, w);
        var k = ComputeSharedElement(Role.Client, x, w, bigY);

        var original = ComputeConfirmations(PairingId, Hash("client"), Hash("host"), bigX, bigY, k, w);
        var substituted = ComputeConfirmations(PairingId, Hash("client"), Hash("attacker"), bigX, bigY, k, w);

        Assert.NotEqual(original.Client, substituted.Client);
    }

    [Fact]
    public void TryDecodeShare_RejectsInvalidElements()
    {
        Assert.Null(TryDecodeShare(new byte[10]));
        Assert.Null(TryDecodeShare(Encode(BigInteger.Zero)));
        Assert.Null(TryDecodeShare(Encode(BigInteger.One)));
        Assert.Null(TryDecodeShare(Encode(P - 1)));
        Assert.Null(TryDecodeShare(Encode(P)));

        // A quadratic non-residue is outside the order-q subgroup.
        var nonResidue = Enumerable.Range(3, 100).Select(i => new BigInteger(i))
            .First(candidate => !BigInteger.ModPow(candidate, Q, P).IsOne);
        Assert.Null(TryDecodeShare(Encode(nonResidue)));

        Assert.NotNull(TryDecodeShare(Encode(ComputeShare(Role.Host, RandomScalar(), PasswordScalar(PairingId, "000000")))));
    }

    [Fact]
    public void RandomScalar_IsInRange()
    {
        for (var i = 0; i < 20; i++)
        {
            var scalar = RandomScalar();
            Assert.InRange(scalar, BigInteger.One, Q - 1);
        }
    }

    [Fact]
    public void TestVectors_Match()
    {
        var path = VectorsPath();
        var computed = ComputeVectors();

        // Set HH_WRITE_SPAKE2_VECTORS=1 to regenerate after an intentional protocol change.
        if (Environment.GetEnvironmentVariable("HH_WRITE_SPAKE2_VECTORS") == "1")
        {
            File.WriteAllText(path, JsonSerializer.Serialize(computed, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        Assert.Equal(expected, computed);
    }

    private static Dictionary<string, string> ComputeVectors()
    {
        const string pin = "482913";
        var x = ToInteger(SHA512.HashData(Encoding.ASCII.GetBytes("HyperHarbor test vector x"))) % Q;
        var y = ToInteger(SHA512.HashData(Encoding.ASCII.GetBytes("HyperHarbor test vector y"))) % Q;
        var clientHash = Hash("client certificate");
        var hostHash = Hash("host certificate");

        var w = PasswordScalar(PairingId, pin);
        var bigX = ComputeShare(Role.Client, x, w);
        var bigY = ComputeShare(Role.Host, y, w);
        var k = ComputeSharedElement(Role.Client, x, w, bigY);
        Assert.Equal(k, ComputeSharedElement(Role.Host, y, w, bigX));
        var confirmations = ComputeConfirmations(PairingId, clientHash, hostHash, bigX, bigY, k, w);

        return new Dictionary<string, string>
        {
            ["pairingId"] = PairingId.ToString("D"),
            ["pin"] = pin,
            ["clientCertificateHash"] = Convert.ToHexString(clientHash),
            ["hostCertificateHash"] = Convert.ToHexString(hostHash),
            ["x"] = Convert.ToHexString(Encode(x)),
            ["y"] = Convert.ToHexString(Encode(y)),
            ["M"] = Convert.ToHexString(Encode(M)),
            ["N"] = Convert.ToHexString(Encode(N)),
            ["w"] = Convert.ToHexString(Encode(w)),
            ["X"] = Convert.ToHexString(Encode(bigX)),
            ["Y"] = Convert.ToHexString(Encode(bigY)),
            ["K"] = Convert.ToHexString(Encode(k)),
            ["clientConfirmation"] = Convert.ToHexString(confirmations.Client),
            ["hostConfirmation"] = Convert.ToHexString(confirmations.Host),
        };
    }

    private static (Confirmations Client, Confirmations Host) Run(string clientPin, string hostPin)
    {
        var wClient = PasswordScalar(PairingId, clientPin);
        var wHost = PasswordScalar(PairingId, hostPin);
        var x = RandomScalar();
        var y = RandomScalar();
        var bigX = ComputeShare(Role.Client, x, wClient);
        var bigY = ComputeShare(Role.Host, y, wHost);

        var kClient = ComputeSharedElement(Role.Client, x, wClient, bigY);
        var kHost = ComputeSharedElement(Role.Host, y, wHost, bigX);

        return (
            ComputeConfirmations(PairingId, Hash("c"), Hash("h"), bigX, bigY, kClient, wClient),
            ComputeConfirmations(PairingId, Hash("c"), Hash("h"), bigX, bigY, kHost, wHost));
    }

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.ASCII.GetBytes(text));

    private static string VectorsPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Spake2Vectors.json");

    /// <summary>Miller-Rabin with fixed bases; enough to catch a mistyped prime.</summary>
    private static bool IsProbablePrime(BigInteger n)
    {
        var d = n - 1;
        var r = 0;
        while (d.IsEven)
        {
            d /= 2;
            r++;
        }

        foreach (var a in new BigInteger[] { 2, 3, 5, 7, 11, 13, 17, 19 })
        {
            var x = BigInteger.ModPow(a, d, n);
            if (x.IsOne || x == n - 1)
            {
                continue;
            }

            var composite = true;
            for (var i = 1; i < r; i++)
            {
                x = BigInteger.ModPow(x, 2, n);
                if (x == n - 1)
                {
                    composite = false;
                    break;
                }
            }

            if (composite)
            {
                return false;
            }
        }

        return true;
    }
}
