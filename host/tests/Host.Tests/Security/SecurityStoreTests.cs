using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Tests.Security;

public sealed class SecurityStoreTests : IDisposable
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
    public void HostCertificate_IsCreatedOnce_AndReloadedWithPrivateKey()
    {
        using var first = new HostCertificateStore(_directory, "TEST-HOST").GetOrCreate();
        using var second = new HostCertificateStore(_directory, "TEST-HOST").GetOrCreate();

        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.True(second.HasPrivateKey);
        Assert.Equal("CN=HyperHarbor Host TEST-HOST", second.Subject);
        var usage = second.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void HostCertificateFile_IsEncrypted()
    {
        using var certificate = new HostCertificateStore(_directory, "TEST-HOST").GetOrCreate();
        var bytes = File.ReadAllBytes(Path.Combine(_directory, "host-certificate.pfx.protected"));

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new X509Certificate2(bytes));
    }

    [Fact]
    public void ProtectedFile_DoesNotInheritAcl_AndExcludesUsers()
    {
        var path = Path.Combine(_directory, "secret.json");
        ProtectedFile.WriteAllBytes(path, "{}"u8);

        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);

        var identities = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToList();
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), identities);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), identities);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), identities);
    }

    [Fact]
    public void PairedDevices_PersistAndRemove()
    {
        var store = new PairedDeviceStore(_directory);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        var device = store.Add("Laptop", "abc123", DateTimeOffset.UtcNow);
        var reloaded = new PairedDeviceStore(_directory);

        Assert.Equal(device, reloaded.FindByFingerprint("ABC123"));
        Assert.True(reloaded.Remove(device.DeviceId));
        Assert.False(reloaded.Remove(device.DeviceId));
        Assert.Empty(new PairedDeviceStore(_directory).List());
        Assert.Equal(1, changes);
    }

    [Fact]
    public void PairedDevices_RepairingSameCertificate_ReplacesEntry()
    {
        var store = new PairedDeviceStore(_directory);
        var first = store.Add("Old name", "ABC", DateTimeOffset.UtcNow);

        var second = store.Add("New name", "abc", DateTimeOffset.UtcNow);

        Assert.NotEqual(first.DeviceId, second.DeviceId);
        Assert.Equal("New name", Assert.Single(store.List()).Name);
    }
}
