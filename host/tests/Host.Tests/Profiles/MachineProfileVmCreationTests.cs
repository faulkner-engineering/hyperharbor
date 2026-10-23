using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Host.Tests.Performance;
using HyperHarbor.Host.Tests.Unattend;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Backup;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Profiles;

/// <summary>Creating a VM from a machine profile: what the profile becomes on this host, and the steps while the VM is off.</summary>
public sealed class MachineProfileVmCreationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVBuilder _builder = new();
    private readonly FakeDiskFiles _files = new();
    private readonly FakeHyperVCompute _compute = new();
    private readonly FakeHyperVPerformance _performance = new();
    private readonly FakeBackupPolicies _backups = new();
    private readonly VmJobStore _jobs = new(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
    private readonly UnattendedInstallStore _installs;
    private readonly MachineProfileStore _profiles;
    private readonly MachineProfileVmCreation _creation;
    private readonly Guid _userId;

    public MachineProfileVmCreationTests()
    {
        var data = Path.Combine(_root, "data");
        var isos = Path.Combine(_root, "isos");
        TestIsos.Windows(Path.Combine(isos, "win11.iso"), "Windows 11 Home", "Windows 11 Pro");
        TestIsos.Ubuntu(Path.Combine(isos, "ubuntu.iso"));

        var users = new UserStore(data);
        _userId = users.GetOrCreateDefault().UserId;
        var credentials = new VmCredentialStore(data);
        _installs = new UnattendedInstallStore(data);
        var unattendProfiles = new UnattendProfileStore(data, () => "Central Standard Time");
        var unattended = new UnattendedSetup(
            unattendProfiles, new IsoInspector(), users, credentials, _installs,
            new FakePowerInvoker(), new FakeVmKeyboard(), TimeProvider.System, NullLogger<UnattendedSetup>.Instance, keyInterval: TimeSpan.Zero);

        // The performance step reads the new VM back; the fake builder always creates this ID.
        _inventory.Vms.Add(new Vm(_builder.CreatedVmId, "created", VmState.Off, null, null, null, 2, false, []));
        _compute.States[_builder.CreatedVmId] = new ComputeState(2, 4096, 8192, true, false, false, 1);
        _builder.OnDiskCreated = path => _files.Files.Add(path);

        var options = new LifecycleOptions { VmRootFolder = Path.Combine(_root, "vms") };
        var hyperV = new FakeHyperVHost();
        var vmCreation = new VmCreationService(
            _inventory, _builder, new FakeHyperVStorage(), hyperV, new FakeHostCapacity(), _files, new IsoLibrary(isos), _jobs, options,
            NullLogger<VmCreationService>.Instance, unattended: unattended);
        var performance = new VmPerformanceService(
            _inventory, _performance, _compute, new FakeHostGpuReader(), new FakeHostCapacity(), new PerformanceStore(data), credentials,
            new FakeGpuDriverSource(), new FakeGuestPerformanceSetup(), new VmOperationLocks(), _jobs, options,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T12:00:00Z")), NullLogger<VmPerformanceService>.Instance);
        var software = new ProfileValidator(Catalogs.Default);
        _profiles = new MachineProfileStore(data, new MachineProfileValidator(software, unattendProfiles));
        _creation = new MachineProfileVmCreation(
            _profiles, vmCreation, unattended, unattendProfiles, software, performance, _compute, hyperV, _backups,
            NullLogger<MachineProfileVmCreation>.Instance);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Save(MachineProfile profile) => _profiles.Create(_userId, profile).Id;

    private async Task<VmJobSnapshot> CreateAsync(string profileId, string iso = "win11.iso", bool acknowledge = false)
    {
        var job = await _creation.StartAsync(_userId, profileId, new CreateVmFromProfileRequest("Rig", iso, null, acknowledge), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);
        return _jobs.Get(job.Id, _userId)!;
    }

    [Fact]
    public async Task TheWholeProfile_BecomesTheVm_TheInstall_AndItsCare()
    {
        var id = Save(MachineProfileTests.Sample() with
        {
            Display = new MachineDisplay(new MachineGuestDisplay(GuestTheme.Dark, "#E39B2E")),
            Compute = new MachineCompute(4, new MachineMemory(8192, null, false), NestedVirtualization: true, DiskSizeGb: 128),
            Firmware = new MachineFirmware(Tpm: true, SecureBoot: false),
        });

        var job = await CreateAsync(id);

        Assert.Equal(VmJobState.Succeeded, job.State);
        var blueprint = _builder.Configured!;
        Assert.Equal((4, 8192L, 8192L, false, false), (blueprint.ProcessorCount, blueprint.StartupMemoryMb, blueprint.MaximumMemoryMb, blueprint.DynamicMemory, blueprint.SecureBoot));
        Assert.Equal(CimHyperVHost.DefaultSwitchId, blueprint.SwitchId);
        Assert.Contains(_compute.Applied, applied => applied.Change.NestedVirtualization == true && applied.Change.MacAddressSpoofing == false);
        Assert.True(_performance.Applied.ContainsKey(_builder.CreatedVmId));
        Assert.Equal((_builder.CreatedVmId, "nas-id"), (_backups.Set.Single().VmId, _backups.Set.Single().TargetId));

        var install = _installs.Find(_builder.CreatedVmId)!;
        Assert.True(install.GuestPerformanceSetup);
        Assert.Equal("machine-" + id, install.ProfileId);
        var tweaks = install.SetupProfile!.Tweaks!;
        Assert.Contains(tweaks, tweak => tweak.Id == "appearance.darkMode");
        Assert.Contains(tweaks, tweak => tweak.Registry?.Name == "AccentColor" && tweak.Registry.Value == "4281244643");
        Assert.Equal(["Valve.Steam", "discord"], install.SetupProfile.Install!.Select(item => item.Id));
    }

    [Fact]
    public async Task EmptySections_TakeTheCreateVmDefaults_AndNoInstall()
    {
        var id = Save(new MachineProfile("Bare", InstallOs.Windows));

        var job = await CreateAsync(id);

        Assert.Equal(VmJobState.Succeeded, job.State);
        var blueprint = _builder.Configured!;
        Assert.Equal((2, 4096L, 8192L, true, true), (blueprint.ProcessorCount, blueprint.StartupMemoryMb, blueprint.MaximumMemoryMb, blueprint.DynamicMemory, blueprint.SecureBoot));
        Assert.Null(blueprint.SeedIsoPath);
        Assert.Null(_installs.Find(_builder.CreatedVmId));
        Assert.Empty(_compute.Applied);
        Assert.Empty(_performance.Applied);
        Assert.Empty(_backups.Set);
    }

    [Fact]
    public async Task NamesTheHostLacks_AreErrorsOrWarnings_BeforeAnythingIsBuilt()
    {
        var unknownSwitch = Save(new MachineProfile("Switch", InstallOs.Windows, Network: new MachineNetwork("LAN")));
        var unknownTarget = Save(new MachineProfile("Backup", InstallOs.Windows, Backup: new MachineBackup(true, "Offsite")));

        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() => CreateAsync(unknownSwitch));
        Assert.Equal("network.switch", Assert.Single(error.Errors).Field);

        var warning = await Assert.ThrowsAsync<ResourceWarningsException>(() => CreateAsync(unknownTarget));
        Assert.Contains(warning.Warnings, issue => issue.Field == "backup.target");
        Assert.Empty(_builder.Steps);

        Assert.Equal(VmJobState.Succeeded, (await CreateAsync(unknownTarget, acknowledge: true)).State);
        Assert.Empty(_backups.Set);
    }

    [Fact]
    public async Task SoftwareWithoutAnAccount_IsAWarning()
    {
        var id = Save(new MachineProfile("Console", InstallOs.Windows, Install: [new ProfileItem("Git.Git")]));

        var warning = await Assert.ThrowsAsync<ResourceWarningsException>(() => CreateAsync(id));

        Assert.Contains(warning.Warnings, issue => issue.Field == "account");
    }

    [Fact]
    public async Task AWindowsProfile_DoesNotInstallLinux()
    {
        var id = Save(new MachineProfile("Win", InstallOs.Windows, Account: new MachineAccount()));

        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() => CreateAsync(id, "ubuntu.iso"));

        Assert.Contains(error.Errors, issue => issue.Field == "install.profileId");
    }

    [Fact]
    public void GuestDisplay_BecomesRegistryValuesInTheUsersAccount()
    {
        var warnings = new List<ValidationIssue>();

        var tweaks = MachineProfileVmCreation.GuestDisplayTweaks(
            new MachineGuestDisplay(GuestTheme.Light, "#112233", new MachineWallpaper("#0B0D10")), warnings);
        var image = MachineProfileVmCreation.GuestDisplayTweaks(new MachineGuestDisplay(Wallpaper: new MachineWallpaper(Image: "harbor.jpg")), warnings);

        var values = tweaks.Select(tweak => (tweak.Registry!.Name, tweak.Registry.Value)).ToList();
        Assert.Contains(("AppsUseLightTheme", "1"), values);
        Assert.Contains(("AccentColor", "4281541137"), values);
        Assert.Contains(("Background", "11 13 16"), values);
        Assert.Contains(("WallPaper", ""), values);
        Assert.Empty(image);
        Assert.Equal("display.guest.wallpaper.image", Assert.Single(warnings).Field);
    }

    private sealed class FakeBackupPolicies : IMachineBackupPolicies
    {
        public List<(Guid VmId, string TargetId, MachineBackup Backup)> Set { get; } = [];

        public string? FindTarget(string name) => string.Equals(name, "NAS", StringComparison.OrdinalIgnoreCase) ? "nas-id" : null;

        void IMachineBackupPolicies.Set(Guid vmId, string targetId, MachineBackup backup) => Set.Add((vmId, targetId, backup));
    }
}
