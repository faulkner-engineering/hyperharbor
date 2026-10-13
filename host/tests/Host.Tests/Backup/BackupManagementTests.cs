using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Lifecycle;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class BackupManagementTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly MemorySecrets _secrets = new();
    private readonly FakeKopiaRunner _runner = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly BackupConfigStore _config;
    private readonly BackupManagementService _service;
    private readonly FakeRepositories _repositories;

    public BackupManagementTests()
    {
        _config = new BackupConfigStore(_directory);
        _repositories = new FakeRepositories(_directory);
        var kopia = new KopiaCli(_runner);
        _service = new BackupManagementService(
            _config, _secrets, new RecoveryKeyService(_secrets, _time), new BackupTargetTester(kopia, _secrets, _directory),
            _repositories, kopia, _directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Tail(RecoveryKeyOffer offer) => string.Join('-', offer.RecoveryKey.Split('-')[^RecoveryKeyService.ConfirmGroups..]);

    private static BackupTarget Local(string name = "Local") =>
        new() { Id = "ignored", Name = name, Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\Backups") };

    private static BackupTarget S3() => new()
    {
        Id = "ignored", Name = "Cloud", Kind = BackupTargetKind.S3,
        S3 = new S3TargetSettings("s3.example.com", "bucket", null, null, UseTls: true, string.Empty, string.Empty),
    };

    [Fact]
    public void AddTarget_AssignsIdsAndStartsWithBothKeysPending()
    {
        var target = _service.AddTarget(Local(), new TargetCredentials());

        Assert.NotEqual("ignored", target.Id);
        Assert.Equal(RepositoryKeyState.PendingKey, target.VmRepository!.KeyState);
        Assert.Equal(RepositoryKeyState.PendingKey, target.HostRepository!.KeyState);
        Assert.NotEqual(target.VmRepository.RepoId, target.HostRepository.RepoId);
        Assert.Single(_config.Load().Targets);
    }

    [Fact]
    public void AddTarget_StoresRemoteKeysInTheVaultAndOnlyReferencesInTheConfig()
    {
        var target = _service.AddTarget(S3(), new TargetCredentials(S3AccessKey: "access-value", S3SecretKey: "secret-value"));

        Assert.Equal("access-value", _secrets.Get(target.S3!.AccessKeyRef));
        Assert.Equal("secret-value", _secrets.Get(target.S3.SecretKeyRef));
        var saved = File.ReadAllText(Path.Combine(_directory, BackupConfigStore.FileName));
        Assert.DoesNotContain("access-value", saved);
        Assert.DoesNotContain("secret-value", saved);
    }

    [Fact]
    public void AddTarget_RejectsMissingKeysAndDuplicateNames()
    {
        Assert.Throws<LifecycleValidationException>(() => _service.AddTarget(S3(), new TargetCredentials(S3AccessKey: "only-one")));

        _service.AddTarget(Local(), new TargetCredentials());
        var duplicate = Assert.Throws<LifecycleValidationException>(() => _service.AddTarget(Local("LOCAL"), new TargetCredentials()));

        Assert.Contains("name", duplicate.Errors.Select(e => e.Field));
    }

    [Fact]
    public void TargetCredentials_HideTheirValues() =>
        Assert.DoesNotContain("secret-value", new TargetCredentials(S3SecretKey: "secret-value").ToString());

    [Fact]
    public void ConfirmKey_StoresOnePassphraseForBothRepositoriesAndUnlocksThem()
    {
        var target = _service.AddTarget(Local(), new TargetCredentials());
        var offer = _service.BeginKey(target.Id);
        var typed = string.Join('-', offer.RecoveryKey.Split('-')[^RecoveryKeyService.ConfirmGroups..]);

        var confirmed = _service.ConfirmKey(offer.SetupId, typed);

        Assert.Equal(RepositoryKeyState.Confirmed, confirmed.VmRepository!.KeyState);
        Assert.Equal(RepositoryKeyState.Confirmed, confirmed.HostRepository!.KeyState);
        var vm = _secrets.Get(RecoveryKeyService.PassphraseReference(confirmed.VmRepository.RepoId));
        Assert.Equal(offer.RecoveryKey, vm);
        Assert.Equal(vm, _secrets.Get(RecoveryKeyService.PassphraseReference(confirmed.HostRepository.RepoId)));
        Assert.Equal(RepositoryKeyState.Confirmed, _service.Find(target.Id).VmRepository!.KeyState);
    }

    [Fact]
    public void ConfirmKey_WithAWrongEntry_LeavesTheTargetPending()
    {
        var target = _service.AddTarget(Local(), new TargetCredentials());
        var offer = _service.BeginKey(target.Id);

        Assert.Throws<RecoveryKeyException>(() => _service.ConfirmKey(offer.SetupId, "AAAA-AAAA-AAAA-AAAA"));

        Assert.Equal(RepositoryKeyState.PendingKey, _service.Find(target.Id).VmRepository!.KeyState);
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public void BeginKey_AfterConfirmation_IsRefused()
    {
        var target = _service.AddTarget(Local(), new TargetCredentials());
        var offer = _service.BeginKey(target.Id);
        _service.ConfirmKey(offer.SetupId, Tail(offer));

        Assert.Throws<LifecycleConflictException>(() => _service.BeginKey(target.Id));
    }

    [Fact]
    public void UpdateTarget_ChangesOnlyWhatWasGiven_AndCanClearTheBandwidthLimit()
    {
        var target = _service.AddTarget(Local() with { DefaultBandwidthKbps = 500 }, new TargetCredentials());

        var renamed = _service.UpdateTarget(target.Id, new TargetChanges(Name: "Renamed", Retention: new RetentionPolicy(3, 2, 1)));
        var cleared = _service.UpdateTarget(target.Id, new TargetChanges(ClearBandwidth: true));

        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal(500, renamed.DefaultBandwidthKbps);
        Assert.Equal(new RetentionPolicy(3, 2, 1), cleared.DefaultRetention);
        Assert.Null(cleared.DefaultBandwidthKbps);
    }

    [Fact]
    public void UpdateTarget_ReplacesAKeyWithoutTouchingTheOther()
    {
        var target = _service.AddTarget(S3(), new TargetCredentials(S3AccessKey: "a1", S3SecretKey: "s1"));

        _service.UpdateTarget(target.Id, new TargetChanges(Credentials: new TargetCredentials(S3SecretKey: "s2")));

        Assert.Equal("a1", _secrets.Get(target.S3!.AccessKeyRef));
        Assert.Equal("s2", _secrets.Get(target.S3.SecretKeyRef));
    }

    [Fact]
    public void DeleteTarget_RemovesItsPoliciesAndEveryKey()
    {
        var target = _service.AddTarget(S3(), new TargetCredentials(S3AccessKey: "a1", S3SecretKey: "s1"));
        var offer = _service.BeginKey(target.Id);
        _service.ConfirmKey(offer.SetupId, Tail(offer));
        _service.SetPolicy(new BackupPolicy { VmId = VmId.ToString(), TargetId = target.Id });

        _service.DeleteTarget(target.Id);

        var config = _config.Load();
        Assert.Empty(config.Targets);
        Assert.Empty(config.Policies);
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public void SetPolicy_ReplacesThePolicyForTheSameVmAndTarget()
    {
        var target = _service.AddTarget(Local(), new TargetCredentials());

        _service.SetPolicy(new BackupPolicy { VmId = VmId.ToString("N"), TargetId = target.Id });
        _service.SetPolicy(new BackupPolicy { VmId = VmId.ToString("D").ToUpperInvariant(), TargetId = target.Id, Retention = new RetentionPolicy(1, 0, 0) });

        var policy = Assert.Single(_config.Load().Policies);
        Assert.Equal(VmId.ToString("D"), policy.VmId);
        Assert.Equal(1, policy.Retention.Daily);
    }

    [Fact]
    public void SetPolicy_RejectsAnUnknownTargetAndEmptyRetention()
    {
        var target = _service.AddTarget(Local(), new TargetCredentials());

        Assert.Throws<BackupTargetNotFoundException>(() => _service.SetPolicy(new BackupPolicy { VmId = VmId.ToString(), TargetId = "missing" }));
        Assert.Throws<LifecycleValidationException>(() => _service.SetPolicy(
            new BackupPolicy { VmId = VmId.ToString(), TargetId = target.Id, Retention = new RetentionPolicy(0, 0, 0) }));
    }

    [Fact]
    public void RemovePolicy_DropsOnlyThatVmAndTarget()
    {
        var target = _service.AddTarget(Local(), new TargetCredentials());
        var other = Guid.NewGuid();
        _service.SetPolicy(new BackupPolicy { VmId = VmId.ToString(), TargetId = target.Id });
        _service.SetPolicy(new BackupPolicy { VmId = other.ToString(), TargetId = target.Id });

        _service.RemovePolicy(VmId, target.Id);

        Assert.Equal(other.ToString("D"), Assert.Single(_config.Load().Policies).VmId);
    }

    [Fact]
    public async Task AttachExisting_WithTheRightKey_StoresItCanonicallyAndUnlocksBothRepositories()
    {
        var target = await _service.AttachExistingAsync(Local(), new TargetCredentials(), "abcd efgh-ijkl", CancellationToken.None);

        Assert.Equal(RepositoryKeyState.Confirmed, target.VmRepository!.KeyState);
        Assert.Equal("ABCD-EFGH-IJKL", _secrets.Get(RecoveryKeyService.PassphraseReference(target.HostRepository!.RepoId)));
        Assert.Contains(_runner.Calls, call => call.Arguments.Contains("--readonly"));
    }

    [Fact]
    public async Task AttachExisting_WithAWrongKey_KeepsNothing()
    {
        _runner.Respond = _ => new KopiaResult(1, string.Empty, "ERROR invalid repository password");

        await Assert.ThrowsAsync<RecoveryKeyException>(
            () => _service.AttachExistingAsync(S3(), new TargetCredentials(S3AccessKey: "a", S3SecretKey: "s"), "AAAA-BBBB", CancellationToken.None));

        Assert.Empty(_config.Load().Targets);
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public async Task AttachExisting_WithNoRepositoryThere_ExplainsThatNothingWasFound()
    {
        _runner.Respond = _ => new KopiaResult(1, string.Empty, "ERROR found no repository");

        var ex = await Assert.ThrowsAsync<BackupException>(
            () => _service.AttachExistingAsync(Local(), new TargetCredentials(), "AAAA-BBBB", CancellationToken.None));

        Assert.Contains("No HyperHarbor backups", ex.Message);
        Assert.Empty(_config.Load().Targets);
    }

    [Fact]
    public async Task Scan_GroupsVmSnapshotsAndTakesTheNameFromTheNewestDescription()
    {
        var target = _service.AddTarget(Local(), new TargetCredentials());
        var offer = _service.BeginKey(target.Id);
        _service.ConfirmKey(offer.SetupId, Tail(offer));
        var source = $@"D:\vms\.hh-backup\{VmId:N}";
        _runner.Respond = invocation => invocation.Arguments.Contains("list")
            ? new KopiaResult(0, $$"""
                [{ "id": "old", "source": { "path": "{{source.Replace("\\", "\\\\")}}" }, "description": "hyperharbor vm={{VmId}} name=Old Name run=aa", "startTime": "2026-10-10T02:00:00Z" },
                 { "id": "new", "source": { "path": "{{source.Replace("\\", "\\\\")}}" }, "description": "hyperharbor vm={{VmId}} name=Dev Box run=bb", "startTime": "2026-10-11T02:00:00Z" },
                 { "id": "other", "source": { "path": "D:\\somewhere" }, "startTime": "2026-10-11T02:00:00Z" }]
                """, string.Empty)
            : new KopiaResult(0, string.Empty, string.Empty);

        var catalog = await _service.ScanAsync(target.Id, CancellationToken.None);

        var vm = Assert.Single(catalog.Vms);
        Assert.Equal(VmId, vm.VmId);
        Assert.Equal("Dev Box", vm.Name);
        Assert.Equal(["new", "old"], vm.Snapshots.Select(s => s.Id));
        Assert.Equal(3, catalog.HostSnapshots.Count);
    }

    [Fact]
    public async Task Scan_OfAPendingTarget_ListsNothing()
    {
        var target = _service.AddTarget(Local(), new TargetCredentials());

        var catalog = await _service.ScanAsync(target.Id, CancellationToken.None);

        Assert.Empty(catalog.Vms);
        Assert.Empty(catalog.HostSnapshots);
        Assert.Equal(0, _repositories.Opened);
    }

    [Theory]
    [InlineData("hyperharbor vm=1 name=Dev Box run=ab", "Dev Box")]
    [InlineData("hyperharbor vm=1 name=a run=b run=ab", "a run=b")]
    [InlineData("nightly", null)]
    public void NameOf_ReadsTheNameBetweenTheMarkers(string description, string? expected) =>
        Assert.Equal(expected, BackupManagementService.NameOf(
            [new KopiaSnapshot("s", "h", "u", "p", DateTimeOffset.UnixEpoch, null, 0, description)]));

    [Theory]
    [InlineData("abcd-efgh ijkl", "ABCD-EFGH-IJKL")]
    [InlineData("ABCDEFGHIJ", "ABCD-EFGH-IJ")]
    [InlineData("  ", "")]
    public void Canonical_RegroupsTypedKeys(string typed, string expected) =>
        Assert.Equal(expected, RecoveryKeyService.Canonical(typed));
}

public sealed class BackupStatusBuilderTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static BackupTarget Target(ScheduleSpec? schedule = null, bool confirmed = true) => new()
    {
        Id = "t1", Name = "Local", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B"),
        VmRepository = new RepositoryRef("r1", confirmed ? RepositoryKeyState.Confirmed : RepositoryKeyState.PendingKey),
        HostRepository = new RepositoryRef("r2", confirmed ? RepositoryKeyState.Confirmed : RepositoryKeyState.PendingKey),
        Schedule = schedule ?? ScheduleSpec.Nightly,
    };

    private static BackupRunRecord Run(string item, BackupOutcome outcome, DateTimeOffset finished) =>
        new(Guid.NewGuid(), item, item == "host" ? null : VmId, "Dev", "t1", BackupTrigger.Schedule, finished, finished, outcome, null, "s", 1, []);

    [Theory]
    [InlineData(null, 2)]
    [InlineData(new[] { DayOfWeek.Monday, DayOfWeek.Thursday }, 8)]
    [InlineData(new[] { DayOfWeek.Sunday }, 14)]
    public void OverdueAfter_IsTwiceTheLongestGap(DayOfWeek[]? days, int expectedDays) =>
        Assert.Equal(TimeSpan.FromDays(expectedDays), BackupStatusBuilder.OverdueAfter(new ScheduleSpec(new TimeOnly(2, 0), days)));

    [Fact]
    public void Build_ListsTheHostAndEachPolicyVm_AndFlagsAStaleSuccess()
    {
        var history = new BackupHistoryStore(_directory);
        history.Add(Run("host", BackupOutcome.Succeeded, Now.AddHours(-3)));
        history.Add(Run(BackupCoordinator.VmItem(VmId), BackupOutcome.Succeeded, Now.AddDays(-5)));
        history.Add(Run(BackupCoordinator.VmItem(VmId), BackupOutcome.Failed, Now.AddHours(-1)));
        var config = new BackupConfig
        {
            Targets = [Target()],
            Policies = [new BackupPolicy { VmId = VmId.ToString(), TargetId = "t1" }],
        };

        var status = BackupStatusBuilder.Build(config, history, new Dictionary<Guid, string> { [VmId] = "Dev" }, Now);

        Assert.Equal(2, status.Count);
        Assert.False(status[0].Overdue);
        Assert.True(status[1].Overdue);
        Assert.Equal(BackupOutcome.Failed, status[1].LastRun!.Outcome);
        Assert.Equal(Now.AddDays(-5), status[1].LastSuccessAt);
    }

    [Fact]
    public void Build_SkipsPendingTargetsAndDisabledPolicies_AndNeverRunIsNotOverdue()
    {
        var history = new BackupHistoryStore(_directory);
        var pending = new BackupConfig { Targets = [Target(confirmed: false)] };
        var disabled = new BackupConfig
        {
            Targets = [Target()],
            Policies = [new BackupPolicy { VmId = VmId.ToString(), TargetId = "t1", Enabled = false }],
        };

        Assert.Empty(BackupStatusBuilder.Build(pending, history, new Dictionary<Guid, string>(), Now));
        var only = Assert.Single(BackupStatusBuilder.Build(disabled, history, new Dictionary<Guid, string>(), Now));
        Assert.Equal("host", only.Item);
        Assert.False(only.Overdue);
        Assert.Null(only.LastRun);
    }
}
