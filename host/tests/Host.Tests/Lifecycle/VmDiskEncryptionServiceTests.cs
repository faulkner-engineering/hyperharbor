using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class VmDiskEncryptionServiceTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeGuest _guest = new();
    private readonly VmCredentialStore _credentials;
    private readonly VmJobStore _jobs;
    private readonly VmDiskEncryptionService _service;

    public VmDiskEncryptionServiceTests()
    {
        _credentials = new VmCredentialStore(_dataDirectory);
        _jobs = new VmJobStore(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
        _service = new VmDiskEncryptionService(_inventory, _credentials, _guest, _jobs, NullLogger<VmDiskEncryptionService>.Instance);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Running));
        _credentials.Save(VmId, new GuestCredential("hhadmin", "the-secret-password"));
    }

    public void Dispose()
    {
        _jobs.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Read_ReturnsTheGuestsVolumes()
    {
        _guest.Volumes = [new("C:", true, GuestEncryptionState.Encrypted, 100)];

        var state = await _service.ReadAsync(VmId, CancellationToken.None);

        Assert.Equal(VmId, state.VmId);
        Assert.Equal(GuestEncryptionState.Encrypted, Assert.Single(state.Volumes).State);
        Assert.Empty(state.Problems);
        Assert.Equal("hhadmin", _guest.LastUser);
    }

    [Fact]
    public async Task TurnOff_StartsAJob_ThatReportsTheVolumesAfterwards()
    {
        _guest.AfterTurnOff = [new("C:", true, GuestEncryptionState.Decrypting, 97), new("D:", false, GuestEncryptionState.Decrypted, 0)];

        var job = await _service.TurnOffAsync(VmId, UserId, null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);
        var done = _jobs.Get(job.Id, UserId)!;

        Assert.Equal(VmJobKind.TurnOffDiskEncryption, done.Kind);
        Assert.Equal(VmJobState.Succeeded, done.State);
        Assert.Equal(2, done.DiskEncryption!.Volumes.Count);
        Assert.Empty(done.DiskEncryption.Problems);
        Assert.True(_guest.TurnedOff);
    }

    [Fact]
    public async Task TurnOff_KeepsTheProblems_WhenSomeVolumeStarted()
    {
        _guest.AfterTurnOff = [new("C:", true, GuestEncryptionState.Encrypted, 100), new("D:", false, GuestEncryptionState.Decrypting, 80)];
        _guest.Problems = ["C: could not start decrypting (error 0x80310029)."];

        var job = await _service.TurnOffAsync(VmId, UserId, null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);
        var done = _jobs.Get(job.Id, UserId)!;

        Assert.Equal(VmJobState.Succeeded, done.State);
        Assert.Contains("0x80310029", Assert.Single(done.DiskEncryption!.Problems));
    }

    [Fact]
    public async Task TurnOff_Fails_WhenNothingCouldStart_WithoutThePassword()
    {
        _guest.AfterTurnOff = [new("C:", true, GuestEncryptionState.Encrypted, 100)];
        _guest.Problems = ["C: could not start decrypting for the-secret-password."];

        var job = await _service.TurnOffAsync(VmId, UserId, null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);
        var done = _jobs.Get(job.Id, UserId)!;

        Assert.Equal(VmJobState.Failed, done.State);
        Assert.Contains("could not start decrypting", done.ErrorDetail);
        Assert.DoesNotContain("the-secret-password", done.ErrorDetail);
    }

    [Fact]
    public async Task AVmThatIsOff_NotSetUp_OrLinux_IsRefusedBeforeAnyJob()
    {
        _inventory.SetState(VmId, VmState.Off);
        var off = await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.TurnOffAsync(VmId, UserId, null, CancellationToken.None));
        Assert.Equal(ContractInfo.ProblemCodes.VmNotRunning, off.Code);

        _inventory.SetState(VmId, VmState.Running);
        _credentials.Remove(VmId);
        var unprovisioned = await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.ReadAsync(VmId, CancellationToken.None));
        Assert.Equal(ContractInfo.ProblemCodes.VmNotProvisioned, unprovisioned.Code);

        _inventory.Vms[0] = FakeVmInventory.CreateVm(VmId, "Dev", VmState.Running, GuestOsFamily.Linux);
        var linux = await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.ReadAsync(VmId, CancellationToken.None));
        Assert.Contains("LUKS", linux.Message);
        Assert.False(_guest.TurnedOff);
    }

    [Fact]
    public void TheScriptsAnswer_MapsConversionStatus()
    {
        var report = PowerShellGuestDiskEncryption.Parse(JsonNode.Parse("""
            { "volumes": [
                { "mountPoint": "C:", "os": true, "status": 3, "percent": 42 },
                { "mountPoint": "D:", "os": false, "status": 1, "percent": 100 },
                { "mountPoint": "\\\\?\\Volume{1}\\", "os": false, "status": 5, "percent": 150 },
                { "mountPoint": "E:", "os": false, "status": 0, "percent": 0 } ],
              "problems": [ "D: could not start decrypting (error 0x80310029)." ] }
            """));

        Assert.Equal(
            [new GuestVolumeEncryption("C:", true, GuestEncryptionState.Decrypting, 42), new("D:", false, GuestEncryptionState.Encrypted, 100),
                new(@"\\?\Volume{1}\", false, GuestEncryptionState.Paused, 100), new("E:", false, GuestEncryptionState.Decrypted, 0)],
            report.Volumes);
        Assert.Single(report.Problems);
        Assert.Empty(PowerShellGuestDiskEncryption.Parse(null).Volumes);
    }

    [Fact]
    public void TheGuestScript_DecryptsDataVolumesBeforeTheWindowsVolume()
    {
        var script = PowerShellGuestDiskEncryption.Script;

        Assert.Contains("Win32_EncryptableVolume", script);
        Assert.Contains("Sort-Object { $_.VolumeType -eq 0 }", script);
        Assert.Contains("-MethodName Decrypt", script);
        Assert.DoesNotContain("Disable-BitLocker", script);
    }

    private sealed class FakeGuest : IGuestDiskEncryption
    {
        public IReadOnlyList<GuestVolumeEncryption> Volumes { get; set; } = [];

        public IReadOnlyList<GuestVolumeEncryption> AfterTurnOff { get; set; } = [];

        public IReadOnlyList<string> Problems { get; set; } = [];

        public bool TurnedOff { get; private set; }

        public string? LastUser { get; private set; }

        public Task<GuestEncryptionReport> ReadAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken)
        {
            LastUser = admin.UserName;
            return Task.FromResult(new GuestEncryptionReport(Volumes, []));
        }

        public Task<GuestEncryptionReport> TurnOffAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken)
        {
            TurnedOff = true;
            return Task.FromResult(new GuestEncryptionReport(AfterTurnOff, Problems));
        }
    }
}
