using System.Security.Cryptography.X509Certificates;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Power;

/// <summary>Records what the controller asked of Windows.</summary>
internal sealed class FakePowerRequest : IPowerRequest
{
    public List<string?> Calls { get; } = [];

    public string? Reason { get; private set; }

    public bool Fail { get; set; }

    public void Set(string reason)
    {
        if (Fail)
        {
            throw new System.ComponentModel.Win32Exception(5);
        }

        Calls.Add(reason);
        Reason = reason;
    }

    public void Clear()
    {
        Calls.Add(null);
        Reason = null;
    }
}

internal sealed class FakeRemoteSessions : IRemoteSessions
{
    public bool Active { get; set; }

    public bool AnyActive() => Active;
}

public sealed class KeepAwakePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public void RecentUse_KeepsTheHostAwake_UntilTheWindowEnds()
    {
        Assert.Equal(KeepAwakeReason.ClientActivity, KeepAwakePolicy.Evaluate(Now, Now.AddMinutes(-9), Window, false, false));
        Assert.Null(KeepAwakePolicy.Evaluate(Now, Now - Window, Window, false, false));
        Assert.Null(KeepAwakePolicy.Evaluate(Now, null, Window, false, false));
    }

    [Fact]
    public void WorkInProgress_OrARemoteSession_KeepsTheHostAwakeOnItsOwn()
    {
        Assert.Equal(KeepAwakeReason.WorkInProgress, KeepAwakePolicy.Evaluate(Now, null, Window, true, true));
        Assert.Equal(KeepAwakeReason.RemoteDesktop, KeepAwakePolicy.Evaluate(Now, null, Window, false, true));
    }
}

public sealed class KeepAwakeControllerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 20, 0, 0, TimeSpan.Zero));
    private readonly FakePowerRequest _power = new();
    private readonly FakeRemoteSessions _sessions = new();
    private readonly KeepAwakeOptions _options = new();
    private readonly RemoteUseTracker _tracker;
    private readonly KeepAwakeController _controller;
    private bool _jobRunning;

    public KeepAwakeControllerTests()
    {
        _tracker = new RemoteUseTracker(_time);
        var activity = new HostActivity(() => _jobRunning, _time);
        _controller = new KeepAwakeController(_options, _tracker, activity, _sessions, _power, _time, NullLogger<KeepAwakeController>.Instance);
    }

    [Fact]
    public void UseHoldsTheRequest_OnceUntilTheWindowPasses_ThenReleasesIt()
    {
        _controller.Tick();
        Assert.Empty(_power.Calls);

        _tracker.MarkUsed();
        _controller.Tick();
        _time.Advance(TimeSpan.FromMinutes(5));
        _controller.Tick();
        Assert.Equal(["HyperHarbor: a paired device is using this host"], _power.Calls);

        _time.Advance(TimeSpan.FromMinutes(5));
        _controller.Tick();
        Assert.Equal(["HyperHarbor: a paired device is using this host", null], _power.Calls);
        Assert.Null(_controller.Current);
    }

    [Fact]
    public void ARunningJob_KeepsTheHostAwake_WithoutRequests()
    {
        _jobRunning = true;
        _controller.Tick();

        Assert.Equal(KeepAwakeReason.WorkInProgress, _controller.Current);
        Assert.NotNull(_power.Reason);
    }

    [Fact]
    public void AFailedRequest_IsTriedAgainAtTheNextTick()
    {
        _sessions.Active = true;
        _power.Fail = true;
        _controller.Tick();
        Assert.Null(_power.Reason);

        _power.Fail = false;
        _controller.Tick();

        Assert.Equal("HyperHarbor: someone is signed in to this host over Remote Desktop", _power.Reason);
    }

    [Fact]
    public void Disabled_NeverHoldsTheRequest()
    {
        _options.Enabled = false;
        _tracker.MarkUsed();
        _controller.Tick();

        Assert.Empty(_power.Calls);
    }

    [Fact]
    public void Release_ClearsAHeldRequest()
    {
        _tracker.MarkUsed();
        _controller.Tick();

        _controller.Release();

        Assert.Null(_power.Reason);
        Assert.Null(_controller.Current);
    }

    [Fact]
    public void UseAfterAQuietSpell_RaisesResumed_ButTheClientPollDoesNot()
    {
        var resumed = 0;
        _tracker.Resumed += () => resumed++;

        _tracker.MarkUsed();
        _time.Advance(TimeSpan.FromSeconds(5));
        _tracker.MarkUsed();
        _time.Advance(TimeSpan.FromMinutes(2));
        _tracker.MarkUsed();

        Assert.Equal(2, resumed);
    }
}

public sealed class RemoteUseApiTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();

    public void Dispose()
    {
        _certificate.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task ARequestFromAPairedDevice_CountsAsUse_AndAnUnpairedOneDoesNot()
    {
        var tracker = _host.Services.GetRequiredService<RemoteUseTracker>();
        using var stranger = _host.CreateClient(_certificate);
        await stranger.GetAsync("/api/v1/vms");
        Assert.Null(tracker.LastUsed);

        _host.Pair(_certificate);
        using var paired = _host.CreateClient(_certificate);
        var response = await paired.GetAsync("/api/v1/vms");

        response.EnsureSuccessStatusCode();
        Assert.NotNull(tracker.LastUsed);
    }
}

public sealed class WindowsPowerTests
{
    [LocalHardwareFact]
    public void ThePowerRequest_CanBeSetChangedAndCleared()
    {
        using var request = new WindowsPowerRequest();

        request.Set("HyperHarbor test: keeping awake");
        request.Set("HyperHarbor test: another reason");
        request.Clear();
        request.Clear();
    }

    [LocalHardwareFact]
    public void RemoteSessions_CanBeRead()
    {
        _ = new WindowsRemoteSessions().AnyActive();
    }
}
