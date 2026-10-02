using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Vms;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Provisioning;

/// <summary>
/// Opt-in checks of the real PowerShell Direct path. Set HH_LIVE_GUEST_VM to a VM ID, and for a
/// Windows guest also HH_LIVE_GUEST_ADMIN and HH_LIVE_GUEST_PASSWORD. Without them the tests do nothing.
/// </summary>
public class PowerShellDirectLiveTests(ITestOutputHelper output)
{
    private static readonly string? VmId = Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM");

    [Fact]
    public async Task NonWindowsOrUnreachableGuest_FailsWithTypedError()
    {
        if (VmId is null || Environment.GetEnvironmentVariable("HH_LIVE_GUEST_ADMIN") is not null)
        {
            return;
        }

        var manager = new PowerShellDirectAccountManager();
        var error = await Record.ExceptionAsync(() =>
            manager.InspectAsync(new GuestTarget(Guid.Parse(VmId), GuestOsFamily.Windows), new GuestCredential("Administrator", "not-a-real-password"), "hh-owner", CancellationToken.None));

        output.WriteLine($"{error?.GetType().Name}: {error?.Message}");
        Assert.True(error is GuestUnavailableException or GuestCredentialRejectedException, $"Unexpected: {error}");
    }

    [Fact]
    public async Task WindowsGuest_InspectsAccount()
    {
        var admin = Environment.GetEnvironmentVariable("HH_LIVE_GUEST_ADMIN");
        var password = Environment.GetEnvironmentVariable("HH_LIVE_GUEST_PASSWORD");
        if (VmId is null || admin is null || password is null)
        {
            return;
        }

        var state = await new PowerShellDirectAccountManager()
            .InspectAsync(new GuestTarget(Guid.Parse(VmId), GuestOsFamily.Windows), new GuestCredential(admin, password), "hh-owner", CancellationToken.None);

        output.WriteLine(state.ToString());
    }
}
