using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;

namespace HyperHarbor.Host.Tests.Backup;

internal sealed class FakeKopiaRunner : IKopiaRunner
{
    public List<KopiaInvocation> Calls { get; } = [];

    public Func<KopiaInvocation, KopiaResult> Respond { get; set; } = _ => new KopiaResult(0, string.Empty, string.Empty);

    public Task<KopiaResult> RunAsync(KopiaInvocation invocation, CancellationToken cancellationToken)
    {
        Calls.Add(invocation);
        return Task.FromResult(Respond(invocation));
    }
}

internal sealed class MemorySecrets : IBackupSecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public string? Get(string reference) => Values.GetValueOrDefault(reference);

    public void Set(string reference, string secret) => Values[reference] = secret;

    public void Remove(string reference) => Values.Remove(reference);
}

public sealed class KopiaTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeKopiaRunner _runner = new();
    private readonly MemorySecrets _secrets = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private BackupTarget S3() => new()
    {
        Id = "t2", Name = "Cloud", Kind = BackupTargetKind.S3,
        S3 = new S3TargetSettings("s3.example.com", "bucket", "eu-1", "hh", UseTls: false, "k:access", "k:secret"),
    };

    private KopiaRepository S3Repository()
    {
        _secrets.Set("k:access", "AKIAEXAMPLEKEY");
        _secrets.Set("k:secret", "super-secret-value");
        return KopiaBackendFactory.For(S3(), RepositoryRole.Vm, "r1", _directory, _secrets);
    }

    [Fact]
    public void Retention_MapsToKopiaPolicyFlags()
    {
        var arguments = KopiaCli.PolicyArguments(@"D:\Source", new RetentionPolicy(7, 4, 6));

        Assert.Equal(["policy", "set", @"D:\Source", "--keep-latest=0", "--keep-hourly=0", "--keep-daily=7", "--keep-weekly=4",
            "--keep-monthly=6", "--keep-annual=0", "--compression=zstd"], arguments);
    }

    [Fact]
    public void Retention_UsesTheValuesGiven()
    {
        var arguments = KopiaCli.PolicyArguments("src", new RetentionPolicy(14, 0, 12));

        Assert.Contains("--keep-daily=14", arguments);
        Assert.Contains("--keep-weekly=0", arguments);
        Assert.Contains("--keep-monthly=12", arguments);
    }

    [Theory]
    [InlineData(8000, "--upload-bytes-per-second=1000000")]
    [InlineData(null, "--upload-bytes-per-second=0")]
    public void Bandwidth_ConvertsKilobitsToBytesPerSecond(int? kilobits, string expected) =>
        Assert.Equal(["repository", "throttle", "set", expected], KopiaCli.ThrottleArguments(kilobits));

    [Fact]
    public void Environment_CarriesThePassphrase_ButTheArgumentsDoNot()
    {
        var repository = S3Repository();

        var arguments = KopiaCli.RepositoryArguments("connect", repository, readOnly: true);
        var environment = KopiaCli.Environment(repository, "the-passphrase");

        Assert.Equal("the-passphrase", environment["KOPIA_PASSWORD"]);
        Assert.Equal("AKIAEXAMPLEKEY", environment["AWS_ACCESS_KEY_ID"]);
        Assert.DoesNotContain("AKIAEXAMPLEKEY", arguments);
        Assert.DoesNotContain("the-passphrase", arguments);
        Assert.Contains("--readonly", arguments);
        Assert.StartsWith(Path.Combine(_directory, "backup", "r1"), environment["KOPIA_CONFIG_PATH"]);
    }

    [Fact]
    public void BackendFactory_BuildsLocalS3AndAzureRepositories_PerRole()
    {
        var local = new BackupTarget { Id = "t1", Name = "L", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\Backups") };
        _secrets.Set("az", "azure-key-value");
        var azure = new BackupTarget { Id = "t3", Name = "A", Kind = BackupTargetKind.Azure, Azure = new AzureTargetSettings("acct", "box", null, "az") };

        var vm = KopiaBackendFactory.For(local, RepositoryRole.Vm, "r1", _directory, _secrets);
        var host = KopiaBackendFactory.For(local, RepositoryRole.Host, "r2", _directory, _secrets);
        var blob = KopiaBackendFactory.For(azure, RepositoryRole.Host, "r3", _directory, _secrets);
        var s3 = S3Repository();

        Assert.Equal(["filesystem", "--path", @"D:\Backups\vm"], vm.Backend.Arguments);
        Assert.Equal(@"D:\Backups\host", host.Backend.Arguments[2]);
        Assert.Contains("--disable-tls", s3.Backend.Arguments);
        Assert.Contains("hh/vm/", s3.Backend.Arguments);
        Assert.Contains("--region", s3.Backend.Arguments);
        Assert.Contains("host/", blob.Backend.Arguments);
        Assert.Equal("azure-key-value", blob.Backend.SecretEnvironment["AZURE_STORAGE_KEY"]);
        Assert.Equal("super-secret-value", s3.Backend.SecretEnvironment["AWS_SECRET_ACCESS_KEY"]);
        Assert.DoesNotContain("azure-key-value", blob.Backend.Arguments);
        Assert.DoesNotContain("super-secret-value", s3.Backend.Arguments);
        Assert.DoesNotContain("super-secret-value", s3.Backend.ToString());
    }

    [Fact]
    public void BackendFactory_WithAMissingKey_Throws() =>
        Assert.Throws<InvalidOperationException>(() => KopiaBackendFactory.For(S3(), RepositoryRole.Vm, "r1", _directory, _secrets));

    [Fact]
    public void Snapshots_ParseListAndCreateOutput_AndIgnoreUnknownFields()
    {
        const string json = """
            [{ "id": "k1", "source": { "host": "PC", "userName": "SYSTEM", "path": "D:\\vm" }, "description": "nightly",
               "startTime": "2026-10-12T02:00:00Z", "endTime": "2026-10-12T02:10:00Z", "stats": { "totalSize": 1234 }, "extra": 1 },
             { "id": "k2", "source": { "path": "x" }, "startTime": "2026-10-11T02:00:00Z", "rootEntry": { "summ": { "size": 99 } } }]
            """;

        var snapshots = KopiaOutput.Snapshots(json);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal("k1", snapshots[0].Id);
        Assert.Equal(1234, snapshots[0].TotalSize);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 2, 10, 0, TimeSpan.Zero), snapshots[0].EndTime);
        Assert.Equal(99, snapshots[1].TotalSize);
        Assert.Equal("k9", KopiaOutput.CreatedSnapshot("""{ "id": "k9", "source": { "path": "p" } }""").Id);
        Assert.Empty(KopiaOutput.Snapshots(""));
    }

    [Fact]
    public void Snapshots_RejectNonJson() =>
        Assert.Throws<KopiaException>(() => KopiaOutput.Snapshots("not json"));

    [Theory]
    [InlineData("ERROR invalid repository password", KopiaFailure.BadPassphrase)]
    [InlineData("error: repository not initialized in the provided storage", KopiaFailure.NotARepository)]
    [InlineData("operation error S3: AccessDenied, StatusCode: 403", KopiaFailure.AccessDenied)]
    [InlineData("dial tcp: lookup s3.example.com: no such host", KopiaFailure.Unreachable)]
    [InlineData("something odd", KopiaFailure.Unknown)]
    public void Classify_MapsErrorTextToAFailure(string text, KopiaFailure expected) =>
        Assert.Equal(expected, KopiaOutput.Classify(text));

    [Fact]
    public async Task Failures_AreSanitized_SoNoSecretReachesTheMessage()
    {
        var repository = S3Repository();
        _runner.Respond = _ => new KopiaResult(1, string.Empty, "failed with key AKIAEXAMPLEKEY secret super-secret-value pw the-passphrase");

        var error = await Assert.ThrowsAsync<KopiaException>(() => new KopiaCli(_runner).ConnectAsync(repository, "the-passphrase", false, default));

        Assert.DoesNotContain("super-secret-value", error.Message);
        Assert.DoesNotContain("the-passphrase", error.Message);
        Assert.DoesNotContain("AKIAEXAMPLEKEY", error.Message);
        Assert.Contains("*****", error.Message);
    }

    [Fact]
    public async Task Cli_RunsSnapshotCreateAndRestoreWithTheRightArguments()
    {
        var repository = S3Repository();
        _runner.Respond = call => call.Arguments[1] == "create"
            ? new KopiaResult(0, """{ "id": "snap1", "source": { "path": "p" }, "startTime": "2026-10-12T02:00:00Z" }""", string.Empty)
            : new KopiaResult(0, string.Empty, string.Empty);
        var cli = new KopiaCli(_runner);

        var snapshot = await cli.CreateSnapshotAsync(repository, "pw", @"D:\stage", "nightly", default);
        await cli.RestoreAsync(repository, "pw", "snap1", @"D:\out", default);

        Assert.Equal("snap1", snapshot.Id);
        Assert.Equal(["snapshot", "create", @"D:\stage", "--json", "--description=nightly"], _runner.Calls[0].Arguments);
        Assert.Equal(["snapshot", "restore", "snap1", @"D:\out", "--write-sparse-files"], _runner.Calls[1].Arguments);
        Assert.Equal(KopiaCli.TransferTimeout, _runner.Calls[0].Timeout);
    }

    [Fact]
    public void Policy_SetsTheSplitter_OnlyWhenGiven()
    {
        Assert.DoesNotContain(KopiaCli.PolicyArguments("src", new RetentionPolicy(7, 4, 6)), a => a.StartsWith("--splitter", StringComparison.Ordinal));
        Assert.Equal("--splitter=FIXED-1M", KopiaCli.PolicyArguments("src", new RetentionPolicy(7, 4, 6), KopiaCli.DiskSplitter)[^1]);
    }

    [Fact]
    public void StoredBytes_PrefersThePackedTotal()
    {
        // Output of kopia 0.23.1 content stats --raw, with and without compressed content.
        const string compressed = "Count: 24\r\nTotal Bytes: 100667295\r\nTotal Packed: 67116663 (compression 33.3%)\r\nBy Method:\r\n";
        const string plain = "Count: 1\nTotal Bytes: 655\nAverage: 655\nHistogram:\n\n        1 between 100 and 1000 (total 683)\n";

        Assert.Equal(67116663, KopiaOutput.StoredBytes(compressed));
        Assert.Equal(655, KopiaOutput.StoredBytes(plain));
        Assert.Null(KopiaOutput.StoredBytes("Count: 0\n"));
        Assert.Null(KopiaOutput.StoredBytes("Total Bytes: 67.1 MB\n"));
    }

    [Fact]
    public void PolicySplitter_ReadsTheAlgorithm_OrNullForTheDefault()
    {
        Assert.Equal("FIXED-1M", KopiaOutput.PolicySplitter("""{ "compression": { "compressorName": "zstd" }, "splitter": { "algorithm": "FIXED-1M" } }"""));
        Assert.Null(KopiaOutput.PolicySplitter("""{ "splitter": {} }"""));
        Assert.Null(KopiaOutput.PolicySplitter(string.Empty));
    }

    [Fact]
    public async Task MeasuredSnapshot_ReportsTheRepositoryGrowth()
    {
        var stored = 1000L;
        _runner.Respond = call => call.Arguments switch
        {
            ["content", "stats", ..] => new KopiaResult(0, $"Count: 3\nTotal Bytes: {stored}\n", string.Empty),
            ["snapshot", "create", ..] => Created(() => stored += 5000),
            _ => new KopiaResult(0, string.Empty, string.Empty),
        };

        var (snapshot, uploaded) = await new KopiaCli(_runner).CreateMeasuredSnapshotAsync(S3Repository(), "pw", @"D:\stage", "nightly", default);

        Assert.Equal("snap1", snapshot.Id);
        Assert.Equal(5000, uploaded);
        Assert.Equal(["content", "stats", "--raw"], _runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task MeasuredSnapshot_IsNotMeasured_WhenStatsFail()
    {
        _runner.Respond = call => call.Arguments switch
        {
            ["content", "stats", ..] => new KopiaResult(1, string.Empty, "unknown command"),
            ["snapshot", "create", ..] => Created(() => { }),
            _ => new KopiaResult(0, string.Empty, string.Empty),
        };

        var (snapshot, uploaded) = await new KopiaCli(_runner).CreateMeasuredSnapshotAsync(S3Repository(), "pw", @"D:\stage", "nightly", default);

        Assert.Equal("snap1", snapshot.Id);
        Assert.Null(uploaded);
    }

    [Fact]
    public async Task MeasuredSnapshots_ThatOverlapInOneRepository_AreNotMeasured()
    {
        var repository = S3Repository();
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var blocking = new BlockingKopiaRunner(firstStarted, releaseFirst);
        var cli = new KopiaCli(blocking);

        var first = cli.CreateMeasuredSnapshotAsync(repository, "pw", @"D:\a", "a", default);
        await firstStarted.Task;
        var second = await cli.CreateMeasuredSnapshotAsync(repository, "pw", @"D:\b", "b", default);
        releaseFirst.SetResult();
        var firstResult = await first;

        Assert.Null(second.UploadedBytes);
        Assert.Null(firstResult.UploadedBytes);

        // Once both are done, the next snapshot has the repository to itself again.
        Assert.NotNull((await cli.CreateMeasuredSnapshotAsync(repository, "pw", @"D:\c", "c", default)).UploadedBytes);
    }

    private static KopiaResult Created(Action onCreate)
    {
        onCreate();
        return new KopiaResult(0, """{ "id": "snap1", "source": { "path": "p" }, "startTime": "2026-10-12T02:00:00Z" }""", string.Empty);
    }

    /// <summary>Holds the first snapshot create until released, so a second one overlaps it.</summary>
    private sealed class BlockingKopiaRunner(TaskCompletionSource started, TaskCompletionSource release) : IKopiaRunner
    {
        private int _creates;

        public async Task<KopiaResult> RunAsync(KopiaInvocation invocation, CancellationToken cancellationToken)
        {
            if (invocation.Arguments is ["snapshot", "create", ..])
            {
                if (Interlocked.Increment(ref _creates) == 1)
                {
                    started.SetResult();
                    await release.Task.ConfigureAwait(false);
                }

                return new KopiaResult(0, """{ "id": "snap1", "source": { "path": "p" }, "startTime": "2026-10-12T02:00:00Z" }""", string.Empty);
            }

            return new KopiaResult(0, "Total Bytes: 10\n", string.Empty);
        }
    }

    [Fact]
    public void RestoreMd_NamesThePasswordManager_AndNeverHoldsASecret()
    {
        var target = S3() with { S3 = S3().S3! with { Prefix = "hh" } };

        var text = RestoreInstructions.Build(target, new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains("password manager", text);
        Assert.Contains("kopia repository connect s3 --bucket bucket --endpoint s3.example.com --prefix hh/vm/", text);
        Assert.Contains("kopia repository connect s3 --bucket bucket --endpoint s3.example.com --prefix hh/host/", text);
        Assert.Contains("Proton Drive, OneDrive, or a NAS", text);
        Assert.Contains("only changed chunks", text);
        Assert.Contains("--access-key <access key> --secret-access-key <secret key>", text);
    }

    [Fact]
    public void RestoreMd_IsWrittenToALocalFolder_AndNotForRemoteTargets()
    {
        var local = new BackupTarget { Id = "t1", Name = "L", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(Path.Combine(_directory, "out")) };

        var path = RestoreInstructionsWriter.WriteForLocal(local, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(path));
        Assert.Contains(Path.Combine(_directory, "out", "vm"), File.ReadAllText(path!));
        Assert.Null(RestoreInstructionsWriter.WriteForLocal(S3(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Tester_Local_WritesAProbe_AndRemovesIt()
    {
        var folder = Path.Combine(_directory, "dest");
        var target = new BackupTarget { Id = "t1", Name = "L", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(folder) };

        var result = await new BackupTargetTester(new KopiaCli(_runner), _secrets, _directory).TestAsync(target, default);

        Assert.True(result.Passed);
        Assert.Empty(Directory.GetFiles(folder));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Tester_Remote_TreatsNoRepositoryYetAsReachable()
    {
        _secrets.Set("k:access", "a-key-value");
        _secrets.Set("k:secret", "a-secret-value");
        _runner.Respond = _ => new KopiaResult(1, string.Empty, "repository not initialized in the provided storage");

        var result = await new BackupTargetTester(new KopiaCli(_runner), _secrets, _directory).TestAsync(S3(), default);

        Assert.True(result.Passed);
        Assert.Contains("--readonly", _runner.Calls[0].Arguments);
    }

    [Theory]
    [InlineData("AccessDenied 403", "refused the keys")]
    [InlineData("dial tcp: no such host", "could not be reached")]
    public async Task Tester_Remote_ReportsWrongKeysAndUnreachableHostsInPlainWords(string error, string expected)
    {
        _secrets.Set("k:access", "a-key-value");
        _secrets.Set("k:secret", "a-secret-value");
        _runner.Respond = _ => new KopiaResult(1, string.Empty, error);

        var result = await new BackupTargetTester(new KopiaCli(_runner), _secrets, _directory).TestAsync(S3(), default);

        Assert.False(result.Passed);
        Assert.Contains(expected, result.Steps[0].Detail);
    }

    [Fact]
    public async Task Tester_Remote_WithoutKeys_FailsBeforeCallingKopia()
    {
        var result = await new BackupTargetTester(new KopiaCli(_runner), _secrets, _directory).TestAsync(S3(), default);

        Assert.False(result.Passed);
        Assert.Empty(_runner.Calls);
    }
}
