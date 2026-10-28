using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Tests.Backup;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Measures what the real kopia.exe uploads for an encrypted disk: random data stands in for BitLocker ciphertext, and
/// scattered 4 KiB writes for a guest's small changes. Compares the disk splitter with kopia's default and checks the
/// upload measurement itself. An end-to-end test (scripts\e2e.ps1). Writes about 1 GiB of temporary files and touches
/// nothing else.
/// </summary>
[Trait("Category", "E2E")]
public sealed class KopiaDedupLiveTests(ITestOutputHelper output) : IDisposable
{
    private const int DiskBytes = 256 * 1024 * 1024;
    private const int SectorBytes = 4096;
    private const int Writes = 100;
    private const string Passphrase = "live-test-passphrase";

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [E2EFact]
    public async Task Live_ScatteredWritesToAnEncryptedDisk_UploadLessWithTheDiskSplitter()
    {
        var cli = new KopiaCli(new KopiaProcessRunner(E2E.KopiaExe));
        var target = new BackupTarget
        {
            Id = "t1", Name = "Live", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(Path.Combine(_root, "dest")),
        };
        var repository = KopiaBackendFactory.For(target, RepositoryRole.Vm, "dedup", _root, new MemorySecrets());
        await cli.CreateRepositoryAsync(repository, Passphrase, default);

        var disk = await MeasureAsync(cli, repository, "disk", KopiaCli.DiskSplitter);
        var standard = await MeasureAsync(cli, repository, "default", null);

        Assert.True(disk.First >= DiskBytes * 0.95, $"first snapshot uploaded {disk.First}");
        Assert.True(disk.Unchanged < 1024 * 1024, $"an unchanged snapshot uploaded {disk.Unchanged}");
        Assert.True(disk.Changed <= Writes * 1.25 * 1024 * 1024, $"{Writes} writes uploaded {disk.Changed} with {KopiaCli.DiskSplitter}");
        Assert.True(standard.Changed > disk.Changed * 1.5, $"default {standard.Changed}, {KopiaCli.DiskSplitter} {disk.Changed}");
    }

    private async Task<(long First, long Unchanged, long Changed)> MeasureAsync(KopiaCli cli, KopiaRepository repository, string name, string? splitter)
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var file = Path.Combine(source, "disk-0.vhdx");
        var random = new Random(20261027);
        var data = new byte[DiskBytes];
        random.NextBytes(data);
        await File.WriteAllBytesAsync(file, data);
        await cli.SetPolicyAsync(repository, Passphrase, source, new RetentionPolicy(7, 4, 6), default, splitter);

        var first = await UploadAsync(cli, repository, source);
        var unchanged = await UploadAsync(cli, repository, source);

        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write))
        {
            var sector = new byte[SectorBytes];
            for (var i = 0; i < Writes; i++)
            {
                random.NextBytes(sector);
                stream.Position = (long)random.Next(DiskBytes / SectorBytes) * SectorBytes;
                await stream.WriteAsync(sector);
            }
        }

        var changed = await UploadAsync(cli, repository, source);
        output.WriteLine($"{splitter ?? "kopia default"}: first {Mib(first)} MiB, unchanged {Mib(unchanged)} MiB, {Writes} writes {Mib(changed)} MiB");
        return (first, unchanged, changed);
    }

    private static async Task<long> UploadAsync(KopiaCli cli, KopiaRepository repository, string source)
    {
        var (_, uploaded) = await cli.CreateMeasuredSnapshotAsync(repository, Passphrase, source, "dedup live test", default);
        return uploaded ?? throw new InvalidOperationException("The upload was not measured.");
    }

    private static string Mib(long bytes) => (bytes / (1024.0 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
