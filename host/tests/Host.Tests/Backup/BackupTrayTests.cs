using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Tray.Ambient;
using HyperHarbor.Host.Tray.Backup;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class BackupNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static TrayBackupItem Item(string outcome = "succeeded", bool overdue = false, Guid? run = null, string name = "Dev") =>
        new(name, "Local", outcome, Now, outcome == "failed" ? "The disk is full." : null, Now, overdue, run ?? Guid.NewGuid());

    private static TrayBackupStatus Status(params TrayBackupItem[] items) => new(true, 0, false, 0, items);

    [Fact]
    public void TheFirstStatus_AnnouncesNothing_EvenWithOldFailures()
    {
        var notifier = new BackupNotifier();

        Assert.Empty(notifier.Observe(Status(Item("failed"), Item(overdue: true, name: "Other")), Now));
    }

    [Fact]
    public void AFailedRestoreTest_IsAnnouncedAsOne()
    {
        var notifier = new BackupNotifier();
        notifier.Observe(Status(Item()), Now);

        var message = Assert.Single(notifier.Observe(Status(Item("failed", name: "Restore test of Dev")), Now));

        Assert.Equal("A restore test failed", message.Title);
        Assert.Contains("latest backup of Dev on Local could not be restored", message.Text);
    }

    [Fact]
    public void ANewFailure_IsAnnouncedOnce()
    {
        var notifier = new BackupNotifier();
        notifier.Observe(Status(Item()), Now);
        var failed = Item("failed");

        var first = notifier.Observe(Status(failed), Now);
        var again = notifier.Observe(Status(failed), Now);

        var message = Assert.Single(first);
        Assert.Equal("A backup failed", message.Title);
        Assert.Contains("Dev to Local: The disk is full.", message.Text);
        Assert.Empty(again);
    }

    [Fact]
    public void AnOverdueItem_IsAnnouncedOncePerDay()
    {
        var notifier = new BackupNotifier();
        notifier.Observe(Status(Item()), Now);
        var overdue = Status(Item(overdue: true));

        Assert.Single(notifier.Observe(overdue, Now));
        Assert.Empty(notifier.Observe(overdue, Now.AddHours(1)));
        Assert.Single(notifier.Observe(overdue, Now.AddDays(1)));
    }

    [Fact]
    public void AnItemThatRecovers_IsAnnouncedAgainIfItLapsesAgain()
    {
        var notifier = new BackupNotifier();
        notifier.Observe(Status(Item()), Now);
        Assert.Single(notifier.Observe(Status(Item(overdue: true)), Now));

        notifier.Observe(Status(Item()), Now.AddHours(1));

        Assert.Single(notifier.Observe(Status(Item(overdue: true)), Now.AddHours(2)));
    }
}

public sealed class BackupTrayStatusTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static BackupTarget Target(bool confirmed) => new()
    {
        Id = "t1", Name = "Local", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B"),
        VmRepository = new RepositoryRef("r1", confirmed ? RepositoryKeyState.Confirmed : RepositoryKeyState.PendingKey),
        HostRepository = new RepositoryRef("r2", confirmed ? RepositoryKeyState.Confirmed : RepositoryKeyState.PendingKey),
    };

    [Fact]
    public void Nothing_IsConfiguredUntilAKeyIsConfirmed()
    {
        var status = BackupTrayStatus.Build(new BackupConfig { Targets = [Target(false)] }, new BackupHistoryStore(_directory), new Dictionary<Guid, string>(), Now, false, 0);

        Assert.False(status.Configured);
        Assert.Equal(1, status.PendingKeys);
        Assert.Equal("none", BackupTrayStatus.Summary(status));
    }

    [Fact]
    public void AFailedLastRun_MeansAttention_AndCarriesTheReason()
    {
        var history = new BackupHistoryStore(_directory);
        history.Add(new BackupRunRecord(
            Guid.NewGuid(), BackupCoordinator.VmItem(VmId), VmId, "Dev", "t1", BackupTrigger.Schedule, Now, Now, BackupOutcome.Failed, "Disk full.", null, 0, []));
        var config = new BackupConfig { Targets = [Target(true)], Policies = [new BackupPolicy { VmId = VmId.ToString(), TargetId = "t1" }] };

        var status = BackupTrayStatus.Build(config, history, new Dictionary<Guid, string> { [VmId] = "Dev" }, Now, true, 1);

        Assert.True(status.Running);
        Assert.Equal(1, status.Waiting);
        var item = status.Items.Single(i => i.Name == "Dev");
        Assert.Equal("failed", item.Outcome);
        Assert.Equal("Disk full.", item.Reason);
        Assert.Equal("attention", BackupTrayStatus.Summary(status));
    }

    private static RestoreTestRecord Test(RestoreTestOutcome outcome, int minutesAgo, string reason = "The guest did not report in.") =>
        new(Guid.NewGuid(), VmId, "Dev", "t1", "snap", BackupTrigger.Schedule, Now.AddMinutes(-minutesAgo), Now.AddMinutes(-minutesAgo), outcome, reason, []);

    [Fact]
    public void ALatestFailedRestoreTest_IsAFailedItem_UntilALaterTestClearsIt()
    {
        var history = new BackupHistoryStore(_directory);
        history.Add(new BackupRunRecord(Guid.NewGuid(), "host", null, null, "t1", BackupTrigger.Schedule, Now, Now, BackupOutcome.Succeeded, null, "s", 1, []));
        var config = new BackupConfig { Targets = [Target(true)] };
        var failed = Test(RestoreTestOutcome.Failed, 30);

        var broken = BackupTrayStatus.Build(config, history, new Dictionary<Guid, string>(), Now, false, 0, [failed, Test(RestoreTestOutcome.Passed, 90)]);
        var fixedLater = BackupTrayStatus.Build(config, history, new Dictionary<Guid, string>(), Now, false, 0, [failed, Test(RestoreTestOutcome.Passed, 5)]);

        var item = broken.Items.Single(i => i.Name == "Restore test of Dev");
        Assert.Equal("failed", item.Outcome);
        Assert.Equal("The guest did not report in.", item.Reason);
        Assert.Equal("attention", BackupTrayStatus.Summary(broken));
        Assert.Equal("ok", BackupTrayStatus.Summary(fixedLater));
        Assert.DoesNotContain(fixedLater.Items, i => i.Name.StartsWith("Restore test", StringComparison.Ordinal));
    }

    [Fact]
    public void WithOnlyGoodRuns_TheSummaryIsOk()
    {
        var history = new BackupHistoryStore(_directory);
        history.Add(new BackupRunRecord(Guid.NewGuid(), "host", null, null, "t1", BackupTrigger.Schedule, Now, Now, BackupOutcome.Succeeded, null, "s", 1, []));

        var status = BackupTrayStatus.Build(new BackupConfig { Targets = [Target(true)] }, history, new Dictionary<Guid, string>(), Now, false, 0);

        Assert.Equal("ok", BackupTrayStatus.Summary(status));
        Assert.Equal("Host settings", status.Items.Single().Name);
    }

    [Theory]
    [InlineData("attention", "A backup needs attention")]
    [InlineData("ok", "Backups are up to date")]
    [InlineData("none", null)]
    [InlineData(null, null)]
    public void TheAmbientScreen_ShowsOneMutedLine(string? summary, string? expected) =>
        Assert.Equal(expected, AmbientCoordinator.BackupText(summary));
}
