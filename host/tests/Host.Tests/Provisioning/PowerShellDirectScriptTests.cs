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

    /// <summary>
    /// The administrator script goes on the command line with -EncodedCommand, which Windows caps at 32,767 characters
    /// for the whole line; it carries a C# wrapper, so its size is checked.
    /// </summary>
    [Fact]
    public void AdminScript_FitsOnTheCommandLine()
    {
        Assert.InRange(PowerShellDirectRunner.Encode(PowerShellDirectAccountManager.AdminScript).Length, 1, 30_000);
    }

    [Fact]
    public void AdminScript_AddsToAdministrators_RemovesFromRemoteDesktopUsers_AndDeniesNetworkAndRemoteSignIn()
    {
        var script = PowerShellDirectAccountManager.AdminScript;

        Assert.Contains("Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $user", script, StringComparison.Ordinal);
        Assert.Contains("Remove-LocalGroupMember -SID 'S-1-5-32-555' -Member $user", script, StringComparison.Ordinal);
        Assert.Contains("@('SeDenyNetworkLogonRight', 'SeDenyRemoteInteractiveLogonRight')", script, StringComparison.Ordinal);

        // UAC needs interactive logon, so it must never be denied.
        Assert.DoesNotContain("SeDenyInteractiveLogonRight", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Member $user.SID", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminState_IsReadFromTheScriptReply()
    {
        var state = PowerShellDirectAccountManager.ParseAdminState(System.Text.Json.Nodes.JsonNode.Parse(
            """{"exists":true,"enabled":true,"inAdministrators":true,"inRemoteDesktopUsers":false,"deniedNetworkLogon":true,"deniedRemoteInteractiveLogon":true}"""));

        Assert.True(state.IsReadyForElevation);
        Assert.False(PowerShellDirectAccountManager.ParseAdminState(null).Exists);
    }
}
