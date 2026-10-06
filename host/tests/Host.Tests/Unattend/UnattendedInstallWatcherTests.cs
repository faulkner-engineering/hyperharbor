using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Unattend;

public sealed class UnattendedInstallWatcherTests : IDisposable
{
    private const string Address = "172.20.0.15";
    private const string OneTimePassword = "One-Time-Pass1!";
    private static readonly Guid VmId = Guid.Parse("5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-04T12:00:00Z"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeGuestAccountManager _guest = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeMedia _media = new();
    private readonly UnattendedInstallStore _installs;
    private readonly VmCredentialStore _credentials;
    private readonly ProvisioningStore _accounts;
    private readonly ProvisioningService _provisioning;
    private readonly InstallWatcherOptions _options = new();
    private readonly Guid _userId;
    private readonly string _seedPath;

    public UnattendedInstallWatcherTests()
    {
        var users = new UserStore(_directory);
        _userId = users.GetOrCreateDefault().UserId;
        _installs = new UnattendedInstallStore(_directory);
        _credentials = new VmCredentialStore(_directory);
        _accounts = new ProvisioningStore(_directory);
        _provisioning = new ProvisioningService(_inventory, users, _guest, _credentials, _accounts, _time, NullLogger<ProvisioningService>.Instance);
        _seedPath = Path.Combine(_directory, "Dev Box.hyperharbor-seed.iso");
        File.WriteAllBytes(_seedPath, [1]);
        _credentials.Save(VmId, new GuestCredential("hhadmin", OneTimePassword));
        _power = new RestartingPower(_probe);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private readonly Profiles.FakeGuestProfileReader _profileReader = new();
    private AppxBaselineStore? _baselines;

    private readonly Profiles.FakeGuestProfileApplier _applier = new();
    private readonly RestartingPower _power;
    private readonly List<AuditEntry> _audit = [];

    private UnattendedInstallWatcher Watcher(bool recordBaselines = false, bool applyProfiles = false)
    {
        AppxInventoryService? appx = null;
        if (recordBaselines)
        {
            _baselines = new AppxBaselineStore(_directory);
            appx = new AppxInventoryService(_inventory, _credentials, _profileReader, _baselines, Catalogs.Default, _time, NullLogger<AppxInventoryService>.Instance);
        }

        if (!applyProfiles)
        {
            return new(
                _installs, _inventory, _probe, _provisioning, _accounts, _credentials, _guest, _media, _time, _options,
                NullLogger<UnattendedInstallWatcher>.Instance, appx);
        }

        return new(
            _installs, _inventory, _probe, _provisioning, _accounts, _credentials, _guest, _media, _time, _options,
            NullLogger<UnattendedInstallWatcher>.Instance, appx,
            new SetupProfileApplication(new SetupProfilePlanner(Catalogs.Default), _applier),
            new GuestRestart(_inventory, _probe, _power, _time) { Poll = TimeSpan.Zero, Checks = 4 },
            new VmOperationLocks(),
            new ListAuditLog(_audit));
    }

    private static readonly SetupProfile Workstation = new(
        "Workstation",
        null,
        [new("7zip")],
        new([new("Microsoft.BingNews")], null, [new("WorkFolders-Client")]),
        [new("explorer.showFileExtensions")],
        null);

    private void Begin(InstallOs os = InstallOs.Windows, bool desktop = false, SetupProfile? profile = null) =>
        _installs.Save(new UnattendedInstall(VmId, _userId, "windows-workstation", os, desktop, _seedPath,
            os == InstallOs.Linux ? UnattendedInstallState.AwaitingConfirmation : UnattendedInstallState.Installing,
            "Installing", _time.GetUtcNow(), _time.GetUtcNow(), SetupProfile: profile));

    private void Guest(VmState state, GuestOsFamily os = GuestOsFamily.Unknown, string? address = null)
    {
        _inventory.Vms.Clear();
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", state, os, address));
    }

    private async Task TickAsync(UnattendedInstallWatcher watcher, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(15));
            await watcher.TickAsync(CancellationToken.None);
            await watcher.WhenConfiguredAsync();
        }
    }

    private UnattendedInstall Install => _installs.Find(VmId)!;

    [Fact]
    public async Task AFinishedWindowsInstall_RecordsTheCleanAppxBaseline_OnceForItsBuild()
    {
        Begin();
        var watcher = Watcher(recordBaselines: true);
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = true;

        await TickAsync(watcher, times: 2);

        Assert.Equal(UnattendedInstallState.Ready, Install.State);
        var (baseline, approximate) = _baselines!.Find("26100", "Professional")!.Value;
        Assert.False(approximate);
        Assert.Equal(AppxBaselineSource.UnattendedInstall, baseline.Source.How);
        Assert.Contains("Microsoft.BingNews", baseline.Packages);
        // The baseline is read with the rotated administrator password, not the one from the answer file.
        Assert.Equal(_credentials.Find(VmId)!.Password, Assert.Single(_profileReader.AdminsUsed).Password);
    }

    [Fact]
    public async Task ABaselineThatCannotBeRead_DoesNotFailTheInstall()
    {
        Begin();
        _profileReader.Failure = new GuestUnavailableException("PowerShell Direct did not respond in time.");
        var watcher = Watcher(recordBaselines: true);
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = true;

        await TickAsync(watcher, times: 2);

        Assert.Equal(UnattendedInstallState.Ready, Install.State);
        Assert.Null(_baselines!.Find("26100", "Professional"));
    }

    [Fact]
    public async Task VmIsMarkedProvisionedOnlyAfterRdpAnswers()
    {
        Begin();
        var watcher = Watcher();

        // Setup is running: no OS reported yet.
        Guest(VmState.Running);
        await TickAsync(watcher);
        Assert.Equal(UnattendedInstallState.Installing, Install.State);

        // Windows runs and has an address, and port 3389 accepts connections but does not answer RDP yet.
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = false;
        await TickAsync(watcher, times: 10);
        Assert.Equal(UnattendedInstallState.WaitingForRemoteAccess, Install.State);
        Assert.Null(_accounts.Find(VmId, _userId));
        Assert.Empty(_guest.PasswordsSet);
        Assert.Empty(_media.Ejected);

        // Remote Desktop answers: the account is set up, the admin password replaced, the seed removed.
        _probe.Rdp = true;
        await TickAsync(watcher);
        Assert.Equal(UnattendedInstallState.Ready, Install.State);
        Assert.NotNull(_accounts.Find(VmId, _userId));
        Assert.Contains(_guest.PasswordsSet, set => set.Account == "hh-owner");
        var rotated = Assert.Single(_guest.PasswordsSet, set => set.Account == "hhadmin");
        Assert.Equal(rotated.Password, _credentials.Find(VmId)!.Password);
        Assert.NotEqual(OneTimePassword, rotated.Password);
        Assert.Equal([(VmId, _seedPath)], _media.Ejected);
        Assert.False(File.Exists(_seedPath));
    }

    [Fact]
    public async Task RemoteDesktopThatStopsAnsweringDuringSetup_IsRetriedNotMarkedReady()
    {
        Begin();
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.RdpAnswers.Enqueue(true);
        _probe.RdpAnswers.Enqueue(false);

        await TickAsync(Watcher());

        Assert.Equal(UnattendedInstallState.WaitingForRemoteAccess, Install.State);
        Assert.Equal(1, Install.Attempts);
        Assert.Null(_accounts.Find(VmId, _userId));
        Assert.Contains("does not answer yet", Install.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedSetups_BackOffThenFail()
    {
        Begin();
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = true;
        _guest.Failure = new GuestUnavailableException("PowerShell Direct could not reach the guest.");
        var watcher = Watcher();

        await TickAsync(watcher);
        Assert.Equal(1, Install.Attempts);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(1), Install.NextAttemptAt);

        // Nothing is tried again before the back-off ends.
        await TickAsync(watcher);
        Assert.Equal(1, Install.Attempts);

        for (var attempt = 0; attempt < 10 && Install.IsActive; attempt++)
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            await TickAsync(watcher);
        }

        Assert.Equal(UnattendedInstallState.Failed, Install.State);
        Assert.Equal(_options.MaxAttempts, Install.Attempts);
        Assert.Contains("PowerShell Direct could not reach the guest", Install.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedCredential_FailsAtOnceWithoutTheSecret()
    {
        Begin();
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = true;
        _guest.Failure = new GuestCredentialRejectedException($"Logon failure for hhadmin with {OneTimePassword}.");

        await TickAsync(Watcher());

        Assert.Equal(UnattendedInstallState.Failed, Install.State);
        Assert.DoesNotContain(OneTimePassword, Install.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeWhileOff_DoesNotCountTowardTheTimeout()
    {
        _options.TimeoutMinutes = 1;
        Begin();
        var watcher = Watcher();

        Guest(VmState.Off);
        await TickAsync(watcher, times: 20);
        Assert.Equal(UnattendedInstallState.Installing, Install.State);
        Assert.Equal(0, Install.RunningSeconds);
        Assert.Contains("is off", Install.Step, StringComparison.Ordinal);

        Guest(VmState.Running);
        await TickAsync(watcher, times: 6);
        Assert.Equal(UnattendedInstallState.Failed, Install.State);
        Assert.Contains("1 minutes of running time", Install.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_WaitsForSsh_ThenSetsUpTheDesktopAndRequiresRemoteDesktop()
    {
        Begin(InstallOs.Linux, desktop: true);
        var watcher = Watcher();

        Guest(VmState.Running);
        await TickAsync(watcher);
        Assert.Equal(UnattendedInstallState.AwaitingConfirmation, Install.State);
        Assert.Contains("type yes", Install.Step, StringComparison.Ordinal);

        Guest(VmState.Running, GuestOsFamily.Linux, Address);
        _probe.Rdp = true;
        _probe.Ssh = false;
        await TickAsync(watcher);
        Assert.Equal(UnattendedInstallState.WaitingForRemoteAccess, Install.State);
        Assert.Equal("Waiting for SSH to answer", Install.Step);

        _probe.Ssh = true;
        await TickAsync(watcher);
        Assert.Equal(UnattendedInstallState.Ready, Install.State);
        Assert.True(Assert.Single(_guest.ProvisionOptions).InstallDesktop);
        Assert.Equal(FakeGuestAccountManager.FakeHostKey, _accounts.Find(VmId, _userId)!.SshHostKey);
    }

    [Fact]
    public async Task LinuxWithoutDesktop_RotatesThePasswordAndFinishesWithoutRemoteDesktop()
    {
        Begin(InstallOs.Linux, desktop: false);
        Guest(VmState.Running, GuestOsFamily.Linux, Address);
        _probe.Ssh = true;
        _probe.Rdp = false;

        await TickAsync(Watcher());

        Assert.Equal(UnattendedInstallState.Ready, Install.State);
        Assert.Null(_accounts.Find(VmId, _userId));
        var rotated = Assert.Single(_guest.PasswordsSet);
        Assert.Equal("hhadmin", rotated.Account);
        Assert.Equal(rotated.Password, _credentials.Find(VmId)!.Password);
    }

    [Fact]
    public async Task ARestartedHost_ResumesFromTheStore()
    {
        Begin();
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = false;
        await TickAsync(Watcher(), times: 3);

        _probe.Rdp = true;
        await TickAsync(Watcher());

        Assert.Equal(UnattendedInstallState.Ready, Install.State);
    }

    [Fact]
    public async Task Cancel_StopsFollowingAndRemovesTheSeed()
    {
        Begin();
        Guest(VmState.Running);
        var watcher = Watcher();

        Assert.True(await watcher.CancelAsync(VmId, CancellationToken.None));

        Assert.Equal(UnattendedInstallState.Canceled, Install.State);
        Assert.Equal([(VmId, _seedPath)], _media.Ejected);
        Assert.False(File.Exists(_seedPath));
        Assert.False(await watcher.CancelAsync(VmId, CancellationToken.None));
        Assert.NotNull(_credentials.Find(VmId));
    }

    [Fact]
    public async Task DeletedVm_FailsTheInstall()
    {
        Begin();

        await TickAsync(Watcher());

        Assert.Equal(UnattendedInstallState.Failed, Install.State);
    }

    [Fact]
    public async Task ASetupProfile_IsAppliedAfterTheAccount_ThenTheInstallIsReady()
    {
        Begin(profile: Workstation);
        var watcher = Watcher(recordBaselines: true, applyProfiles: true);
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = true;
        var statesSeen = new List<UnattendedInstallState>();
        _applier.OnCall = _ => statesSeen.Add(Install.State);

        await TickAsync(watcher, times: 2);

        Assert.Equal(UnattendedInstallState.Ready, Install.State);
        Assert.Equal("Ready", Install.Step);
        Assert.True(Install.AccountConfigured);
        Assert.All(statesSeen, state => Assert.Equal(UnattendedInstallState.ApplyingProfile, state));
        Assert.Equal(["packages: 7-Zip", "remove: Microsoft.BingNews, WorkFolders-Client", "settings: Show file name extensions"], _applier.Calls);
        // The steps run as the rotated administrator, and the clean baseline was read before anything changed.
        Assert.All(_applier.AdminsUsed, admin => Assert.Equal(_credentials.Find(VmId)!.Password, admin.Password));
        Assert.NotNull(_baselines!.Find("26100", "Professional"));
        Assert.True(_profileReader.FirstReadOrder < _applier.FirstCallOrder);

        var result = Install.SetupResult!;
        Assert.Equal((4, false), (result.Applied, result.Restarted));
        Assert.Empty(result.Problems);
        Assert.Empty(_power.Calls);
        var entry = Assert.Single(_audit);
        Assert.Equal(("applySetupProfile", AuditOutcome.Succeeded, (Guid?)VmId, (Guid?)_userId), (entry.Action, entry.Outcome, entry.VmId, entry.UserId));
        Assert.Equal("profile=Workstation, applied=4, problems=0, restarted=False", entry.Detail);
    }

    [Fact]
    public async Task ItemsThatFail_AreProblems_AndARestartWaitsForRemoteDesktopToComeBack()
    {
        Begin(profile: Workstation);
        var watcher = Watcher(applyProfiles: true);
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = true;
        _applier.Failing["7-Zip"] = "winget install failed (0x8A150011) as hhadmin with " + OneTimePassword;
        _applier.Restart = "WorkFolders-Client";
        _power.Answers = [true, false, true];

        await TickAsync(watcher, times: 2);

        Assert.Equal(UnattendedInstallState.Ready, Install.State);
        Assert.Equal("Ready; 1 items of Workstation could not be applied", Install.Step);
        Assert.Equal([VmAction.Restart], _power.Calls);
        var result = Install.SetupResult!;
        Assert.True(result.Restarted);
        Assert.Equal(3, result.Applied);
        var problem = Assert.Single(result.Problems);
        Assert.StartsWith("7-Zip: winget install failed", problem, StringComparison.Ordinal);
        Assert.Equal(AuditOutcome.Failed, Assert.Single(_audit).Outcome);
    }

    [Fact]
    public async Task ARestartAfterWhichRemoteDesktopNeverReturns_IsAProblem_NotAHang()
    {
        Begin(profile: Workstation);
        var watcher = Watcher(applyProfiles: true);
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = true;
        _applier.Restart = "WorkFolders-Client";
        _power.Answers = [false];

        await TickAsync(watcher, times: 2);

        Assert.Equal(UnattendedInstallState.Ready, Install.State);
        Assert.False(Install.SetupResult!.Restarted);
        Assert.Contains("did not answer Remote Desktop", Assert.Single(Install.SetupResult.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGuestThatStopsAnsweringWhileApplying_IsRetried_WithoutSettingUpTheAccountAgain()
    {
        Begin(profile: Workstation);
        var watcher = Watcher(applyProfiles: true);
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = true;
        _applier.Failure = new GuestUnavailableException("PowerShell Direct did not respond in time.");

        await TickAsync(watcher, times: 2);

        Assert.Equal(UnattendedInstallState.WaitingForRemoteAccess, Install.State);
        Assert.True(Install.AccountConfigured);
        Assert.Equal(1, Install.Attempts);
        Assert.Contains("did not respond in time", Install.Error, StringComparison.Ordinal);
        var passwordsSet = _guest.PasswordsSet.Count;

        _applier.Failure = null;
        _time.Advance(TimeSpan.FromMinutes(2));
        await TickAsync(watcher);

        Assert.Equal(UnattendedInstallState.Ready, Install.State);
        Assert.Equal(passwordsSet, _guest.PasswordsSet.Count);
        Assert.NotNull(Install.SetupResult);
    }

    [Fact]
    public async Task ARejectedAdministratorWhileApplying_FailsTheInstall()
    {
        Begin(profile: Workstation);
        var watcher = Watcher(applyProfiles: true);
        Guest(VmState.Running, GuestOsFamily.Windows, Address);
        _probe.Rdp = true;
        _applier.Failure = new GuestCredentialRejectedException("The user name or password is incorrect.");

        await TickAsync(watcher, times: 2);

        Assert.Equal(UnattendedInstallState.Failed, Install.State);
        Assert.Null(Install.SetupResult);
    }

    /// <summary>A restart makes the probe give <see cref="Answers"/> in order; the last one stays.</summary>
    internal sealed class RestartingPower(FakeProbe probe) : IHyperVPowerInvoker
    {
        public List<VmAction> Calls { get; } = [];

        public bool[] Answers { get; set; } = [];

        public Task InvokeAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
        {
            Calls.Add(action);
            foreach (var answer in Answers)
            {
                probe.RdpAnswers.Enqueue(answer);
            }

            if (Answers.Length > 0)
            {
                probe.Rdp = Answers[^1];
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ListAuditLog(List<AuditEntry> entries) : IAuditLog
    {
        public void Write(AuditEntry entry) => entries.Add(entry);
    }

    internal sealed class FakeProbe : IRemoteAccessProbe
    {
        public bool Rdp { get; set; }

        public bool Ssh { get; set; }

        /// <summary>Answers used in order before <see cref="Rdp"/>.</summary>
        public Queue<bool> RdpAnswers { get; } = new();

        public Task<bool> RdpAnswersAsync(string address, CancellationToken cancellationToken) =>
            Task.FromResult(RdpAnswers.TryDequeue(out var answer) ? answer : Rdp);

        public Task<bool> SshAnswersAsync(string address, CancellationToken cancellationToken) => Task.FromResult(Ssh);
    }

    private sealed class FakeMedia : IVmMedia
    {
        public List<(Guid VmId, string Path)> Ejected { get; } = [];

        public Task EjectAsync(Guid vmId, string isoPath, CancellationToken cancellationToken)
        {
            Ejected.Add((vmId, isoPath));
            return Task.CompletedTask;
        }
    }
}
