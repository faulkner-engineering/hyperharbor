using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.HostProfiles;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.HostProfiles;

/// <summary>The Lean host action end to end against an in-memory PC: dry run first, backups, idempotence, undo, schedule.</summary>
public sealed class HostLeanServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hh-lean-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHostSystem _host = FakeHostSystem.GamingPc();
    private readonly FakeSteamLibrary _steam = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly List<AuditEntry> _audit = [];
    private readonly HostLeanStore _store;
    private readonly FakeHostMetrics _metrics;
    private readonly HostLeanService _service;

    public HostLeanServiceTests()
    {
        _store = new HostLeanStore(_directory);
        _metrics = new FakeHostMetrics(_time);
        _service = new HostLeanService(
            _host,
            _steam,
            _store,
            new SetupProfilePlanner(Catalogs.Default),
            new ProfileValidator(Catalogs.Default, HostCatalogs.Default),
            new HostDiffer(Catalogs.Default, HostCatalogs.Default),
            _metrics,
            _time,
            NullLogger<HostLeanService>.Instance,
            new ListAudit(_audit),
            TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<HostLeanRunSummary> ReviewAndApplyAsync(string source = HostLeanSources.Lean)
    {
        await _service.DryRunAsync(source, Ct);
        return await _service.ApplyAsync(source, Ct);
    }

    // The dry run.

    [Fact]
    public async Task ADryRun_ListsTheChanges_AndChangesNothing()
    {
        var services = _host.Services.ToList();

        var dry = await _service.DryRunAsync(HostLeanSources.Lean, Ct);

        Assert.True(dry.CanApply);
        Assert.Contains(dry.Changes, line => line.Handler == "services" && line.Item.Contains("DiagTrack", StringComparison.Ordinal));
        Assert.Contains(dry.Changes, line => line.Handler == "startup" && line.Item == "OneDrive");
        Assert.Contains(dry.Changes, line => line.Handler == "registry");
        Assert.Contains(dry.Changes, line => line.Handler == "power" && line.Item == "Power plan");
        Assert.Contains(dry.Changes, line => line.Handler == "appx");
        Assert.Contains(dry.Changes, line => line.Handler == "programs" && line.Item == "Microsoft OneDrive");
        Assert.Equal(0, _host.ApplyCalls);
        Assert.Equal(services, _host.Services);
        Assert.DoesNotContain("restorePoint", _host.Log);
        Assert.NotNull(_service.Status().DryRun);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task AnApply_WithoutADryRun_IsRefused_AndChangesNothing()
    {
        var run = await _service.ApplyAsync(HostLeanSources.Lean, Ct);

        Assert.Equal(0, run.Changed);
        Assert.Contains("dry run", Assert.Single(run.Problems), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _host.ApplyCalls);
        Assert.DoesNotContain("restorePoint", _host.Log);
        Assert.Null(_store.Load().Baseline);
    }

    [Fact]
    public async Task AnApply_AfterAnExpiredDryRun_IsRefused()
    {
        await _service.DryRunAsync(HostLeanSources.Lean, Ct);
        _time.Advance(HostLeanService.DryRunValidity + TimeSpan.FromMinutes(1));

        var run = await _service.ApplyAsync(HostLeanSources.Lean, Ct);

        Assert.Contains("dry run", Assert.Single(run.Problems), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _host.ApplyCalls);
        Assert.Null(_service.Status().DryRun);
    }

    [Fact]
    public async Task WhenThePcChangesAfterTheDryRun_TheApplyNeedsANewDryRun()
    {
        await _service.DryRunAsync(HostLeanSources.Lean, Ct);
        _host.Startup.Add(new StartupEntry("machine", "Run", "Zoom launcher", "zoom.exe", true));

        var run = await _service.ApplyAsync(HostLeanSources.Lean, Ct);

        Assert.Contains("changed since the dry run", Assert.Single(run.Problems), StringComparison.Ordinal);
        Assert.Equal(0, _host.ApplyCalls);
        Assert.Contains(_service.Status().DryRun!.Changes, line => line.Item == "Zoom launcher");

        var again = await _service.ApplyAsync(HostLeanSources.Lean, Ct);

        Assert.Empty(again.Problems);
        Assert.Equal(1, _host.ApplyCalls);
    }

    [Fact]
    public async Task AnApply_OnAnUnelevatedHost_IsRefused_ButADryRunStillWorks()
    {
        _host.CanModify = false;

        var dry = await _service.DryRunAsync(HostLeanSources.Lean, Ct);
        var run = await _service.ApplyAsync(HostLeanSources.Lean, Ct);

        Assert.False(dry.CanApply);
        Assert.False(_service.Status().Supported);
        Assert.Contains("administrator", Assert.Single(run.Problems), StringComparison.Ordinal);
        Assert.Equal(0, _host.ApplyCalls);
    }

    // The first apply.

    [Fact]
    public async Task TheFirstApply_MakesARestorePoint_AndExportsTheRegistry_BeforeChangingAnything()
    {
        var run = await ReviewAndApplyAsync();

        Assert.Empty(run.Problems);
        var order = _host.Log.Where(entry => entry is "restorePoint" or "export" or "apply").ToList();
        Assert.Equal(["restorePoint", "export", "apply"], order);
        Assert.Contains("restore point", run.RestorePoint, StringComparison.Ordinal);
        Assert.Contains(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", _host.ExportedKeys);
        Assert.Contains($@"HKU\{FakeHostSystem.UserSid}\Software\Microsoft\Windows\CurrentVersion\GameDVR", _host.ExportedKeys);
        Assert.Contains(@"HKLM\SYSTEM\CurrentControlSet\Services\DiagTrack", _host.ExportedKeys);
        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved", _host.ExportedKeys);
        Assert.NotNull(_store.Load().Baseline);
        Assert.True(_service.Status().Applied);
    }

    [Fact]
    public async Task WithoutARestorePoint_NothingIsChanged()
    {
        _host.RestorePointFailure = new HostLeanException("Turn on System Protection.");
        await _service.DryRunAsync(HostLeanSources.Lean, Ct);

        var run = await _service.ApplyAsync(HostLeanSources.Lean, Ct);

        Assert.Equal(0, run.Changed);
        var problem = Assert.Single(run.Problems);
        Assert.Contains("Nothing was changed", problem, StringComparison.Ordinal);
        Assert.Contains("System Protection", problem, StringComparison.Ordinal);
        Assert.Equal(0, _host.ApplyCalls);
        Assert.Null(_store.Load().Baseline);
    }

    [Fact]
    public async Task LaterApplies_DoNotMakeAnotherRestorePoint()
    {
        await ReviewAndApplyAsync();
        _host.Startup.Add(new StartupEntry("machine", "Run", "Zoom launcher", "zoom.exe", true));

        var second = await ReviewAndApplyAsync();

        Assert.Empty(second.Problems);
        Assert.Equal(1, _host.Log.Count(entry => entry == "restorePoint"));
        Assert.Null(second.RestorePoint);
    }

    // Idempotence.

    [Fact]
    public async Task ApplyingTwice_ChangesNothingTheSecondTime()
    {
        var first = await ReviewAndApplyAsync();
        Assert.Empty(first.Problems);
        Assert.True(first.Changed > 10);

        var again = await _service.DryRunAsync(HostLeanSources.Lean, Ct);

        Assert.Empty(again.Changes);
        Assert.False(again.CanApply);
        Assert.True(again.AlreadyInPlace > 10);

        var second = await _service.ApplyAsync(HostLeanSources.Lean, Ct);

        Assert.Equal(0, second.Changed);
        Assert.Empty(second.Problems);
        Assert.Equal(1, _host.ApplyCalls);
    }

    [Fact]
    public async Task TheAppliedState_IsWhatTheProfileAsks()
    {
        await ReviewAndApplyAsync();

        Assert.Equal("disabled", _host.Services.Single(service => service.Name == "DiagTrack").Startup);
        Assert.False(_host.Services.Single(service => service.Name == "DiagTrack").Running);
        Assert.Equal("automatic", _host.Services.Single(service => service.Name == "vmms").Startup);
        Assert.All(_host.Startup.Where(entry => entry.Name is "OneDrive" or "iCUE"), entry => Assert.False(entry.Enabled));
        Assert.All(_host.Startup.Where(entry => entry.Name is "SecurityHealth" or "Steam"), entry => Assert.True(entry.Enabled));
        Assert.Equal(HostDiffer.KnownPlans["highPerformance"].Guid, _host.ActivePlan);
        Assert.Equal(["Intel(R) Ethernet Controller I225-V", "HID Keyboard Device"], _host.Armed); // the keyboard may still wake the PC
        Assert.DoesNotContain("Microsoft.BingNews", _host.Appx);
        Assert.Contains("Microsoft.Windows.Photos", _host.Appx); // kept: no other image viewer is installed
        Assert.Contains("Microsoft.WindowsStore", _host.Appx);
        Assert.Equal(["Git"], _host.Programs.Select(program => program.Name));
        Assert.Equal(("dword", "0"), _host.Registry["HKLM|SOFTWARE\\Policies\\Microsoft\\Dsh|AllowNewsAndInterests"]);
        Assert.Equal(("dword", "0"), _host.Registry[$"{FakeHostSystem.UserSid}|Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR|AppCaptureEnabled"]);
        Assert.Equal(("dword", "0"), _host.Registry["Default|Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR|AppCaptureEnabled"]);
        Assert.Equal(("dword", "0"), _host.Registry["HKLM|SOFTWARE\\Policies\\Microsoft\\Edge|StartupBoostEnabled"]);
    }

    [Fact]
    public async Task AGuardedGame_KeepsTheXboxPieces_ThroughTheWholeApply()
    {
        _steam.Games.Add(new SteamGame(976730, "Halo: The Master Chief Collection"));

        var run = await ReviewAndApplyAsync();

        Assert.Empty(run.Problems);
        Assert.Contains("Microsoft.GamingServices", _host.Appx);
        Assert.Contains("Microsoft.XboxIdentityProvider", _host.Appx);
        Assert.Equal("manual", _host.Services.Single(service => service.Name == "GamingServices").Startup);
    }

    [Fact]
    public async Task AFailingItem_IsReported_AndTheOthersStillApply()
    {
        _host.FailingItems.Add("Service DiagTrack");

        var run = await ReviewAndApplyAsync();

        Assert.Contains(run.Problems, problem => problem.StartsWith("Service DiagTrack: refused", StringComparison.Ordinal));
        Assert.Equal("disabled", _host.Services.Single(service => service.Name == "MapsBroker").Startup);
        Assert.Equal("automatic", _host.Services.Single(service => service.Name == "DiagTrack").Startup);
    }

    // Metrics and audit.

    [Fact]
    public async Task IdleMemoryAndProcessCount_AreRecordedBeforeAndAfter()
    {
        var run = await ReviewAndApplyAsync();

        Assert.Equal(9000, run.Before!.UsedMemoryMb);
        Assert.Equal(8400, run.After!.UsedMemoryMb);
        Assert.Equal(260, run.Before.ProcessCount);
        Assert.Equal(235, run.After.ProcessCount);
        Assert.Equal(run, _service.Status().LastRun);
        Assert.Equal(9000, _store.Load().Baseline!.Idle!.UsedMemoryMb);
    }

    [Fact]
    public async Task AnApply_IsAudited_BeforeAndAfter()
    {
        await ReviewAndApplyAsync();

        Assert.Equal(["hostLeanApply", "hostLeanApply"], _audit.Select(entry => entry.Action));
        Assert.Equal([AuditOutcome.Requested, AuditOutcome.Succeeded], _audit.Select(entry => entry.Outcome));
    }

    [Fact]
    public async Task WhenTheAuditLogCannotBeWritten_NothingIsChanged()
    {
        var service = new HostLeanService(
            _host, _steam, _store, new SetupProfilePlanner(Catalogs.Default), new ProfileValidator(Catalogs.Default, HostCatalogs.Default),
            new HostDiffer(Catalogs.Default, HostCatalogs.Default), _metrics, _time, NullLogger<HostLeanService>.Instance, new BrokenAudit(), TimeSpan.Zero);
        await service.DryRunAsync(HostLeanSources.Lean, Ct);

        var run = await service.ApplyAsync(HostLeanSources.Lean, Ct);

        Assert.Contains("audit log", Assert.Single(run.Problems), StringComparison.Ordinal);
        Assert.Equal(0, _host.ApplyCalls);
    }

    // The undo profile.

    [Fact]
    public async Task TheUndoProfile_PutsBackWhatTheApplyChanged()
    {
        await ReviewAndApplyAsync();
        Assert.True(_service.Status().UndoAvailable);
        var yaml = _store.ReadUndo()!;
        Assert.Contains("target: host", yaml, StringComparison.Ordinal);
        Assert.Contains("type: absent", yaml, StringComparison.Ordinal);

        var undone = await ReviewAndApplyAsync(HostLeanSources.Undo);

        Assert.Empty(undone.Problems);
        Assert.Equal("automatic", _host.Services.Single(service => service.Name == "DiagTrack").Startup);
        Assert.Equal("automaticDelayed", _host.Services.Single(service => service.Name == "MapsBroker").Startup);
        Assert.All(_host.Startup, entry => Assert.Equal(entry.Name != "Spotify", entry.Enabled));
        Assert.Equal(HostDiffer.KnownPlans["balanced"].Guid, _host.ActivePlan);
        Assert.Contains("HID Keyboard Device", _host.Armed);
        Assert.Equal(("dword", "1"), _host.Registry["HKLM|SOFTWARE\\Policies\\Microsoft\\Dsh|AllowNewsAndInterests"]);
        Assert.Equal(("dword", "1"), _host.Registry[$"{FakeHostSystem.UserSid}|Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR|AppCaptureEnabled"]);
        Assert.DoesNotContain("HKLM|SOFTWARE\\Policies\\Microsoft\\Edge|StartupBoostEnabled", _host.Registry.Keys);
        Assert.DoesNotContain(_host.Registry.Keys, key => key.Contains("GameDVR_Enabled", StringComparison.Ordinal));

        // Removed apps and uninstalled programs cannot be put back by a profile.
        Assert.DoesNotContain("Microsoft.BingNews", _host.Appx);
        Assert.Equal(["Git"], _host.Programs.Select(program => program.Name));

        Assert.False(_service.Status().UndoAvailable);
        Assert.Null(_store.ReadUndo());
    }

    [Fact]
    public async Task TheUndoProfile_KeepsTheEarliestValues_AcrossApplies()
    {
        await ReviewAndApplyAsync();
        // Something sets a value and a later apply changes it again; the undo must still return to the original.
        _host.Registry["HKLM|SOFTWARE\\Policies\\Microsoft\\Dsh|AllowNewsAndInterests"] = ("dword", "5");
        _host.Startup.Add(new StartupEntry("machine", "Run", "Zoom launcher", "zoom.exe", true));
        await ReviewAndApplyAsync();

        await ReviewAndApplyAsync(HostLeanSources.Undo);

        Assert.Equal(("dword", "1"), _host.Registry["HKLM|SOFTWARE\\Policies\\Microsoft\\Dsh|AllowNewsAndInterests"]);
        Assert.True(_host.Startup.Single(entry => entry.Name == "Zoom launcher").Enabled);
    }

    [Fact]
    public async Task WithNothingToUndo_TheDryRunSaysSo()
    {
        var error = await Assert.ThrowsAsync<HostLeanUnsupportedException>(() => _service.DryRunAsync(HostLeanSources.Undo, Ct));

        Assert.Contains("nothing to undo", error.Message, StringComparison.Ordinal);
    }

    // The monthly re-apply.

    [Fact]
    public async Task TheMonthlyReapply_RunsWithoutADryRun_AfterAPersonApprovedTheProfile()
    {
        await ReviewAndApplyAsync();
        Assert.True(_store.Load().ScheduleEnabled);
        Assert.Equal(_time.GetUtcNow() + HostLeanService.ReapplyInterval, _service.Status().NextScheduled);

        _time.Advance(TimeSpan.FromDays(10));
        _host.Startup[_host.Startup.FindIndex(entry => entry.Name == "iCUE")] = _host.Startup.Single(entry => entry.Name == "iCUE") with { Enabled = true };
        await _service.TickAsync(Ct);
        Assert.Equal(1, _host.ApplyCalls);

        _time.Advance(TimeSpan.FromDays(21));
        await _service.TickAsync(Ct);

        Assert.Equal(2, _host.ApplyCalls);
        var run = _service.Status().LastRun!;
        Assert.True(run.Scheduled);
        Assert.Empty(run.Problems);
        Assert.Equal(1, run.Changed);
        Assert.False(_host.Startup.Single(entry => entry.Name == "iCUE").Enabled);
        Assert.Equal(1, _host.Log.Count(entry => entry == "restorePoint"));
        Assert.Contains(_audit, entry => entry.Action == "hostLeanScheduled");

        await _service.TickAsync(Ct);
        Assert.Equal(2, _host.ApplyCalls);
    }

    [Fact]
    public async Task TheMonthlyReapply_DoesNothing_WhenItIsOff_OrNothingWasApplied()
    {
        await _service.TickAsync(Ct);
        Assert.Empty(_host.Log);

        await ReviewAndApplyAsync();
        _service.SetSchedule(false);
        _time.Advance(TimeSpan.FromDays(40));
        _host.Startup[_host.Startup.FindIndex(entry => entry.Name == "iCUE")] = _host.Startup.Single(entry => entry.Name == "iCUE") with { Enabled = true };

        await _service.TickAsync(Ct);

        Assert.Equal(1, _host.ApplyCalls);
        Assert.Null(_service.Status().NextScheduled);
    }

    [Fact]
    public async Task TheMonthlyReapply_SkipsAProfileNobodyReviewed()
    {
        await ReviewAndApplyAsync();
        _store.Update(state => state with { ApprovedProfileHash = "an older version of the profile" });
        _time.Advance(TimeSpan.FromDays(31));
        _host.Startup[_host.Startup.FindIndex(entry => entry.Name == "iCUE")] = _host.Startup.Single(entry => entry.Name == "iCUE") with { Enabled = true };

        await _service.TickAsync(Ct);

        Assert.Equal(1, _host.ApplyCalls);
        Assert.Contains("not been reviewed", Assert.Single(_service.Status().LastRun!.Problems), StringComparison.Ordinal);
    }

    private sealed class ListAudit(List<AuditEntry> entries) : IAuditLog
    {
        public void Write(AuditEntry entry) => entries.Add(entry);
    }

    private sealed class BrokenAudit : IAuditLog
    {
        public void Write(AuditEntry entry) => throw new AuditUnavailableException(new IOException("disk full"));
    }
}
