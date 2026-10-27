using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Changes a throwaway account's password in a running Windows guest with the real <see cref="PowerShellDirectAccountManager"/>:
/// from the right current password it is a change (which keeps the account's DPAPI data readable, verified by hand on
/// 2026-10-26: data protected before a change opened after it and not after a reset), and from a wrong one it falls
/// back to a reset. Opt-in: HH_PASSWORD_CHANGE_LIVE=1 with HH_LIVE_GUEST_VM=&lt;id&gt; of a VM the harness set up
/// (its administrator comes from the harness data folder, or HH_GUEST_ADMIN_DATA).
/// </summary>
public sealed class PasswordChangeLiveTests
{
    private const string Account = "hh-livechange";

    [EnvironmentFact("HH_PASSWORD_CHANGE_LIVE", "HH_LIVE_GUEST_VM")]
    public async Task AChangeFromTheCurrentPassword_IsAChange_AndAWrongOneResets()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var data = Environment.GetEnvironmentVariable("HH_GUEST_ADMIN_DATA") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarborHarness");
        var admin = new VmCredentialStore(data).Find(vmId) ?? throw new InvalidOperationException($"No administrator credential for {vmId} in {data}.");
        var target = new GuestTarget(vmId, GuestOsFamily.Windows);
        var manager = new PowerShellDirectAccountManager();

        var first = PasswordGenerator.Generate();
        var second = PasswordGenerator.Generate();
        var third = PasswordGenerator.Generate();
        await manager.ProvisionAsync(target, admin, Account, first, new GuestProvisionOptions(false, false), CancellationToken.None);

        var change = await manager.ChangePasswordAsync(target, admin, Account, first, second, CancellationToken.None);
        Assert.True(change.Changed, change.ResetReason);
        Assert.False((await manager.ChangePasswordAsync(target, admin, Account, "not-the-password-" + first, third, CancellationToken.None)).Changed);
        Assert.True((await manager.ChangePasswordAsync(target, admin, Account, third, first, CancellationToken.None)).Changed);
    }
}
