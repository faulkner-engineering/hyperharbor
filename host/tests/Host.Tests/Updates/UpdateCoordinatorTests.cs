using System.Text;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Updates;

/// <summary>The service side of updates against a fake release server and a fake self-test.</summary>
public sealed class UpdateCoordinatorTests : IDisposable
{
    private static readonly SemanticVersion Current = SemanticVersion.Parse("1.1.0");

    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeReleaseServer _server = new();
    private readonly UpdateOptions _options = new() { MaintenanceTime = null };
    private readonly UpdateStateStore _store;
    private readonly HostActivity _activity;
    private readonly UpdateCoordinator _coordinator;
    private int _helperStarts;
    private Exception? _helperFailure;

    public UpdateCoordinatorTests()
    {
        File.WriteAllText(Path.Combine(_data, "users.json"), "users");
        _store = new UpdateStateStore(_data);
        _activity = new HostActivity(() => false, _time);
        var preparer = new UpdatePreparer(_options, Releases.Downloader(_server, _options), new UnsignedPackageVerifier(), new SelfTestGate(_data, new PassingSelfTest(), TimeSpan.FromSeconds(30)), _data);
        _coordinator = new UpdateCoordinator(preparer, _store, _activity, () => _options, Current, StartHelper, _time, NullLogger.Instance);
        _server.Serve(Releases.ManifestUrl, Encoding.UTF8.GetBytes(Releases.Manifest()));
        _server.Serve(Releases.PackageUrl, Releases.Package);
    }

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Fact]
    public async Task Auto_ChecksPreparesAndWaitsForTheIdleTime_ThenHandsOff()
    {
        await _coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(UpdateActivity.Ready, _coordinator.Status.Activity);
        Assert.Equal("1.2.0", _coordinator.Status.AvailableVersion);
        Assert.Equal(0, _helperStarts);

        _time.Advance(TimeSpan.FromMinutes(10));
        await _coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, _helperStarts);
        Assert.True(_activity.IsClosed);
        Assert.Equal(UpdateActivity.Installing, _coordinator.Status.Activity);
        var state = _store.Load();
        Assert.Equal(UpdatePhase.HandingOff, state.Phase);
        Assert.Equal(("1.1.0", "1.2.0"), (state.From, state.To));
        Assert.Equal(Releases.Package, File.ReadAllBytes(state.StagedExecutable!));
    }

    [Fact]
    public async Task WorkInProgress_HoldsTheInstall_AndTheGateStaysOpen()
    {
        await _coordinator.TickAsync(CancellationToken.None);
        using var request = _activity.TryBegin();
        _time.Advance(TimeSpan.FromHours(1));

        await _coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(0, _helperStarts);
        Assert.False(_activity.IsClosed);
        Assert.Equal(UpdateActivity.Ready, _coordinator.Status.Activity);
    }

    [Fact]
    public async Task Notify_PreparesButInstallsOnlyWhenAsked()
    {
        _options.Mode = UpdateMode.Notify;
        await _coordinator.TickAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await _coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, _helperStarts);

        Assert.True(_coordinator.RequestInstall());
        await _coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, _helperStarts);
    }

    [Fact]
    public void InstallNow_WithNothingReady_IsRefused()
    {
        Assert.False(_coordinator.RequestInstall());
    }

    [Fact]
    public async Task Off_DoesNotCheck_UnlessAsked()
    {
        _options.Mode = UpdateMode.Off;
        await _coordinator.TickAsync(CancellationToken.None);
        Assert.Empty(_server.Requested);

        _coordinator.RequestCheck();
        // The check shows as started before the tick that runs it, so Check now has something to show.
        Assert.Equal(UpdateActivity.Checking, _coordinator.Status.Activity);
        await _coordinator.TickAsync(CancellationToken.None);

        Assert.Equal([Releases.ManifestUrl], _server.Requested);
        Assert.Equal("1.2.0", _coordinator.Status.AvailableVersion);
        Assert.Equal(UpdateActivity.Idle, _coordinator.Status.Activity);
    }

    [Fact]
    public async Task UpToDate_DownloadsNothing()
    {
        _server.Serve(Releases.ManifestUrl, Encoding.UTF8.GetBytes(Releases.Manifest(version: "1.1.0")));

        await _coordinator.TickAsync(CancellationToken.None);

        Assert.Null(_coordinator.Status.AvailableVersion);
        Assert.Equal([Releases.ManifestUrl], _server.Requested);
    }

    [Fact]
    public async Task ARolledBackVersion_IsNotDownloadedAgain()
    {
        _store.Save(new UpdateState { RolledBack = ["1.2.0"], LastResult = "Version 1.2.0 did not start, so 1.1.0 was restored." });

        await _coordinator.TickAsync(CancellationToken.None);

        Assert.Equal([Releases.ManifestUrl], _server.Requested);
        Assert.Equal(["1.2.0"], _coordinator.Status.RolledBack);
        Assert.Equal("Version 1.2.0 did not start, so 1.1.0 was restored.", _coordinator.Status.LastResult);
    }

    [Fact]
    public async Task AFailedPreparation_IsNotRetriedUntilTheNextCheck()
    {
        _server.Serve(Releases.PackageUrl, Encoding.UTF8.GetBytes("tampered"));
        await _coordinator.TickAsync(CancellationToken.None);
        Assert.Contains("bytes", _coordinator.Status.Message, StringComparison.Ordinal);

        await _coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, _server.Requested.Count(url => url == Releases.PackageUrl));
    }

    [Fact]
    public async Task WhenTheHelperCannotStart_TheGateReopens_AndNothingIsHandedOff()
    {
        _helperFailure = new InvalidOperationException("schtasks failed");
        await _coordinator.TickAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10));

        await _coordinator.TickAsync(CancellationToken.None);

        Assert.False(_activity.IsClosed);
        Assert.Equal(UpdatePhase.Idle, _store.Load().Phase);
        Assert.Contains("schtasks failed", _coordinator.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHandoffTheHelperNeverPicksUp_IsTakenBack()
    {
        await _coordinator.TickAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10));
        await _coordinator.TickAsync(CancellationToken.None);
        Assert.True(_activity.IsClosed);

        _time.Advance(UpdateCoordinator.HandoffTimeout);
        await _coordinator.TickAsync(CancellationToken.None);

        Assert.False(_activity.IsClosed);
        Assert.Equal(UpdatePhase.Idle, _store.Load().Phase);
        Assert.Equal("The update helper did not start, so the update was not installed.", _store.Load().LastResult);
    }

    [Fact]
    public async Task WhileTheHelperOwnsTheState_TicksDoNothing()
    {
        _store.Save(new UpdateState { Phase = UpdatePhase.Starting, From = "1.0.0", To = "1.1.0" });

        await _coordinator.TickAsync(CancellationToken.None);

        Assert.Empty(_server.Requested);
    }

    private void StartHelper()
    {
        _helperStarts++;
        if (_helperFailure is not null)
        {
            throw _helperFailure;
        }
    }
}
