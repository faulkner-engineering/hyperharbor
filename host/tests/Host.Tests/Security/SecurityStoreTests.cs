using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;

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
        var store = new PairedDeviceStore(_directory, Users());
        var userId = Users().GetOrCreateDefault().UserId;
        var changes = 0;
        store.Changed += (_, _) => changes++;

        var device = store.Add(userId, "Laptop", "abc123", DateTimeOffset.UtcNow);
        var reloaded = new PairedDeviceStore(_directory, Users());

        Assert.Equal(device, reloaded.FindByFingerprint("ABC123"));
        Assert.Equal(userId, device.UserId);
        Assert.True(reloaded.Remove(device.DeviceId));
        Assert.False(reloaded.Remove(device.DeviceId));
        Assert.Empty(new PairedDeviceStore(_directory, Users()).List());
        Assert.Equal(1, changes);
    }

    [Fact]
    public void PairedDevices_RepairingSameCertificate_ReplacesEntry()
    {
        var store = new PairedDeviceStore(_directory, Users());
        var userId = Users().GetOrCreateDefault().UserId;
        var first = store.Add(userId, "Old name", "ABC", DateTimeOffset.UtcNow);

        var second = store.Add(userId, "New name", "abc", DateTimeOffset.UtcNow);

        Assert.NotEqual(first.DeviceId, second.DeviceId);
        Assert.Equal("New name", Assert.Single(store.List()).Name);
    }

    [Fact]
    public void PairedDevices_RequireAUser()
    {
        var store = new PairedDeviceStore(_directory, Users());

        Assert.Throws<ArgumentException>(() => store.Add(Guid.Empty, "Laptop", "ABC", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PairedDevices_LegacyRecordsWithoutUser_AreAssignedToDefaultUserAndRewritten()
    {
        // The format written before Users existed (Phase 5 and 6).
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "paired-devices.json"), """
            [
              {
                "DeviceId": "33481bb5-915b-4851-8cbe-1f9810d304a5",
                "Name": "DESKTOP-65QRD0H",
                "CertificateFingerprint": "A13F9360FF3C4BF44D2234E36C7326512CBFF19C50539B0CB37B166D53BF2C5A",
                "PairedAt": "2026-10-02T13:31:18.8910841+00:00"
              }
            ]
            """);

        var device = new PairedDeviceStore(_directory, Users())
            .FindByFingerprint("A13F9360FF3C4BF44D2234E36C7326512CBFF19C50539B0CB37B166D53BF2C5A");

        var defaultUser = Users().GetOrCreateDefault();
        Assert.NotNull(device);
        Assert.Equal(defaultUser.UserId, device.UserId);
        Assert.Contains(defaultUser.UserId.ToString(), File.ReadAllText(Path.Combine(_directory, "paired-devices.json")));
    }

    [Fact]
    public void Users_DefaultUserIsCreatedOnceAndPersisted()
    {
        var first = Users().GetOrCreateDefault();
        var second = Users().GetOrCreateDefault();

        Assert.Equal(first, second);
        Assert.Equal("owner", first.Name);
        Assert.Equal("hh-owner", first.VmAccountName);
        Assert.Single(Users().List());
    }

    [Theory]
    [InlineData("owner", "hh-owner")]
    [InlineData("Tyler Faulkner", "hh-tylerfaulkner")]
    [InlineData("very-long-user-name-here", "hh-very-long-user-na")]
    [InlineData("name-", "hh-name")]
    [InlineData("a.b_c!", "hh-abc")]
    public void VmAccountName_IsSanitizedAndLimitedTo20Characters(string userName, string expected)
    {
        var name = VmAccountName.For(userName);

        Assert.Equal(expected, name);
        Assert.InRange(name.Length, 4, VmAccountName.MaxLength);
    }

    [Fact]
    public void VmAccountName_RejectsNamesWithNoValidCharacters()
    {
        Assert.Throws<ArgumentException>(() => VmAccountName.For("!!!"));
    }

    private UserStore Users() => new(_directory);
}
