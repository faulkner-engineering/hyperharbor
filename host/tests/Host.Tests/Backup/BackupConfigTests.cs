using HyperHarbor.Host.Core.Backup;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class BackupConfigTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static BackupTarget Local(string path = @"D:\Backups") =>
        new() { Id = "t1", Name = "Local", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(path) };

    [Fact]
    public void Retention_DefaultsToSevenDailyFourWeeklySixMonthly()
    {
        Assert.Equal(new RetentionPolicy(7, 4, 6), Local().DefaultRetention);
        Assert.Equal(new TimeOnly(2, 0), Local().Schedule.Time);
    }

    [Fact]
    public void Validate_AcceptsACompleteLocalTarget() => Assert.Empty(Local().Validate());

    [Theory]
    [InlineData(BackupTargetKind.Local)]
    [InlineData(BackupTargetKind.S3)]
    [InlineData(BackupTargetKind.Azure)]
    public void Validate_RequiresTheKindsSettings(BackupTargetKind kind) =>
        Assert.NotEmpty(new BackupTarget { Id = "t", Name = "x", Kind = kind }.Validate());

    [Fact]
    public void Validate_RejectsEmptyRetentionAndZeroBandwidth()
    {
        var target = Local() with { DefaultRetention = new RetentionPolicy(0, 0, 0), DefaultBandwidthKbps = 0 };

        Assert.Equal(2, target.Validate().Count);
    }

    [Fact]
    public void Store_RoundTripsTargetsAndPolicies_WithoutSecrets()
    {
        var store = new BackupConfigStore(_directory);
        var s3 = new BackupTarget
        {
            Id = "t2", Name = "Cloud", Kind = BackupTargetKind.S3,
            S3 = new S3TargetSettings("s3.example.com", "bucket", null, "hh", true, "key:t2:access", "key:t2:secret"),
            VmRepository = new RepositoryRef("r1", RepositoryKeyState.Confirmed),
        };
        store.Update(c => c with
        {
            Targets = [Local(), s3],
            Policies = [new BackupPolicy { VmId = "vm1", TargetId = "t1", GpuLiveCopy = GpuLiveCopy.AllowLive, Conditions = new RunConditions(Window: new TimeWindow(new TimeOnly(1, 0), new TimeOnly(6, 30))) }],
        });

        var loaded = new BackupConfigStore(_directory).Load();

        Assert.Equal(2, loaded.Targets.Count);
        Assert.Equal("bucket", loaded.Targets[1].S3!.Bucket);
        Assert.Equal(RepositoryKeyState.Confirmed, loaded.Targets[1].VmRepository!.KeyState);
        Assert.Equal(GpuLiveCopy.AllowLive, loaded.Policies[0].GpuLiveCopy);
        Assert.Equal(new TimeOnly(6, 30), loaded.Policies[0].Conditions.Window!.End);
        Assert.Contains("\"allowLive\"", File.ReadAllText(Path.Combine(_directory, BackupConfigStore.FileName)));
    }

    [Fact]
    public void Store_WhenMissing_IsEmpty() => Assert.Empty(new BackupConfigStore(_directory).Load().Targets);

    [Fact]
    public void SecretStore_RoundTripsThroughDpapi_AndKeepsPlaintextOffDisk()
    {
        var store = new BackupSecretStore(_directory);
        store.Set("repo:r1:passphrase", "correct-horse-battery");
        store.Set("key:t2:secret", "s3-secret-value");
        store.Remove("key:t2:secret");

        var reopened = new BackupSecretStore(_directory);

        Assert.Equal("correct-horse-battery", reopened.Get("repo:r1:passphrase"));
        Assert.Null(reopened.Get("key:t2:secret"));
        Assert.DoesNotContain("correct-horse", File.ReadAllText(Path.Combine(_directory, BackupSecretStore.FileName), System.Text.Encoding.Latin1));
    }

    [Fact]
    public void DataFormat_IsAtLeastThree_AndTheBackupFolderIsNotCopiedInUpdateBackups()
    {
        Assert.True(HyperHarbor.Host.Core.Installation.DataFormat.Current >= 3);
        Assert.Contains("backup", HyperHarbor.Host.Core.Installation.DataBackup.Excluded);
    }
}
