using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Unattend;

public sealed class WindowsSetupBootRecoveryTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-25T16:38:00Z"));
    private readonly FakeBootEvents _events = new();
    private readonly FakeVmInventory _inventory = new();
    private readonly FakePowerInvoker _power = new();
    private readonly FakeVmKeyboard _keyboard = new();
    private readonly WindowsSetupBootRecovery _recovery;

    public WindowsSetupBootRecoveryTests()
    {
        _recovery = new WindowsSetupBootRecovery(_events, _power, _keyboard, _time, NullLogger<WindowsSetupBootRecovery>.Instance, TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task NothingToBoot_RestartsTheVm_AndPressesTheBootKeyAgain()
    {
        _events.Outcome = VmBootOutcome.NothingToBoot;

        var result = await _recovery.RecoverAsync(Install(), CancellationToken.None);

        Assert.Equal(BootRecoveryResult.Restarted, result);
        Assert.Equal([(VmId, VmAction.TurnOff), (VmId, VmAction.Start)], _power.Calls);
        Assert.Equal(WindowsBootKey.Presses, _keyboard.Presses.Count);
        Assert.All(_keyboard.Presses, press => Assert.Equal((VmId, WindowsBootKey.SpaceKey), press));
        Assert.Equal(Install().StartedAt, _events.AskedSince);
    }

    [Fact]
    public async Task AStartRefusedWhileTheVmIsStillTurningOff_IsRetried()
    {
        _events.Outcome = VmBootOutcome.NothingToBoot;
        var power = new StillStoppingPower(refusals: 2);
        var recovery = new WindowsSetupBootRecovery(_events, power, _keyboard, _time, NullLogger<WindowsSetupBootRecovery>.Instance, TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(BootRecoveryResult.Restarted, await recovery.RecoverAsync(Install(), CancellationToken.None));

        Assert.Equal([VmAction.TurnOff, VmAction.Start, VmAction.Start, VmAction.Start], power.Calls);
        Assert.Equal(WindowsBootKey.Presses, _keyboard.Presses.Count);
    }

    /// <summary>Refuses Start a number of times, as Hyper-V does while a turned-off VM is still stopping.</summary>
    private sealed class StillStoppingPower(int refusals) : HyperHarbor.Host.Core.Power.IHyperVPowerInvoker
    {
        private int _refusals = refusals;

        public List<VmAction> Calls { get; } = [];

        public Task InvokeAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
        {
            Calls.Add(action);
            if (action == VmAction.Start && _refusals-- > 0)
            {
                throw new HyperHarbor.Host.Core.Power.VmActionNotAllowedException(action, null, "Hyper-V reported that Start is not valid in the current state.");
            }

            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(VmBootOutcome.Booted)]
    [InlineData(VmBootOutcome.Unknown)]
    public async Task ABootThatStarted_OrNoLog_IsLeftAlone(VmBootOutcome outcome)
    {
        _events.Outcome = outcome;

        Assert.Equal(BootRecoveryResult.None, await _recovery.RecoverAsync(Install(), CancellationToken.None));
        Assert.Empty(_power.Calls);
        Assert.Empty(_keyboard.Presses);
    }

    [Fact]
    public async Task LinuxInstalls_AreLeftAlone()
    {
        _events.Outcome = VmBootOutcome.NothingToBoot;

        Assert.Equal(BootRecoveryResult.None, await _recovery.RecoverAsync(Install(InstallOs.Linux), CancellationToken.None));
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task GivesUp_AfterThreeRestarts()
    {
        _events.Outcome = VmBootOutcome.NothingToBoot;

        for (var i = 0; i < WindowsSetupBootRecovery.MaxRestarts; i++)
        {
            Assert.Equal(BootRecoveryResult.Restarted, await _recovery.RecoverAsync(Install(), CancellationToken.None));
        }

        Assert.Equal(BootRecoveryResult.GaveUp, await _recovery.RecoverAsync(Install(), CancellationToken.None));
        Assert.Equal(WindowsSetupBootRecovery.MaxRestarts * 2, _power.Calls.Count);
    }

    [Fact]
    public async Task Watcher_RestartsAStuckWindowsSetup_ThenFailsTheInstallWhenItKeepsFindingNothing()
    {
        var users = new UserStore(_directory);
        var userId = users.GetOrCreateDefault().UserId;
        var installs = new UnattendedInstallStore(_directory);
        var credentials = new VmCredentialStore(_directory);
        var accounts = new ProvisioningStore(_directory);
        var guest = new FakeGuestAccountManager();
        var watcher = new UnattendedInstallWatcher(
            installs, _inventory, new UnattendedInstallWatcherTests.FakeProbe(), new ProvisioningService(_inventory, users, guest, credentials, accounts, _time, NullLogger<ProvisioningService>.Instance),
            accounts, credentials, guest, new UnattendedInstallWatcherTests.FakeMedia(), _time, new InstallWatcherOptions(), NullLogger<UnattendedInstallWatcher>.Instance,
            bootRecovery: _recovery);
        installs.Save(Install() with { UserId = userId });
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Running, GuestOsFamily.Unknown));
        _events.Outcome = VmBootOutcome.NothingToBoot;

        await watcher.TickAsync(CancellationToken.None);

        var install = installs.Find(VmId)!;
        Assert.Equal(UnattendedInstallState.Installing, install.State);
        Assert.Contains("Starting Windows Setup again", install.Step, StringComparison.Ordinal);
        Assert.Equal(2, _power.Calls.Count);

        for (var i = 0; i < WindowsSetupBootRecovery.MaxRestarts; i++)
        {
            await watcher.TickAsync(CancellationToken.None);
        }

        Assert.Equal(UnattendedInstallState.Failed, installs.Find(VmId)!.State);
        Assert.Contains("found nothing to boot", installs.Find(VmId)!.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the real Hyper-V log. HH_LIVE_GUEST_VM must have booted in the last day; with HH_BOOT_FAILED_AT (the time
    /// of an 18603 event for that VM), the minute before it must read as nothing to boot.
    /// </summary>
    [EnvironmentFact("HH_BOOT_EVENTS_LIVE", "HH_LIVE_GUEST_VM")]
    public void Live_ReadsTheVmsBootsFromTheHyperVLog()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var since = DateTimeOffset.UtcNow.AddDays(-1);

        Assert.Equal(VmBootOutcome.Booted, new HyperVWorkerBootEvents().LastBoot(vmId, since));
        Assert.Equal(VmBootOutcome.Unknown, new HyperVWorkerBootEvents().LastBoot(Guid.NewGuid(), since));
        if (Environment.GetEnvironmentVariable("HH_BOOT_FAILED_AT") is { Length: > 0 } failed)
        {
            var at = DateTimeOffset.Parse(failed, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(VmBootOutcome.NothingToBoot, HyperVWorkerBootEvents.LastBoot(vmId, at.AddMinutes(-1), at.AddSeconds(1)));
        }
    }

    private UnattendedInstall Install(InstallOs os = InstallOs.Windows) =>
        new(VmId, Guid.Empty, "windows-workstation", os, false, Path.Combine(_directory, "seed.iso"), UnattendedInstallState.Installing,
            "Installing Windows", DateTimeOffset.Parse("2026-10-25T16:38:45Z"), DateTimeOffset.Parse("2026-10-25T16:38:45Z"));

    private sealed class FakeBootEvents : IVmBootEvents
    {
        public VmBootOutcome Outcome { get; set; }

        public DateTimeOffset? AskedSince { get; private set; }

        public VmBootOutcome LastBoot(Guid vmId, DateTimeOffset since)
        {
            AskedSince = since;
            return Outcome;
        }
    }
}
