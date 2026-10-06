using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Tests.Unattend;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Profiles;

public sealed class SetupProfileJobsTests : IDisposable
{
    private const string Address = "172.20.0.15";
    private static readonly Guid VmId = Guid.Parse("5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly UnattendedInstallWatcherTests.FakeProbe _probe = new() { Rdp = true };
    private readonly UnattendedInstallWatcherTests.RestartingPower _power;
    private readonly FakeGuestProfileApplier _applier = new();
    private readonly VmCredentialStore _credentials;
    private readonly SetupProfileStore _profiles;
    private readonly UnattendedInstallStore _installs;
    private readonly VmJobStore _jobs = new(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
    private readonly SetupProfileJobs _setup;
    private readonly Guid _userId;
    private readonly string _profileId;

    public SetupProfileJobsTests()
    {
        var users = new UserStore(_directory);
        _userId = users.GetOrCreateDefault().UserId;
        _credentials = new VmCredentialStore(_directory);
        _credentials.Save(VmId, new GuestCredential("hhadmin", "Admin-Pass1!"));
        _profiles = new SetupProfileStore(_directory, Catalogs.Default);
        _profileId = _profiles.Create(_userId, new SetupProfile(
            "Workstation", null, [new("7zip")], new(null, null, [new("WorkFolders-Client")]), [new("explorer.showFileExtensions")], null)).Id;
        _installs = new UnattendedInstallStore(_directory);
        _power = new UnattendedInstallWatcherTests.RestartingPower(_probe);
        var restart = new GuestRestart(_inventory, _probe, _power, TimeProvider.System) { Poll = TimeSpan.Zero, Checks = 4 };
        _setup = new SetupProfileJobs(
            _inventory, _credentials, _profiles, users, _installs, new SetupProfileApplication(new SetupProfilePlanner(Catalogs.Default), _applier),
            restart, _jobs, TimeProvider.System, NullLogger<SetupProfileJobs>.Instance);
        Guest(VmState.Running, GuestOsFamily.Windows);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private void Guest(VmState state, GuestOsFamily os)
    {
        _inventory.Vms.Clear();
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", state, os, Address));
    }

    private async Task<VmJobSnapshot> RunAsync(bool restartIfNeeded = true, Action<VmJobSnapshot>? onFinished = null)
    {
        var job = await _setup.StartAsync(VmId, _userId, new ApplySetupProfileRequest(_profileId, restartIfNeeded), onFinished, CancellationToken.None);
        Assert.Equal(VmJobKind.ApplySetupProfile, job.Kind);
        await _jobs.WhenFinished(job.Id);
        return _jobs.Get(job.Id, _userId)!;
    }

    [Fact]
    public async Task AProfile_IsApplied_WithTheUsersAccount_AndTheResultIsOnTheJob()
    {
        var job = await RunAsync();

        Assert.Equal(VmJobState.Succeeded, job.State);
        Assert.Equal(["packages: 7-Zip", "remove: WorkFolders-Client", "settings: Show file name extensions"], _applier.Calls);
        // Settings for the account go to its own hive as well as the Default profile.
        Assert.Equal(["hh-owner"], _applier.UserAccounts);
        var result = job.SetupResult!;
        Assert.Equal((3, false, false), (result.Applied, result.Restarted, result.RestartPending));
        Assert.Empty(result.Problems);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task ARestartThatIsNeeded_IsDone_AndWaitsForRemoteDesktop()
    {
        _applier.Restart = "WorkFolders-Client";
        _power.Answers = [true, false, true];

        var job = await RunAsync();

        Assert.Equal([VmAction.Restart], _power.Calls);
        Assert.True(job.SetupResult!.Restarted);
        Assert.False(job.SetupResult.RestartPending);
    }

    [Fact]
    public async Task WithoutRestartIfNeeded_TheRestartIsLeftPending()
    {
        _applier.Restart = "WorkFolders-Client";

        var job = await RunAsync(restartIfNeeded: false);

        Assert.Empty(_power.Calls);
        Assert.Equal((false, true), (job.SetupResult!.Restarted, job.SetupResult.RestartPending));
    }

    [Fact]
    public async Task AGuestThatCannotBeReached_FailsTheJob_WithoutThePassword()
    {
        _applier.Failure = new GuestUnavailableException("PowerShell Direct could not reach the guest with Admin-Pass1!.");

        var job = await RunAsync();

        Assert.Equal(VmJobState.Failed, job.State);
        Assert.Contains("could not reach the guest", job.ErrorDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("Admin-Pass1!", job.ErrorDetail, StringComparison.Ordinal);
        Assert.Null(job.SetupResult);
    }

    [Fact]
    public async Task OnlyARunningWindowsGuest_WithACredential_AndNoInstallInProgress_IsAccepted()
    {
        Task Start() => _setup.StartAsync(VmId, _userId, new ApplySetupProfileRequest(_profileId), null, CancellationToken.None);

        Guest(VmState.Off, GuestOsFamily.Windows);
        await Assert.ThrowsAsync<LifecycleConflictException>(Start);
        Guest(VmState.Running, GuestOsFamily.Linux);
        await Assert.ThrowsAsync<LifecycleConflictException>(Start);

        Guest(VmState.Running, GuestOsFamily.Windows);
        var now = DateTimeOffset.UtcNow;
        _installs.Save(new UnattendedInstall(VmId, _userId, "windows-workstation", InstallOs.Windows, false, "seed.iso",
            UnattendedInstallState.WaitingForRemoteAccess, "Waiting", now, now));
        Assert.Contains("still being installed", (await Assert.ThrowsAsync<LifecycleConflictException>(Start)).Message, StringComparison.Ordinal);
        _installs.Save(_installs.Find(VmId)! with { State = UnattendedInstallState.Ready });

        _credentials.Remove(VmId);
        Assert.Equal(ContractInfo.ProblemCodes.CredentialRequired, (await Assert.ThrowsAsync<LifecycleConflictException>(Start)).Code);

        await Assert.ThrowsAsync<SetupProfileNotFoundException>(() =>
            _setup.StartAsync(VmId, _userId, new ApplySetupProfileRequest("missing"), null, CancellationToken.None));
        Assert.Empty(_applier.Calls);
    }
}
