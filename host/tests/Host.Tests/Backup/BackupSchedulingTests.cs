using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class BackupSchedulingTests
{
    private static readonly Guid VmId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static ConditionInputs Inputs(bool? session = false, bool game = false, int hour = 12) =>
        new(session, game, new TimeOnly(hour, 0));

    [Fact]
    public void Conditions_allow_a_quiet_host()
    {
        Assert.True(RunConditionEvaluator.Evaluate(RunConditions.Default, Inputs()).Allowed);
    }

    [Fact]
    public void An_active_session_blocks_when_required_and_is_ignored_when_not()
    {
        Assert.False(RunConditionEvaluator.Evaluate(RunConditions.Default, Inputs(session: true)).Allowed);
        Assert.True(RunConditionEvaluator.Evaluate(new RunConditions(NoActiveSession: false), Inputs(session: true)).Allowed);
    }

    [Fact]
    public void Host_state_has_no_session_to_wait_for()
    {
        Assert.True(RunConditionEvaluator.Evaluate(RunConditions.Default, Inputs(session: null)).Allowed);
    }

    [Fact]
    public void A_game_blocks_when_required_and_is_ignored_when_not()
    {
        var blocked = RunConditionEvaluator.Evaluate(RunConditions.Default, Inputs(game: true));

        Assert.False(blocked.Allowed);
        Assert.Contains("game", blocked.Reason);
        Assert.True(RunConditionEvaluator.Evaluate(new RunConditions(NotInGame: false), Inputs(game: true)).Allowed);
    }

    [Theory]
    [InlineData(1, 5, 1, true)]
    [InlineData(1, 5, 4, true)]
    [InlineData(1, 5, 5, false)]
    [InlineData(1, 5, 0, false)]
    [InlineData(22, 6, 23, true)]
    [InlineData(22, 6, 3, true)]
    [InlineData(22, 6, 6, false)]
    [InlineData(22, 6, 12, false)]
    [InlineData(3, 3, 14, true)]
    public void The_time_window_is_start_inclusive_end_exclusive_and_may_cross_midnight(int start, int end, int hour, bool allowed)
    {
        var conditions = new RunConditions(Window: new TimeWindow(new TimeOnly(start, 0), new TimeOnly(end, 0)));

        Assert.Equal(allowed, RunConditionEvaluator.Evaluate(conditions, Inputs(hour: hour)).Allowed);
    }

    [Fact]
    public void A_slot_is_not_due_before_its_time_and_is_due_after_when_never_run()
    {
        var schedule = ScheduleSpec.Nightly;

        Assert.False(ScheduleCalculator.IsDue(schedule, new DateTimeOffset(2026, 10, 12, 1, 59, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 12, 2, 5, 0, TimeSpan.Zero)));
        Assert.True(ScheduleCalculator.IsDue(schedule, new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero), null));
    }

    [Fact]
    public void A_slot_that_was_already_served_is_not_due_again()
    {
        var now = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

        Assert.False(ScheduleCalculator.IsDue(ScheduleSpec.Nightly, now, new DateTimeOffset(2026, 10, 12, 2, 10, 0, TimeSpan.Zero)));
        Assert.True(ScheduleCalculator.IsDue(ScheduleSpec.Nightly, now, new DateTimeOffset(2026, 10, 11, 2, 10, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Only_the_listed_days_have_slots_and_a_missed_slot_runs_at_the_next_chance()
    {
        var mondays = new ScheduleSpec(new TimeOnly(2, 0), [DayOfWeek.Monday]);
        var tuesday = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(DayOfWeek.Tuesday, tuesday.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero), ScheduleCalculator.LatestSlot(mondays, tuesday));
        Assert.True(ScheduleCalculator.IsDue(mondays, tuesday, new DateTimeOffset(2026, 10, 5, 2, 10, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_shutdown_runs_once_after_the_quiet_period()
    {
        var debouncer = new ShutdownDebouncer();
        var period = TimeSpan.FromMinutes(10);
        debouncer.Observe(VmId, VmState.Running, Noon, period);

        debouncer.Observe(VmId, VmState.Off, Noon.AddMinutes(1), period);

        Assert.Empty(debouncer.TakeDue(Noon.AddMinutes(10)));
        Assert.Equal([VmId], debouncer.TakeDue(Noon.AddMinutes(11)));
        Assert.Empty(debouncer.TakeDue(Noon.AddMinutes(60)));
    }

    [Fact]
    public void A_start_within_the_period_cancels_the_run()
    {
        var debouncer = new ShutdownDebouncer();
        var period = TimeSpan.FromMinutes(10);
        debouncer.Observe(VmId, VmState.Running, Noon, period);
        debouncer.Observe(VmId, VmState.Off, Noon.AddMinutes(1), period);

        debouncer.Observe(VmId, VmState.Starting, Noon.AddMinutes(5), period);
        debouncer.Observe(VmId, VmState.Running, Noon.AddMinutes(6), period);

        Assert.Empty(debouncer.TakeDue(Noon.AddMinutes(30)));
    }

    [Fact]
    public void A_restart_runs_at_most_once_and_after_the_last_shutdown()
    {
        var debouncer = new ShutdownDebouncer();
        var period = TimeSpan.FromMinutes(10);
        debouncer.Observe(VmId, VmState.Running, Noon, period);
        debouncer.Observe(VmId, VmState.Off, Noon.AddMinutes(1), period);
        debouncer.Observe(VmId, VmState.Running, Noon.AddMinutes(4), period);
        debouncer.Observe(VmId, VmState.Off, Noon.AddMinutes(8), period);

        Assert.Empty(debouncer.TakeDue(Noon.AddMinutes(12)));
        Assert.Equal([VmId], debouncer.TakeDue(Noon.AddMinutes(18)));
        Assert.Empty(debouncer.TakeDue(Noon.AddMinutes(40)));
    }

    [Fact]
    public void Staying_off_does_not_reset_the_period_and_a_VM_found_off_is_not_a_shutdown()
    {
        var debouncer = new ShutdownDebouncer();
        var period = TimeSpan.FromMinutes(10);
        debouncer.Observe(VmId, VmState.Off, Noon, period);
        Assert.Equal(0, debouncer.PendingCount);

        debouncer.Observe(VmId, VmState.Running, Noon.AddMinutes(1), period);
        debouncer.Observe(VmId, VmState.Off, Noon.AddMinutes(2), period);
        debouncer.Observe(VmId, VmState.Off, Noon.AddMinutes(9), period);

        Assert.Equal([VmId], debouncer.TakeDue(Noon.AddMinutes(12)));
    }

    [Fact]
    public void Without_the_trigger_nothing_is_scheduled()
    {
        var debouncer = new ShutdownDebouncer();
        debouncer.Observe(VmId, VmState.Running, Noon, null);
        debouncer.Observe(VmId, VmState.Off, Noon.AddMinutes(1), null);

        Assert.Equal(0, debouncer.PendingCount);
    }
}
