using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Turns BitLocker off in a real Windows guest through VmDiskEncryptionService and follows the decryption to the end,
/// then shuts the VM down and checks that the host-side probe no longer finds BitLocker in its disk. The VM's
/// administrator comes from the harness data folder (or HH_GUEST_ADMIN_DATA). Changes the guest: use a throwaway VM.
/// HH_DISK_ENCRYPTION_LIVE=1 and HH_LIVE_GUEST_VM=&lt;id&gt;; HH_DECRYPT_MINUTES limits the wait (default 90).
/// </summary>
public sealed class DiskEncryptionLiveTests(ITestOutputHelper output)
{
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [EnvironmentFact("HH_DISK_ENCRYPTION_LIVE", "HH_LIVE_GUEST_VM")]
    public async Task Live_TurningEncryptionOff_DecryptsTheGuest()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var data = Environment.GetEnvironmentVariable("HH_GUEST_ADMIN_DATA") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarborHarness");
        var limit = TimeSpan.FromMinutes(int.TryParse(Environment.GetEnvironmentVariable("HH_DECRYPT_MINUTES"), out var minutes) ? minutes : 90);
        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));
        var power = new CimHyperVPowerInvoker();
        using var jobs = new VmJobStore(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
        var service = new VmDiskEncryptionService(
            inventory, new VmCredentialStore(data), new PowerShellGuestDiskEncryption(), jobs, NullLogger<VmDiskEncryptionService>.Instance);

        if ((await inventory.GetAsync(vmId, default))?.State != VmState.Running)
        {
            await power.InvokeAsync(vmId, VmAction.Start, default);
        }

        // PowerShell Direct answers once Windows has started; until then the guest is unavailable.
        var before = await RetryAsync(() => service.ReadAsync(vmId, default), TimeSpan.FromMinutes(5));
        output.WriteLine("Before: " + string.Join("; ", before.Volumes));

        var job = await service.TurnOffAsync(vmId, UserId, null, default);
        await jobs.WhenFinished(job.Id);
        var done = jobs.Get(job.Id, UserId)!;
        output.WriteLine($"Job: {done.State} {done.ErrorDetail}; after: {string.Join("; ", done.DiskEncryption?.Volumes ?? [])}; problems: {string.Join(" ", done.DiskEncryption?.Problems ?? [])}");
        Assert.Equal(VmJobState.Succeeded, done.State);

        var started = DateTime.UtcNow;
        var state = done.DiskEncryption!;
        while (state.Volumes.Any(v => v.State is GuestEncryptionState.Decrypting or GuestEncryptionState.Encrypted) && DateTime.UtcNow - started < limit)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            state = await RetryAsync(() => service.ReadAsync(vmId, default), TimeSpan.FromMinutes(2));
            output.WriteLine($"{(DateTime.UtcNow - started).TotalMinutes:0.0} min: {string.Join("; ", state.Volumes)}");
        }

        Assert.All(state.Volumes, volume => Assert.Equal(GuestEncryptionState.Decrypted, volume.State));
        output.WriteLine($"Decrypted in {(DateTime.UtcNow - started).TotalMinutes:0.0} minutes.");

        await power.InvokeAsync(vmId, VmAction.Shutdown, default);
        for (var i = 0; i < 120 && (await inventory.GetAsync(vmId, default))?.State != VmState.Off; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        var disk = Assert.Single((await new CimHyperVDefinitionReader(new CimHyperVCompute()).ReadAsync(vmId, default)).Disks).Path;
        Assert.Equal(DiskEncryption.None, new VhdxEncryptionProbe().Probe(disk));
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> call, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return await call();
            }
            catch (GuestUnavailableException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
    }
}
