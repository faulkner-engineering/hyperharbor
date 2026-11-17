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

    /// <summary>
    /// The administrator account's own password: expiry is turned off only when asked (a managed account, or the
    /// user's choice), and the date is reported either way. It runs after a password operation without failing it.
    /// </summary>
    [Fact]
    public void TheAdministratorsExpiry_IsTurnedOffOnlyWhenAsked_AndReportedAfterPasswordOperations()
    {
        var script = PowerShellDirectAccountManager.Script;

        Assert.Contains("if ($keep -and $user.PasswordExpires) {", script, StringComparison.Ordinal);
        Assert.Contains("Set-LocalUser -SID $user.SID -PasswordNeverExpires $true -ErrorAction Stop", script, StringComparison.Ordinal);
        Assert.Contains("'checkAdmin' {", script, StringComparison.Ordinal);
        Assert.Equal(2, script.Split("administrator = (AdministratorAfter ([bool]$request.keepAdminPassword))").Length - 1);
        Assert.Contains("catch { $null }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdministrator_IsReadFromTheScriptReply()
    {
        var expiring = PowerShellDirectAccountManager.ParseAdministrator(System.Text.Json.Nodes.JsonNode.Parse(
            """{"isAdministrator":true,"passwordExpires":"2026-12-04T22:06:35.0439036Z","error":""}"""));
        var never = PowerShellDirectAccountManager.ParseAdministrator(System.Text.Json.Nodes.JsonNode.Parse(
            """{"isAdministrator":false,"passwordExpires":"","error":"Access is denied."}"""));

        Assert.Equal(new DateTimeOffset(2026, 12, 4, 22, 6, 35, TimeSpan.Zero).AddTicks(439036), expiring!.PasswordExpiresAt);
        Assert.True(expiring.IsAdministrator);
        Assert.Null(expiring.Problem);
        Assert.Equal(new GuestAdministrator(false, null, "Access is denied."), never);

        // A password operation whose extra step failed reports nothing rather than a wrong answer.
        Assert.Null(PowerShellDirectAccountManager.ParseAdministrator(null));
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
