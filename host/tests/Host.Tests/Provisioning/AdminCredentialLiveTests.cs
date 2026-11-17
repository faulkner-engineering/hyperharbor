using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Vms;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Provisioning;

/// <summary>
/// The stored administrator credential against a real, running Windows VM that HyperHarbor holds one for. The
/// credential is read from the host's data folder (run elevated), or from HH_GUEST_ADMIN_DATA (for example the live
/// harness's folder). Use a throwaway VM: the test turns off password expiry for the stored administrator, and for a
/// moment creates a second administrator (hh-expiry-test) whose password must be changed at the next sign-in, which
/// Windows treats as expired, to see that PowerShell Direct refuses it the way the host expects. Each refused sign-in
/// counts toward the guest's account lockout (two here). HH_ADMIN_CREDENTIAL_LIVE=1 and HH_LIVE_GUEST_VM=&lt;VM id&gt;.
/// About a minute.
/// </summary>
public sealed class AdminCredentialLiveTests(ITestOutputHelper output)
{
    private const string ExpiredAccount = "hh-expiry-test";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [EnvironmentFact("HH_ADMIN_CREDENTIAL_LIVE", "HH_LIVE_GUEST_VM")]
    public async Task Live_TheGuestReportsItsAdministrator_RefusesAnExpiredPassword_AndTurnsExpiryOff()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var data = Environment.GetEnvironmentVariable("HH_GUEST_ADMIN_DATA") is { Length: > 0 } folder ? folder : HostIdentityStore.DefaultDataDirectory;
        var admin = new VmCredentialStore(data).Find(vmId) ?? throw new InvalidOperationException($"No administrator credential is stored for {vmId} in {data}.");
        var guest = new PowerShellDirectAccountManager();
        var target = new GuestTarget(vmId, GuestOsFamily.Windows);

        // As stored, without changing the account (unless the host made it: a managed account is kept from expiring).
        var before = await guest.CheckAdministratorAsync(target, admin, passwordNeverExpires: false, CancellationToken.None);
        output.WriteLine($"{admin.UserName} (managed: {admin.Managed}): {before}");
        Assert.True(before.IsAdministrator);

        var wrong = await Assert.ThrowsAsync<GuestCredentialRejectedException>(() =>
            guest.CheckAdministratorAsync(target, admin with { Password = admin.Password + "x", Managed = false }, false, CancellationToken.None));
        output.WriteLine($"A wrong password: {GuestErrors.Clean(wrong.Message, admin.Password)}");

        var expiredPassword = PasswordGenerator.Generate();
        await RunAsync(vmId, admin, "create", expiredPassword);
        try
        {
            var expired = await Assert.ThrowsAsync<GuestCredentialRejectedException>(() =>
                guest.CheckAdministratorAsync(target, new GuestCredential(ExpiredAccount, expiredPassword), false, CancellationToken.None));
            output.WriteLine($"An expired password: {GuestErrors.Clean(expired.Message, expiredPassword)}");
        }
        finally
        {
            await RunAsync(vmId, admin, "remove", expiredPassword);
        }

        var after = await guest.CheckAdministratorAsync(target, admin, passwordNeverExpires: true, CancellationToken.None);
        output.WriteLine($"After turning expiry off: {after}");
        Assert.True(after.IsAdministrator);
        Assert.Null(after.PasswordExpiresAt);
        Assert.Null(after.Problem);
    }

    private static Task RunAsync(Guid vmId, GuestCredential admin, string operation, string accountPassword) =>
        PowerShellDirectRunner.RunAsync(
            PowerShellDirectRunner.Encode(Script),
            new { vmId, admin.UserName, admin.Password, operation, accountName = ExpiredAccount, accountPassword },
            Timeout,
            CancellationToken.None);

    /// <summary>Creates or removes the second administrator. Same request and reply shape as the product's scripts.</summary>
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Secure([string]$text) { ConvertTo-SecureString $text -AsPlainText -Force }
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 4 }

        try {
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, (Secure $request.Password))
            $session = New-PSSession -VMId $request.vmId -Credential $credential
        }
        catch {
            Reply @{ ok = $false; stage = 'connect'; error = $_.Exception.Message }
            exit 0
        }

        try {
            Invoke-Command -Session $session -ArgumentList $request.operation, $request.accountName, (Secure $request.accountPassword) -ScriptBlock {
                param($operation, $name, [securestring]$password)
                $ErrorActionPreference = 'Stop'
                if (Get-LocalUser -Name $name -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $name }
                if ($operation -eq 'create') {
                    $user = New-LocalUser -Name $name -Password $password -Description 'HyperHarbor live test; safe to delete'
                    Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $user
                    # "User must change password at next logon": Windows treats the password as expired.
                    $account = [ADSI]"WinNT://$env:COMPUTERNAME/$name,user"
                    $account.PasswordExpired = 1
                    $account.SetInfo()
                }
            } | Out-Null
            Reply @{ ok = $true; result = $null }
        }
        catch {
            Reply @{ ok = $false; stage = 'guest'; error = $_.Exception.Message }
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        """;
}
