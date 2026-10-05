using HyperHarbor.Host.Core.Updates;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Updates;

public sealed class UpdateScheduleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

    [Fact]
    public void ACheckIsDue_TheFirstTime_AndOnceTheIntervalPassed()
    {
        Assert.True(UpdateSchedule.IsCheckDue(null, TimeSpan.FromHours(6), Noon));
        Assert.False(UpdateSchedule.IsCheckDue(Noon.AddHours(-5), TimeSpan.FromHours(6), Noon));
        Assert.True(UpdateSchedule.IsCheckDue(Noon.AddHours(-6), TimeSpan.FromHours(6), Noon));
    }

    [Theory]
    [InlineData("Auto", false, 10, true)]
    [InlineData("Auto", false, 9, false)]
    [InlineData("Auto", true, 60, false)]
    [InlineData("Notify", false, 60, false)]
    [InlineData("Off", false, 60, false)]
    public void Auto_InstallsAfterTheIdleTime_AndNeverWhileSomethingIsInProgress(string mode, bool inProgress, int idleMinutes, bool expected)
    {
        var may = UpdateSchedule.MayInstallAutomatically(Enum.Parse<UpdateMode>(mode), inProgress, Noon.AddMinutes(-idleMinutes), Noon, Idle, null, Window, TimeZoneInfo.Utc);

        Assert.Equal(expected, may);
    }

    [Theory]
    [InlineData("03:00", 3, 0, true)]
    [InlineData("03:00", 3, 59, true)]
    [InlineData("03:00", 4, 0, false)]
    [InlineData("03:00", 2, 59, false)]
    [InlineData("23:30", 0, 15, true)]
    [InlineData("23:30", 0, 31, false)]
    public void TheMaintenanceWindow_SkipsTheIdleWait_IncludingAcrossMidnight(string start, int hour, int minute, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 4, hour, minute, 0, TimeSpan.Zero);

        // Something ended a minute ago: too recent for the idle rule, so only the window can allow it.
        var may = UpdateSchedule.MayInstallAutomatically(UpdateMode.Auto, false, now.AddMinutes(-1), now, Idle, TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), Window, TimeZoneInfo.Utc);

        Assert.Equal(expected, may);
    }

    [Fact]
    public void TheMaintenanceWindow_UsesHostLocalTime()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");
        var threeLocal = new DateTimeOffset(2026, 10, 4, 8, 10, 0, TimeSpan.Zero);

        Assert.True(UpdateSchedule.MayInstallAutomatically(UpdateMode.Auto, false, threeLocal, threeLocal, Idle, new TimeOnly(3, 0), Window, zone));
    }
}

public sealed class HostActivityTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private bool _jobRunning;

    private HostActivity Activity() => new(() => _jobRunning, _time);

    [Fact]
    public void ARequestInProgress_KeepsTheGateOpen_AndMovesIdleSinceWhenItEnds()
    {
        var activity = Activity();
        var request = activity.TryBegin()!;

        Assert.True(activity.InProgress);
        Assert.False(activity.TryClose());

        _time.Advance(TimeSpan.FromMinutes(3));
        request.Dispose();
        request.Dispose();

        Assert.False(activity.InProgress);
        Assert.Equal(_time.GetUtcNow(), activity.IdleSince);
        Assert.True(activity.TryClose());
    }

    [Fact]
    public void AClosedGate_RefusesNewRequests_UntilReopened()
    {
        var activity = Activity();
        Assert.True(activity.TryClose());

        Assert.Null(activity.TryBegin());

        activity.Reopen();
        Assert.NotNull(activity.TryBegin());
    }

    [Fact]
    public void ARunningJob_BlocksClosing_AndObserveKeepsIdleSinceCurrent()
    {
        var activity = Activity();
        _jobRunning = true;
        _time.Advance(TimeSpan.FromMinutes(30));

        activity.Observe();

        Assert.False(activity.TryClose());
        Assert.Equal(_time.GetUtcNow(), activity.IdleSince);
    }
}
