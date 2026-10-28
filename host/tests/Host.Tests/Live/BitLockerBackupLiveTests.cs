using System.Globalization;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Measures what backups of a real Windows guest upload: backs the VM up while it is off, runs it for a while, shuts it
/// down, and backs it up again, for a few rounds, through the real VmBackupService and kopia. Between backups it compares
/// the disk file in 4 KiB sectors, so each upload can be set against how much the guest really changed and what other
/// fixed piece sizes would have cost. Leaves the VM off and unchanged; the repository is temporary.
/// HH_BACKUP_BITLOCKER_LIVE=1, HH_KOPIA_EXE=&lt;path to kopia.exe&gt;, HH_LIVE_GUEST_VM=&lt;VM id&gt; (an off VM without
/// checkpoints). HH_BITLOCKER_RUN_MINUTES (default 10) and HH_BITLOCKER_ROUNDS (default 2). HH_LIVE_LOG=&lt;file&gt; also
/// writes each step there as it happens (xUnit shows the output only at the end). Needs free space for one full copy of
/// the disk.
/// </summary>
public sealed class BitLockerBackupLiveTests(ITestOutputHelper output) : IDisposable
{
    private const int SectorBytes = 4096;
    private static readonly int[] PieceSizes = [256 * 1024, 1024 * 1024, 4 * 1024 * 1024];
    private static readonly TimeSpan PowerCallTimeout = TimeSpan.FromMinutes(2);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-live", Guid.NewGuid().ToString("N"));
    private readonly string? _log = Environment.GetEnvironmentVariable("HH_LIVE_LOG");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [EnvironmentFact("HH_BACKUP_BITLOCKER_LIVE", "HH_KOPIA_EXE", "HH_LIVE_GUEST_VM")]
    public async Task Live_BackupsOfARealGuest_UploadAboutWhatChanged()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var runFor = TimeSpan.FromMinutes(Setting("HH_BITLOCKER_RUN_MINUTES", 10));
        var rounds = Setting("HH_BITLOCKER_ROUNDS", 2);
        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));
        var vm = await inventory.GetAsync(vmId, default) ?? throw new InvalidOperationException("HH_LIVE_GUEST_VM is not a VM on this host.");
        Assert.Equal(VmState.Off, vm.State);

        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var secrets = new MemorySecrets();
        secrets.Set(RecoveryKeyService.PassphraseReference("repo-live"), "live-passphrase");
        var target = new BackupTarget
        {
            Id = "t1", Name = "Live", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(Path.Combine(_root, "dest")),
            VmRepository = new RepositoryRef("repo-live", RepositoryKeyState.Confirmed),
        };
        var kopia = new KopiaCli(new KopiaProcessRunner(Environment.GetEnvironmentVariable("HH_KOPIA_EXE")!));
        var storage = new CimHyperVStorage();
        var definitions = new CimHyperVDefinitionReader(new CimHyperVCompute());
        var service = new VmBackupService(
            inventory, definitions, new CimHyperVCheckpoints(), storage, new WindowsBackupStaging(), new BackupRepositories(kopia, secrets, data), kopia,
            new BackupHistoryStore(data), new PerformanceStore(data), _ => false, TimeProvider.System, NullLogger<VmBackupService>.Instance, "live");
        var request = new VmBackupRequest(vmId, target, new BackupPolicy { VmId = vmId.ToString("D"), TargetId = "t1" }, BackupTrigger.Manual);
        var disk = Assert.Single((await definitions.ReadAsync(vmId, default)).Disks).Path;
        var power = new CimHyperVPowerInvoker();
        Log($"{vm.Name}: {disk}, {Gib(new FileInfo(disk).Length)} GiB file, probe: {new VhdxEncryptionProbe().Probe(disk)}");

        var sectors = Sectors(disk);
        Log("Backing up (round 0)");
        var first = await service.RunAsync(request, (step, percent) => Log($"  {percent}% {step}"), default);
        Report("Round 0 (first backup)", first, null);
        Assert.Equal(BackupOutcome.Succeeded, first.Outcome);

        for (var round = 1; round <= rounds; round++)
        {
            Log($"Round {round}: starting the VM");
            await power.InvokeAsync(vmId, VmAction.Start, default).WaitAsync(PowerCallTimeout);
            await WaitForStateAsync(inventory, vmId, VmState.Running, TimeSpan.FromMinutes(2));
            await Task.Delay(runFor);
            Log($"Round {round}: shutting the VM down");
            try
            {
                await power.InvokeAsync(vmId, VmAction.Shutdown, default).WaitAsync(PowerCallTimeout);
                await WaitForStateAsync(inventory, vmId, VmState.Off, TimeSpan.FromMinutes(10));
            }
            catch (TimeoutException)
            {
                // A guest busy installing updates can leave the shutdown request hanging; the disk still compares.
                Log($"Round {round}: the guest did not shut down, so it is turned off");
                await power.InvokeAsync(vmId, VmAction.TurnOff, default).WaitAsync(PowerCallTimeout);
                await WaitForStateAsync(inventory, vmId, VmState.Off, TimeSpan.FromMinutes(2));
            }

            Log($"Round {round}: comparing the disk");
            var next = Sectors(disk);
            var changes = Changes(sectors, next);
            sectors = next;
            var record = await service.RunAsync(request, (step, percent) => Log($"  {percent}% {step}"), default);
            Report($"Round {round} (after {runFor.TotalMinutes:0} minutes running)", record, changes);
            Assert.Equal(BackupOutcome.Succeeded, record.Outcome);
            Assert.NotNull(record.UploadedBytes);
        }
    }

    private void Log(string line)
    {
        output.WriteLine(line);
        if (_log is not null)
        {
            File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
    }

    private void Report(string title, BackupRunRecord record, Dictionary<int, long>? changes)
    {
        Log($"{title}: {record.Outcome} {record.Reason}, total {Gib(record.TotalBytes)} GiB, uploaded {Gib(record.UploadedBytes ?? -1)} GiB, encrypted disks {record.EncryptedDisks}");
        foreach (var warning in record.Warnings)
        {
            Log($"  warning: {warning}");
        }

        if (changes is not null)
        {
            Log($"  changed: {Gib(changes[SectorBytes] * SectorBytes)} GiB in 4 KiB sectors; "
                + string.Join(", ", PieceSizes.Select(size => $"{Gib(changes[size] * size)} GiB in {size / 1024} KiB pieces")));
        }
    }

    /// <summary>One hash per 4 KiB sector of the file. VHDX blocks keep their place in the file, so file offsets compare.</summary>
    private static List<int> Sectors(string path)
    {
        var hashes = new List<int>();
        var buffer = new byte[1024 * 1024];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, FileOptions.SequentialScan);
        int read;
        while ((read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
        {
            for (var offset = 0; offset < read; offset += SectorBytes)
            {
                var hash = new HashCode();
                hash.AddBytes(buffer.AsSpan(offset, Math.Min(SectorBytes, read - offset)));
                hashes.Add(hash.ToHashCode());
            }
        }

        return hashes;
    }

    /// <summary>How many 4 KiB sectors, and how many aligned pieces of each size, differ between two scans.</summary>
    private static Dictionary<int, long> Changes(List<int> before, List<int> after)
    {
        var counts = new Dictionary<int, long> { [SectorBytes] = 0 };
        var pieces = PieceSizes.ToDictionary(size => size, _ => new HashSet<long>());
        for (var i = 0; i < after.Count; i++)
        {
            if (i < before.Count && before[i] == after[i])
            {
                continue;
            }

            counts[SectorBytes]++;
            foreach (var size in PieceSizes)
            {
                pieces[size].Add((long)i * SectorBytes / size);
            }
        }

        foreach (var size in PieceSizes)
        {
            counts[size] = pieces[size].Count;
        }

        return counts;
    }

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string Gib(long bytes) => (bytes / (double)(1L << 30)).ToString("0.00", CultureInfo.InvariantCulture);

    private static async Task WaitForStateAsync(IVmInventory inventory, Guid vmId, VmState state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await inventory.GetAsync(vmId, default))?.State == state)
            {
                return;
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException($"The VM did not reach {state}.");
    }
}
