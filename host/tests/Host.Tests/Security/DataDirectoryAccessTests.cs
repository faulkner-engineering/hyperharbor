using System.Security.AccessControl;
using System.Security.Principal;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Tests.Security;

/// <summary>
/// The test account stays the owner of the files it locks, and an owner may always read and change an ACL,
/// so these tests can lock and repair files without administrator rights.
/// </summary>
public sealed class DataDirectoryAccessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly SecurityIdentifier _currentUser = CurrentUser();

    public DataDirectoryAccessTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            new FileInfo(file).SetAccessControl(ProtectedFile.CreateSecurity());
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void FindUnreadable_ListsFilesThisAccountCannotOpen()
    {
        var locked = Path.Combine(_directory, "users.json");
        var readable = Path.Combine(_directory, "data-format.json");
        var nested = Path.Combine(_directory, "logs", "host.log");
        ProtectedFile.WriteAllBytes(readable, "{}"u8);
        ProtectedFile.WriteAllBytes(locked, "[]"u8);
        ProtectedFile.WriteAllBytes(nested, "entry"u8);
        LockToSystem(locked);
        LockToSystem(nested);

        var unreadable = DataDirectoryAccess.FindUnreadable(_directory);

        Assert.Equal(new[] { locked, nested }.Order(), unreadable.Order());
    }

    [Fact]
    public void FindUnreadable_IsEmptyForAMissingFolder()
    {
        Assert.Empty(DataDirectoryAccess.FindUnreadable(Path.Combine(_directory, "missing")));
    }

    [Fact]
    public void GrantHostAccount_RestoresAccess_AndKeepsTheAclProtected()
    {
        var locked = Path.Combine(_directory, "console-accounts.json.protected");
        ProtectedFile.WriteAllBytes(locked, "secret"u8);
        LockToSystem(locked);

        var changed = DataDirectoryAccess.GrantHostAccount(_directory, _currentUser);

        Assert.Equal([locked], changed);
        Assert.Empty(DataDirectoryAccess.FindUnreadable(_directory));
        var security = new FileInfo(locked).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(_currentUser) && rule.FileSystemRights == FileSystemRights.FullControl);
        Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
    }

    [Fact]
    public void GrantHostAccount_LeavesFilesThatAlreadyGrantTheAccount()
    {
        ProtectedFile.WriteAllBytes(Path.Combine(_directory, "users.json"), "[]"u8);

        Assert.Empty(DataDirectoryAccess.GrantHostAccount(_directory, _currentUser));
    }

    private static void LockToSystem(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }
}
