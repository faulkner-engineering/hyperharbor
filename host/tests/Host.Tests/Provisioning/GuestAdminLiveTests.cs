using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Provisioning;

/// <summary>
/// An administrator window in a real, running Windows VM that HyperHarbor has set up and holds an administrator
/// credential for. The credential and the VM's setup are read from the host's data folder (run elevated), or from
/// HH_GUEST_ADMIN_DATA (for example the live harness's folder). Window records go to a temporary folder, so the host's
/// own guest-admin.json is not touched. It creates the User's hha- account in the guest on first run and leaves it
/// there, disabled. HH_GUEST_ADMIN_LIVE=1 and HH_LIVE_GUEST_VM=&lt;VM id&gt;. About a minute.
/// </summary>
public sealed class GuestAdminLiveTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _records = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_records, recursive: true);

    [EnvironmentFact("HH_GUEST_ADMIN_LIVE", "HH_LIVE_GUEST_VM")]
    public async Task Live_OpenEnablesAnAdministratorOnly_CloseDisablesIt()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var data = Environment.GetEnvironmentVariable("HH_GUEST_ADMIN_DATA") is { Length: > 0 } folder ? folder : HostIdentityStore.DefaultDataDirectory;
        using var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddVmInventory().BuildServiceProvider();
        var users = new UserStore(data);
        var userId = users.GetOrCreateDefault().UserId;
        var credentials = new VmCredentialStore(data);
        var guest = new PowerShellDirectAccountManager();
        using var service = new GuestAdminService(
            services.GetRequiredService<IVmInventory>(),
            users,
            new ProvisioningStore(data),
            credentials,
            guest,
            new GuestAdminStore(_records),
            new NullAuditLog(),
            TimeProvider.System,
            NullLogger<GuestAdminService>.Instance);
        var target = new GuestTarget(vmId, GuestOsFamily.Windows);
        var admin = credentials.Find(vmId) ?? throw new InvalidOperationException($"No administrator credential is stored for {vmId} in {data}.");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var window = await service.OpenAsync(vmId, userId, new GuestAdminRequester(null, "live test"), CancellationToken.None);
        output.WriteLine($"Opened {window.AccountName} until {window.ExpiresAt:u} in {watch.ElapsedMilliseconds} ms.");

        var open = await guest.InspectAdminAsync(target, admin, window.AccountName, CancellationToken.None);
        output.WriteLine($"While open: {open}");
        Assert.True(open.Exists);
        Assert.True(open.Enabled);
        Assert.True(open.InAdministrators);
        Assert.False(open.InRemoteDesktopUsers);
        Assert.True(open.DeniedNetworkLogon);
        Assert.True(open.DeniedRemoteInteractiveLogon);

        watch.Restart();
        Assert.True(await service.CloseAsync(vmId, userId, CancellationToken.None));
        output.WriteLine($"Closed in {watch.ElapsedMilliseconds} ms.");

        var closed = await guest.InspectAdminAsync(target, admin, window.AccountName, CancellationToken.None);
        output.WriteLine($"After close: {closed}");
        Assert.True(closed.Exists);
        Assert.False(closed.Enabled);
        Assert.True(closed.InAdministrators);
    }

    private sealed class NullAuditLog : IAuditLog
    {
        public void Write(AuditEntry entry)
        {
        }
    }
}
