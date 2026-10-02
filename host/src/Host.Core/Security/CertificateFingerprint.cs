using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyperHarbor.Host.Core.Security;

public static class CertificateFingerprint
{
    /// <summary>SHA-256 of the DER encoding, as uppercase hex. Matches HostInfo.certificateFingerprint.</summary>
    public static string Of(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }
}
