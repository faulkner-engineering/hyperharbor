using System.Security.AccessControl;
using System.Security.Principal;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Tests.Installation;

public sealed class DataBackupTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _data;
    private readonly string _backup;

    public DataBackupTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _backup = Path.Combine(_root, "backup");
        File.WriteAllText(Path.Combine(_data, "users.json"), "users v1");
        ProtectedFile.WriteAllBytes(Path.Combine(_data, "host-certificate.pfx.protected"), "secret"u8);
        Directory.CreateDirectory(Path.Combine(_data, "profiles"));
        File.WriteAllText(Path.Combine(_data, "profiles", "a.json"), "profile");
        Directory.CreateDirectory(Path.Combine(_data, "logs"));
        File.WriteAllText(Path.Combine(_data, "logs", "host-20261004.log"), "log v1");
        File.WriteAllText(Path.Combine(_data, "audit.log"), "audit v1");
        Directory.CreateDirectory(Path.Combine(_data, DataBackup.UpdateFolderName));
        File.WriteAllText(Path.Combine(_data, ".users.json.0123.tmp"), "half written");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Copy_TakesTheDataButNotLogsTheAuditTrailUpdateWorkOrTemporaryFiles()
    {
        DataBackup.Copy(_data, _backup);

        Assert.Equal("users v1", File.ReadAllText(Path.Combine(_backup, "users.json")));
        Assert.Equal("profile", File.ReadAllText(Path.Combine(_backup, "profiles", "a.json")));
        Assert.True(File.Exists(Path.Combine(_backup, "host-certificate.pfx.protected")));
        Assert.False(Directory.Exists(Path.Combine(_backup, "logs")));
        Assert.False(File.Exists(Path.Combine(_backup, "audit.log")));
        Assert.False(Directory.Exists(Path.Combine(_backup, DataBackup.UpdateFolderName)));
        Assert.False(File.Exists(Path.Combine(_backup, ".users.json.0123.tmp")));
    }

    [Fact]
    public void Copy_KeepsAProtectedFilesAcl()
    {
        DataBackup.Copy(_data, _backup);

        var original = new FileInfo(Path.Combine(_data, "host-certificate.pfx.protected")).GetAccessControl();
        var copied = new FileInfo(Path.Combine(_backup, "host-certificate.pfx.protected")).GetAccessControl();
        Assert.True(copied.AreAccessRulesProtected);
        Assert.Equal(Rules(original), Rules(copied));
        Assert.DoesNotContain(Rules(copied), rule => rule.StartsWith(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value, StringComparison.Ordinal));
    }

    [Fact]
    public void Copy_RefusesAnExistingDestination()
    {
        Directory.CreateDirectory(_backup);

        Assert.Throws<IOException>(() => DataBackup.Copy(_data, _backup));
    }

    [Fact]
    public void Restore_PutsTheDataBack_AndKeepsLogsAndTheAuditTrail()
    {
        DataBackup.Copy(_data, _backup);
        File.WriteAllText(Path.Combine(_data, "users.json"), "users v2");
        File.WriteAllText(Path.Combine(_data, "paired-devices.json"), "added after the backup");
        Directory.Delete(Path.Combine(_data, "profiles"), recursive: true);
        File.WriteAllText(Path.Combine(_data, "logs", "host-20261004.log"), "log v2");
        File.WriteAllText(Path.Combine(_data, "audit.log"), "audit v2");

        DataBackup.Restore(_backup, _data);

        Assert.Equal("users v1", File.ReadAllText(Path.Combine(_data, "users.json")));
        Assert.False(File.Exists(Path.Combine(_data, "paired-devices.json")));
        Assert.Equal("profile", File.ReadAllText(Path.Combine(_data, "profiles", "a.json")));
        Assert.Equal("log v2", File.ReadAllText(Path.Combine(_data, "logs", "host-20261004.log")));
        Assert.Equal("audit v2", File.ReadAllText(Path.Combine(_data, "audit.log")));
        Assert.True(new FileInfo(Path.Combine(_data, "host-certificate.pfx.protected")).GetAccessControl().AreAccessRulesProtected);
    }

    private static List<string> Rules(FileSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => $"{rule.IdentityReference.Value} {rule.AccessControlType} {rule.FileSystemRights}")
            .Order()
            .ToList();
}
