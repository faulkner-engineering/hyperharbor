using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Host.Tests.Performance;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Profiles;

/// <summary>Applying a machine profile to an existing VM: the preview, what needs the VM off, and the job's order.</summary>
public sealed class MachineProfileApplierTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716");
    private const string Address = "192.168.0.50";

    private readonly string _directory = Directory.CreateTempSubdirectory("hh-apply-").FullName;
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVCompute _compute = new();
    private readonly FakeHyperVBuilder _builder = new();
    private readonly FakeHyperVPerformance _performance = new();
    private readonly FakeGuestPerformanceSetup _guestPerformance = new();
    private readonly FakeGuestProfileApplier _applier = new();
    private readonly SwitchingPower _power;
    private readonly FakeBackups _backups = new();
    private readonly FakeDefinitions _definitions = new();
    private readonly VmJobStore _jobs = new(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
    private readonly MachineProfileStore _profiles;
    private readonly MachineProfileApplier _service;
    private readonly VmConnectionDefaultsStore _connectionDefaults;
    private readonly Guid _userId;
    private bool _tpm;

    public MachineProfileApplierTests()
    {
        var users = new UserStore(_directory);
        _userId = users.GetOrCreateDefault().UserId;
        var credentials = new VmCredentialStore(_directory);
        credentials.Save(VmId, new GuestCredential("hhadmin", "secret"));
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Running, GuestOsFamily.Windows, Address));
        _compute.States[VmId] = new ComputeState(2, 4096, 8192, true, false, false, 1);
        _definitions.Current = () => new VmDefinition(VmId, "Dev Box", 2, null, _compute.States[VmId], true, null, _tpm, [], [], null, 128, 512);
        _power = new SwitchingPower(_inventory);

        var probe = new Unattend.UnattendedInstallWatcherTests.FakeProbe { Rdp = true };
        var unattendProfiles = new UnattendProfileStore(_directory, () => "Central Standard Time");
        var software = new ProfileValidator(Catalogs.Default);
        _profiles = new MachineProfileStore(_directory, new MachineProfileValidator(software, unattendProfiles));
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T12:00:00Z"));
        _connectionDefaults = new VmConnectionDefaultsStore(_directory);
        var performance = new VmPerformanceService(
            _inventory, _performance, _compute, new FakeHostGpuReader(), new FakeHostCapacity(), new PerformanceStore(_directory), credentials,
            new FakeGpuDriverSource(), _guestPerformance, new VmOperationLocks(), _jobs, new LifecycleOptions(), time, NullLogger<VmPerformanceService>.Instance);
        _service = new MachineProfileApplier(
            _profiles, _inventory, _compute, _definitions, _builder, performance, _backups, software, credentials, users,
            new UnattendedInstallStore(_directory), new SetupProfileApplication(new SetupProfilePlanner(Catalogs.Default), _applier),
            new GuestRestart(_inventory, probe, _power, TimeProvider.System) { Poll = TimeSpan.Zero, Checks = 3 },
            _power, _jobs, new LifecycleOptions { ShutdownTimeoutSeconds = 5 }, TimeProvider.System, NullLogger<MachineProfileApplier>.Instance,
            _connectionDefaults);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static MachineProfile Full() => new(
        "Workstation",
        InstallOs.Windows,
        Account: new MachineAccount(),
        Display: new MachineDisplay(new MachineGuestDisplay(GuestTheme.Dark)),
        Firmware: new MachineFirmware(Tpm: true, SecureBoot: false),
        Compute: new MachineCompute(4, new MachineMemory(8192, null, false), NestedVirtualization: true, DiskSizeGb: 256),
        Network: new MachineNetwork("LAN", MacAddressSpoofing: true),
        Backup: new MachineBackup(true, "NAS"),
        Install: [new ProfileItem("Git.Git")]);

    private string Save(MachineProfile profile) => _profiles.Create(_userId, profile).Id;

    private async Task<VmJobSnapshot> ApplyAsync(string profileId, MachinePart[] parts, bool shutDown = false)
    {
        var job = await _service.StartAsync(_userId, VmId, new ApplyMachineProfileRequest(profileId, parts, shutDown), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);
        return _jobs.Get(job.Id, _userId)!;
    }

    [Fact]
    public async Task ThePreview_ListsWhatDiffers_AndWhyTheRestIsLeft()
    {
        var preview = await _service.PreviewAsync(_userId, VmId, Save(Full()), CancellationToken.None);

        Assert.Equal(VmState.Running, preview.VmState);
        Assert.Equal(
            [
                (MachinePart.Compute, "Virtual processors", "2", "4", true),
                (MachinePart.Compute, "Memory", "4 GB dynamic, up to 8 GB", "8 GB fixed", true),
                (MachinePart.Compute, "Nested virtualization", "Off", "On", true),
                (MachinePart.Network, "MAC address spoofing", "Off", "On", false),
                (MachinePart.Firmware, "Virtual TPM", "Off", "On", true),
                (MachinePart.Backup, "Backup policy", null, "To NAS", false),
                (MachinePart.Software, "Software and display", null, "1 package, 0 removals, 1 setting", false),
            ],
            preview.Changes.Select(change => (change.Part, change.Setting, change.Current, change.Wanted, change.NeedsOff)));
        Assert.True(preview.Changes.Single(change => change.Part == MachinePart.Software).NeedsGuest);
        Assert.Equal(["compute", "network", "firmware", "account"], preview.Notes.Select(note => note.Section));
        Assert.Empty(preview.Warnings);
    }

    [Fact]
    public async Task ARunningVm_MustBeShutDown_ForPartsThatNeedItOff()
    {
        var id = Save(Full());

        var refused = await Assert.ThrowsAsync<LifecycleConflictException>(() => ApplyAsync(id, [MachinePart.Compute]));

        Assert.Equal(ContractInfo.ProblemCodes.RequiresShutdown, refused.Code);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task ShutDownToApply_ChangesTheHardware_StartsIt_ThenSetsUpTheGuest()
    {
        var id = Save(Full());

        var job = await ApplyAsync(id, [MachinePart.Compute, MachinePart.Firmware, MachinePart.Network, MachinePart.Backup, MachinePart.Software], shutDown: true);

        Assert.Equal(VmJobState.Succeeded, job.State);
        Assert.Equal([VmAction.Shutdown, VmAction.Start], _power.Calls.Select(call => call.Action));
        var state = _compute.States[VmId];
        Assert.Equal((4, 8192L, 8192L, false, true, true), (state.ProcessorCount, state.StartupMemoryMb, state.MaximumMemoryMb, state.DynamicMemory, state.NestedVirtualization, state.MacAddressSpoofing));
        Assert.Contains("tpm", _builder.Steps);
        Assert.Equal((VmId, "nas-id"), (_backups.Set.Single().VmId, _backups.Set.Single().TargetId));
        Assert.Contains("packages: Git", _applier.Calls);
        Assert.Contains("settings: Use dark mode", _applier.Calls);
        Assert.Empty(job.SetupResult!.Problems);
        Assert.True(job.SetupResult.Applied > 0);
    }

    [Fact]
    public async Task OnlyTheChosenParts_AreApplied_AndARunningVmIsNotStopped()
    {
        var job = await ApplyAsync(Save(Full()), [MachinePart.Software, MachinePart.Backup]);

        Assert.Equal(VmJobState.Succeeded, job.State);
        Assert.Empty(_power.Calls);
        Assert.Equal(2, _compute.States[VmId].ProcessorCount);
        Assert.DoesNotContain("tpm", _builder.Steps);
        Assert.Single(_backups.Set);
        Assert.Contains("packages: Git", _applier.Calls);
    }

    [Fact]
    public async Task AnOffVm_IsChangedOff_AndStaysOff_WhenNoGuestWorkIsChosen()
    {
        _inventory.SetState(VmId, VmState.Off);

        var job = await ApplyAsync(Save(Full()), [MachinePart.Compute]);

        Assert.Equal(VmJobState.Succeeded, job.State);
        Assert.Empty(_power.Calls);
        Assert.Equal(4, _compute.States[VmId].ProcessorCount);
    }

    [Fact]
    public async Task NothingThatDiffers_IsRefused()
    {
        var id = Save(new MachineProfile("Same", InstallOs.Windows, Compute: new MachineCompute(2)));

        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() => ApplyAsync(id, [MachinePart.Compute]));

        Assert.Equal("parts", Assert.Single(error.Errors).Field);
    }

    [Fact]
    public async Task RemoteDesktopDefaults_AreAChange_UntilTheVmHasThem()
    {
        var connection = new MachineConnection(ConnectionMonitors.All, ConnectionWindow.FullScreen, new MachineRedirect(Clipboard: true, Camera: false));
        var id = Save(new MachineProfile("Desk", InstallOs.Windows, Display: new MachineDisplay(Connection: connection)));

        var change = Assert.Single((await _service.PreviewAsync(_userId, VmId, id, CancellationToken.None)).Changes);
        Assert.Equal((MachinePart.Connection, (string?)null, "All monitors, full screen, clipboard shared, camera not shared", false),
            (change.Part, change.Current, change.Wanted, change.NeedsOff));

        var job = await ApplyAsync(id, [MachinePart.Connection]);

        Assert.Equal(VmJobState.Succeeded, job.State);
        Assert.Empty(_power.Calls);
        Assert.Equal(connection, _connectionDefaults.Find(VmId)!.Connection);
        Assert.Empty((await _service.PreviewAsync(_userId, VmId, id, CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task ATpmIsNeverRemoved()
    {
        _tpm = true;

        var preview = await _service.PreviewAsync(_userId, VmId, Save(new MachineProfile("No TPM", InstallOs.Windows, Firmware: new MachineFirmware(Tpm: false))), CancellationToken.None);

        Assert.Empty(preview.Changes);
        Assert.Contains("never removes", Assert.Single(preview.Notes).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerformanceMode_IsTurnedOnWhileOff_ThenTheGuestDriverIsSetUp()
    {
        var id = Save(new MachineProfile("Gaming", InstallOs.Windows, Gpu: new MachineGpu(true, Share: new MachineGpuShare(60, 50, 50, 50))));

        var preview = await _service.PreviewAsync(_userId, VmId, id, CancellationToken.None);
        var change = Assert.Single(preview.Changes);
        Assert.Equal((MachinePart.Gpu, "Performance mode", "Off"), (change.Part, change.Setting, change.Current));

        var job = await ApplyAsync(id, [MachinePart.Gpu], shutDown: true);

        Assert.Equal(VmJobState.Succeeded, job.State);
        Assert.True(_performance.Applied.ContainsKey(VmId));
        Assert.Equal([VmAction.Shutdown, VmAction.Start], _power.Calls.Select(call => call.Action));
        Assert.Single(_guestPerformance.Runs);
        Assert.Empty(job.SetupResult!.Problems);
    }

    [Fact]
    public async Task AMissingBackupTarget_IsAWarning_NotAChange()
    {
        var preview = await _service.PreviewAsync(_userId, VmId, Save(new MachineProfile("Offsite", InstallOs.Windows, Backup: new MachineBackup(true, "Offsite"))), CancellationToken.None);

        Assert.Empty(preview.Changes);
        Assert.Equal("backup.target", Assert.Single(preview.Warnings).Field);
    }

    /// <summary>Shutdown turns the VM off and Start runs it again, as the guest would.</summary>
    private sealed class SwitchingPower(FakeVmInventory inventory) : IHyperVPowerInvoker
    {
        public List<(Guid VmId, VmAction Action)> Calls { get; } = [];

        public Task InvokeAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
        {
            Calls.Add((vmId, action));
            if (action is VmAction.Shutdown or VmAction.TurnOff)
            {
                inventory.SetState(vmId, VmState.Off);
            }
            else if (action == VmAction.Start)
            {
                inventory.SetState(vmId, VmState.Running);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeBackups : IMachineBackupPolicies
    {
        public List<(Guid VmId, string TargetId)> Set { get; } = [];

        public string? FindTarget(string name) => string.Equals(name, "NAS", StringComparison.OrdinalIgnoreCase) ? "nas-id" : null;

        void IMachineBackupPolicies.Set(Guid vmId, string targetId, MachineBackup backup) => Set.Add((vmId, targetId));
    }
}
