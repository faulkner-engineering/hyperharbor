using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Tests.Provisioning;

public sealed class PowerShellDirectScriptTests
{
    /// <summary>
    /// Windows PowerShell cannot convert a SecurityIdentifier to the LocalPrincipal that -Member takes
    /// (CannotConvertArgumentNoMessage), which broke account setup on fresh Windows 11 guests. The user
    /// object converts.
    /// </summary>
    [Fact]
    public void RemoteDesktopUsers_IsJoinedWithTheUserObject_NotItsSid()
    {
        Assert.DoesNotContain("-Member $user.SID", PowerShellDirectAccountManager.Script, StringComparison.Ordinal);
        Assert.Contains("Add-LocalGroupMember -SID 'S-1-5-32-555' -Member $user }", PowerShellDirectAccountManager.Script, StringComparison.Ordinal);
    }
}
