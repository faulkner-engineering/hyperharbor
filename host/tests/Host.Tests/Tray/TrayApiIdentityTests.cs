using System.Security.Cryptography;
using HyperHarbor.Host.Tray.Api;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>The tray's client certificate for the host's API, kept with DPAPI in the tray user's profile.</summary>
public sealed class TrayApiIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Certificate_IsMadeOnce_AndReadBackWithItsKey()
    {
        var first = new TrayApiIdentity(_directory);
        var fingerprint = first.Fingerprint;

        var second = new TrayApiIdentity(_directory);

        Assert.Equal(fingerprint, second.Fingerprint);
        Assert.True(second.Certificate.HasPrivateKey);
        Assert.Equal(64, fingerprint.Length);
    }

    [Fact]
    public void TheStoredFile_IsNotThePfx()
    {
        _ = new TrayApiIdentity(_directory).Certificate;

        var stored = File.ReadAllBytes(Directory.GetFiles(_directory).Single());

        // DPAPI output, which X509 cannot read without unprotecting it first.
        Assert.ThrowsAny<CryptographicException>(() =>
            System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(stored, null));
    }

    [Fact]
    public void AnUnreadableFile_GivesANewCertificate()
    {
        var old = new TrayApiIdentity(_directory).Fingerprint;
        File.WriteAllBytes(Directory.GetFiles(_directory).Single(), [1, 2, 3]);

        var fresh = new TrayApiIdentity(_directory).Fingerprint;

        Assert.NotEqual(old, fresh);
    }

    [Fact]
    public void DeviceId_IsKept()
    {
        var id = Guid.NewGuid();
        new TrayApiIdentity(_directory).DeviceId = id;

        Assert.Equal(id, new TrayApiIdentity(_directory).DeviceId);
        Assert.Null(new TrayApiIdentity(Path.Combine(_directory, "other")).DeviceId);
    }
}
