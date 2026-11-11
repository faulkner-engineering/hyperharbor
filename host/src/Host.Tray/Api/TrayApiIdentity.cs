using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyperHarbor.Host.Tray.Api;

/// <summary>
/// The tray's client certificate for the host's API: a P-256 key and a self-signed certificate made on first use, as
/// the client makes its own (client/src-tauri/src/identity.rs). The PFX is kept with DPAPI for the tray's user in that
/// user's profile, so another account on the PC cannot use it; the device ID the service gave it is kept beside it.
/// </summary>
public sealed class TrayApiIdentity
{
    private const string CertificateFile = "tray-api-certificate.bin";
    private const string DeviceFile = "tray-api-device.txt";
    private static readonly byte[] Entropy = "HyperHarbor.TrayApiIdentity.v1"u8.ToArray();

    private readonly string _directory;
    private readonly object _gate = new();
    private X509Certificate2? _certificate;

    /// <param name="directory">Where the files are kept; %LOCALAPPDATA%\HyperHarbor unless a test passes its own.</param>
    public TrayApiIdentity(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarbor");
    }

    /// <summary>The certificate with its private key, made and stored on first use.</summary>
    public X509Certificate2 Certificate
    {
        get
        {
            lock (_gate)
            {
                return _certificate ??= Load() ?? Create();
            }
        }
    }

    /// <summary>SHA-256 of the certificate DER, uppercase hex: what the host stores for a paired device.</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData));

    /// <summary>The device the service added for this certificate, or null before the first registration.</summary>
    public Guid? DeviceId
    {
        get
        {
            var path = Path.Combine(_directory, DeviceFile);
            return File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id) ? id : null;
        }
        set
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, DeviceFile);
            if (value is { } id)
            {
                File.WriteAllText(path, id.ToString());
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    private X509Certificate2? Load()
    {
        var path = Path.Combine(_directory, CertificateFile);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] pfx;
        try
        {
            pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Written by another account, or the user's DPAPI keys were reset: a new certificate is made and
            // registered, and the old device entry is replaced (TrayApiRegisterMessage.ReplacesDeviceId).
            return null;
        }

        try
        {
            // SChannel cannot use ephemeral keys for a client certificate, so the key goes to a temporary container.
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=HyperHarbor Host tray {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.2", "Client Authentication")],
            critical: false));

        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        var pfx = created.Export(X509ContentType.Pkcs12);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, CertificateFile);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, ProtectedData.Protect(pfx, Entropy, DataProtectionScope.CurrentUser));
            File.Move(temporary, path, overwrite: true);
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}
