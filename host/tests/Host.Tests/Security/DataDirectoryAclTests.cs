using System.Security.AccessControl;
using System.Security.Principal;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Tests.Security;

public sealed class DataDirectoryAclTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly SecurityIdentifier _trayUser = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Secure_ProtectsTheFolder_AndLetsTheTrayUserOnlyRead()
    {
        DataDirectoryAcl.Secure(_directory, _trayUser);

        var security = new DirectoryInfo(_directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));

        var trayRule = Assert.Single(rules, rule => rule.IdentityReference.Equals(_trayUser));
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, trayRule.FileSystemRights);
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) && rule.FileSystemRights == FileSystemRights.FullControl);
        foreach (var type in new[] { WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.WorldSid, WellKnownSidType.CreatorOwnerSid })
        {
            Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(new SecurityIdentifier(type, null)));
        }
    }

    [Fact]
    public void Secure_KeepsFilesTrustedAccountsOwn()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "logs"));
        File.WriteAllText(Path.Combine(_directory, "logs", "host.log"), "entry");
        File.WriteAllText(Path.Combine(_directory, "paired-devices.json"), "{}");

        var untrusted = DataDirectoryAcl.Secure(_directory, _trayUser);

        Assert.Empty(untrusted);
        Assert.True(File.Exists(Path.Combine(_directory, "logs", "host.log")));
        Assert.True(File.Exists(Path.Combine(_directory, "paired-devices.json")));
        Assert.Empty(Directory.GetDirectories(_directory, DataDirectoryAcl.QuarantinePrefix + "*"));
    }

    [Fact]
    public void Secure_MovesWhatAnotherAccountOwns_AsideWithItsContents()
    {
        // 2026-11-06: the installer deleted a portable host's certificate and pairings, which the signed-in user owned,
        // because it ran from another administrator's console and trusted only that account.
        Directory.CreateDirectory(Path.Combine(_directory, "logs"));
        File.WriteAllText(Path.Combine(_directory, "logs", "host.log"), "entry");
        File.WriteAllText(Path.Combine(_directory, "host-certificate.pfx.protected"), "identity");
        var owner = OwnerName(Path.Combine(_directory, "host-certificate.pfx.protected"));
        var now = new DateTimeOffset(2026, 11, 6, 16, 38, 29, TimeSpan.Zero);

        var untrusted = DataDirectoryAcl.Secure(_directory, _trayUser, OnlySystemTrusted(), now);

        var quarantine = Path.Combine(_directory, "untrusted-20261106-163829");
        Assert.Equal(
            new[] { ("host-certificate.pfx.protected", Path.Combine(quarantine, "host-certificate.pfx.protected")), ("logs", Path.Combine(quarantine, "logs")) },
            untrusted.Select(entry => (Path.GetRelativePath(_directory, entry.Path), entry.MovedTo!)).Order());
        Assert.All(untrusted, entry => Assert.Equal(owner, entry.Owner));
        Assert.False(File.Exists(Path.Combine(_directory, "host-certificate.pfx.protected")));
        Assert.Equal("identity", File.ReadAllText(Path.Combine(quarantine, "host-certificate.pfx.protected")));
        Assert.Equal("entry", File.ReadAllText(Path.Combine(quarantine, "logs", "host.log")));
    }

    [Fact]
    public void Secure_DeletesALinkAnotherAccountPlanted_WithoutFollowingIt()
    {
        var outside = Path.Combine(_directory + "-outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
        Directory.CreateDirectory(_directory);
        Junction.Create(Path.Combine(_directory, "logs"), outside);
        try
        {
            var untrusted = DataDirectoryAcl.Secure(_directory, _trayUser, OnlySystemTrusted(), DateTimeOffset.UtcNow);

            var entry = Assert.Single(untrusted);
            Assert.Null(entry.MovedTo);
            Assert.False(Directory.Exists(Path.Combine(_directory, "logs")));
            Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
            Assert.Empty(Directory.GetDirectories(_directory, DataDirectoryAcl.QuarantinePrefix + "*"));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>The test account owns the files it writes and cannot give them to another; trusting only SYSTEM makes them another account's.</summary>
    private static HashSet<SecurityIdentifier> OnlySystemTrusted() => [new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)];

    /// <summary>
    /// The account that owns a file the test wrote: the test account, or Administrators when the tests run elevated
    /// (CI), because Windows gives an elevated administrator's new files to that group.
    /// </summary>
    private static string OwnerName(string path) =>
        new FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(NTAccount))!.Value;
}
