using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyperHarbor.Host.Core.Security;

/// <summary>
/// Loads the host's TLS certificate, creating a self-signed ECDSA P-256 certificate on first run.
/// The PKCS#12 export is encrypted with machine-scope DPAPI and written with a restricted ACL.
/// Clients pin this certificate during pairing, so it must not change once created.
/// </summary>
public sealed class HostCertificateStore
{
    private const string FileName = "host-certificate.pfx.protected";
    private static readonly byte[] Entropy = "HyperHarbor host certificate v1"u8.ToArray();

    private readonly string _path;
    private readonly string _hostName;
    private readonly object _gate = new();
    private X509Certificate2? _certificate;

    public HostCertificateStore(string dataDirectory, string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        _path = Path.Combine(dataDirectory, FileName);
        _hostName = hostName;
    }

    public X509Certificate2 GetOrCreate()
    {
        lock (_gate)
        {
            return _certificate ??= File.Exists(_path) ? Load() : Create();
        }
    }

    private X509Certificate2 Load()
    {
        var pfx = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            // SChannel cannot use ephemeral keys, so let the key be imported to a temporary key container.
            return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=HyperHarbor Host {_hostName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")],
            critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(_hostName);
        names.AddDnsName($"{_hostName}.local");
        request.CertificateExtensions.Add(names.Build());

        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));

        var pfx = created.Export(X509ContentType.Pkcs12);
        try
        {
            ProtectedFile.WriteAllBytes(_path, ProtectedData.Protect(pfx, Entropy, DataProtectionScope.LocalMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }

        return Load();
    }
}
