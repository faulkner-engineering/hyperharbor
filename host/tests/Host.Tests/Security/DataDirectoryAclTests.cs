using System.Security.AccessControl;
using System.Security.Principal;
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

        var removed = DataDirectoryAcl.Secure(_directory, _trayUser);

        Assert.Empty(removed);
        Assert.True(File.Exists(Path.Combine(_directory, "logs", "host.log")));
        Assert.True(File.Exists(Path.Combine(_directory, "paired-devices.json")));
    }
}
