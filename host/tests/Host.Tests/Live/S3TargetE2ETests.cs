using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// An S3 backup target end to end, with rclone's S3 server on loopback standing in for the cloud: the connection test,
/// a VM backup (Hyper-V faked, the VM off) with its measured upload, and a restore that gives back the same disk.
/// An end-to-end test (scripts\e2e.ps1, which fetches the pinned rclone into HH_RCLONE_EXE).
/// </summary>
[Trait("Category", "E2E")]
public sealed class S3TargetE2ETests(ITestOutputHelper output) : IDisposable
{
    private const int DiskBytes = 64 << 20;
    private const string Bucket = "hyperharbor";
    private static readonly Guid VmId = Guid.Parse("8c1d2e3f-4a5b-4c6d-9e7f-0a1b2c3d4e5f");
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private Process? _server;

    public void Dispose()
    {
        try
        {
            _server?.Kill(entireProcessTree: true);
            _server?.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }

        _server?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [E2EFact]
    public async Task AnS3Target_PassesItsTest_BacksAVmUp_AndRestoresIt()
    {
        var accessKey = "e2e" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        var secretKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var port = await StartServerAsync(accessKey, secretKey);

        var dataDirectory = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var secrets = new MemorySecrets();
        secrets.Set("s3:access", accessKey);
        secrets.Set("s3:secret", secretKey);
        secrets.Set(RecoveryKeyService.PassphraseReference("repo-s3"), "s3-passphrase");
        var target = new BackupTarget
        {
            Id = "t1", Name = "Cloud", Kind = BackupTargetKind.S3,
            S3 = new S3TargetSettings($"127.0.0.1:{port}", Bucket, null, "hh", UseTls: false, "s3:access", "s3:secret"),
            VmRepository = new RepositoryRef("repo-s3", RepositoryKeyState.Confirmed),
        };
        var kopia = new KopiaCli(new KopiaProcessRunner(E2E.KopiaExe));

        // The connection test the client offers before a target is used.
        var test = await new BackupTargetTester(kopia, secrets, dataDirectory).TestAsync(target, default);
        output.WriteLine("Target test: " + string.Join("; ", test.Steps.Select(s => $"{s.Name} {s.Passed} {s.Detail}")));
        Assert.True(test.Passed);

        // A VM backup to it.
        var disk = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "vms", "Cloudy")).FullName, "Cloudy.vhdx");
        var bytes = RandomNumberGenerator.GetBytes(DiskBytes);
        await File.WriteAllBytesAsync(disk, bytes);
        var inventory = new FakeVmInventory();
        inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Cloudy", VmState.Off));
        var definitions = new FakeDefinitions
        {
            Current = () => new VmDefinition(
                VmId, "Cloudy", 2, null, new ComputeState(2, 2048, 2048, false, false, false, 1), true, null, false, [],
                [new VmDiskDefinition(disk, "scsi", 0, 0)], null, 128, 512),
        };
        var repositories = new BackupRepositories(kopia, secrets, dataDirectory);
        var service = new VmBackupService(
            inventory, definitions, new FakeCheckpoints([]), new FakeHyperVStorage(), new WindowsBackupStaging(), repositories, kopia,
            new BackupHistoryStore(dataDirectory), new PerformanceStore(dataDirectory), _ => false, TimeProvider.System,
            NullLogger<VmBackupService>.Instance, "e2e");
        var request = new VmBackupRequest(VmId, target, new BackupPolicy { VmId = VmId.ToString("D"), TargetId = "t1" }, BackupTrigger.Manual);

        var record = await service.RunAsync(request, (_, _) => { }, default);
        output.WriteLine($"Backup: {record.Outcome} {record.Reason}, uploaded {record.UploadedBytes?.ToString(CultureInfo.InvariantCulture) ?? "not measured"}");

        Assert.Equal(BackupOutcome.Succeeded, record.Outcome);
        Assert.InRange(record.UploadedBytes!.Value, DiskBytes * 0.99, DiskBytes * 1.05);

        // The snapshot restores to the same disk.
        var repository = await repositories.OpenAsync(target, RepositoryRole.Vm, default);
        var snapshot = Assert.Single(await kopia.ListSnapshotsAsync(repository.Repository, repository.Passphrase, default));
        Assert.Equal(record.SnapshotId, snapshot.Id);
        var restored = Path.Combine(_root, "restored");
        await kopia.RestoreAsync(repository.Repository, repository.Passphrase, snapshot.Id, restored, default);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(restored, "disk-0.vhdx"))));
        Assert.True(File.Exists(Path.Combine(restored, BackupManifest.FileName)));
    }

    /// <summary>Starts rclone's S3 server on a free loopback port over a folder with the bucket in it.</summary>
    private async Task<int> StartServerAsync(string accessKey, string secretKey)
    {
        var storage = Directory.CreateDirectory(Path.Combine(_root, "s3")).FullName;
        Directory.CreateDirectory(Path.Combine(storage, Bucket));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var start = new ProcessStartInfo(E2E.Variable("HH_RCLONE_EXE"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "serve", "s3", "--addr", $"127.0.0.1:{port}", "--auth-key", $"{accessKey},{secretKey}", "--config", "", storage })
        {
            start.ArgumentList.Add(argument);
        }

        _server = Process.Start(start) ?? throw new InvalidOperationException("rclone did not start.");
        _server.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { } text)
            {
                output.WriteLine("rclone: " + text);
            }
        };
        _server.BeginErrorReadLine();
        _server.BeginOutputReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return port;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline && !_server.HasExited)
            {
                await Task.Delay(200);
            }
        }
    }
}
