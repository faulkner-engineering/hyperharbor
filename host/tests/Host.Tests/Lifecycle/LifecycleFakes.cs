using HyperHarbor.Host.Core.Lifecycle;

namespace HyperHarbor.Host.Tests.Lifecycle;

/// <summary>In-memory Hyper-V storage: disk attachments, parent links, and recorded deletions.</summary>
internal sealed class FakeHyperVStorage : IHyperVStorage
{
    public List<DiskAttachment> Disks { get; } = [];

    /// <summary>Attached ISO images.</summary>
    public List<DiskAttachment> Images { get; } = [];

    public Dictionary<Guid, int> Checkpoints { get; } = [];

    /// <summary>Child path to parent path.</summary>
    public Dictionary<string, string> Parents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Guid> DeletedVms { get; } = [];

    public List<Guid> CheckpointsDeleted { get; } = [];

    public Exception? ThrowOnDeleteVm { get; set; }

    /// <summary>Runs before DeleteVmAsync completes, for example to change state mid-job.</summary>
    public Action? BeforeDeleteVm { get; set; }

    public Task<StorageSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new StorageSnapshot(Disks.ToList(), new Dictionary<Guid, int>(Checkpoints), Images.ToList()));

    public Task<string?> GetParentPathAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(Parents.TryGetValue(path, out var parent) ? parent : null);

    public Task DeleteCheckpointsAsync(Guid vmId, Action<int> progress, CancellationToken cancellationToken)
    {
        CheckpointsDeleted.Add(vmId);
        progress(50);
        progress(100);
        Checkpoints.Remove(vmId);
        Disks.RemoveAll(disk => disk.VmId == vmId && disk.InCheckpoint);
        return Task.CompletedTask;
    }

    public Task DeleteVmAsync(Guid vmId, CancellationToken cancellationToken)
    {
        BeforeDeleteVm?.Invoke();
        if (ThrowOnDeleteVm is not null)
        {
            throw ThrowOnDeleteVm;
        }

        DeletedVms.Add(vmId);
        Disks.RemoveAll(disk => disk.VmId == vmId);
        return Task.CompletedTask;
    }

    public void Attach(Guid vmId, string vmName, string path, bool inCheckpoint = false) =>
        Disks.Add(new DiskAttachment(vmId, vmName, path, inCheckpoint));
}

/// <summary>In-memory disk files.</summary>
internal sealed class FakeDiskFiles : IDiskFiles
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Undeletable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Deleted { get; } = [];

    public long? FreeSpaceMb { get; set; } = 1024 * 1024;

    /// <summary>Files whose Delete throws, as if another process opened them after the check.</summary>
    public HashSet<string> FailOnDelete { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path) => Files.Contains(path);

    public long? AvailableSpaceMb(string path) => FreeSpaceMb;

    public bool CanDelete(string path) => Files.Contains(path) && !Undeletable.Contains(path);

    public void Delete(string path)
    {
        if (FailOnDelete.Contains(path))
        {
            throw new IOException("The process cannot access the file because it is being used by another process.");
        }

        Files.Remove(path);
        Deleted.Add(path);
    }

    public IEnumerable<string> ListDiskFiles(string directory) =>
        Files.Where(file => string.Equals(Path.GetDirectoryName(file), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
}

/// <summary>Fixed host capacity.</summary>
internal sealed class FakeHostCapacity : IHostCapacityReader
{
    public HostCapacity Capacity { get; set; } = new(16, 32768, 20000);

    public HostCapacity Read() => Capacity;
}

/// <summary>Hyper-V defaults and switches that tests can change.</summary>
internal sealed class FakeHyperVHost : IHyperVHost
{
    public HyperVDefaults Defaults { get; set; } = new(@"C:\Hyper-V\Config", @"C:\Hyper-V\Virtual Hard Disks");

    public List<Shared.Contracts.Hosts.VirtualSwitch> Switches { get; } =
    [
        new(CimHyperVHost.DefaultSwitchId, "Default Switch", true),
        new("1d6e5b3c-0000-4000-8000-000000000001", "External", false),
    ];

    public Task<HyperVDefaults> GetDefaultsAsync(CancellationToken cancellationToken) => Task.FromResult(Defaults);

    public Task<IReadOnlyList<Shared.Contracts.Hosts.VirtualSwitch>> ListSwitchesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Shared.Contracts.Hosts.VirtualSwitch>>(Switches.ToList());
}

/// <summary>Records each creation step and can fail at any of them.</summary>
internal sealed class FakeHyperVBuilder : IHyperVBuilder
{
    public List<string> Steps { get; } = [];

    public VmBlueprint? Configured { get; private set; }

    public Guid CreatedVmId { get; } = Guid.Parse("5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716");

    /// <summary>The step name ("disk", "define", "configure", "tpm", "notes") that throws.</summary>
    public string? FailAt { get; set; }

    /// <summary>Called when the disk is created, for example to add it to a <see cref="FakeDiskFiles"/>.</summary>
    public Action<string>? OnDiskCreated { get; set; }

    public Task CreateDiskAsync(string path, long sizeBytes, Action<int> progress, CancellationToken cancellationToken)
    {
        Step("disk");
        progress(100);
        OnDiskCreated?.Invoke(path);
        return Task.CompletedTask;
    }

    public Task<Guid> DefineAsync(VmBlueprint blueprint, string notes, CancellationToken cancellationToken)
    {
        Step("define");
        return Task.FromResult(CreatedVmId);
    }

    public Task ConfigureAsync(Guid vmId, VmBlueprint blueprint, CancellationToken cancellationToken)
    {
        Step("configure");
        Configured = blueprint;
        return Task.CompletedTask;
    }

    public Task EnableTpmAsync(Guid vmId, CancellationToken cancellationToken)
    {
        Step("tpm");
        return Task.CompletedTask;
    }

    public Task SetNotesAsync(Guid vmId, string notes, CancellationToken cancellationToken)
    {
        Step("notes");
        return Task.CompletedTask;
    }

    private void Step(string name)
    {
        lock (Steps)
        {
            Steps.Add(name);
        }

        if (FailAt == name)
        {
            throw new Core.HyperV.HyperVJobFailedException(name, 32768, $"Simulated failure at {name}.");
        }
    }
}

/// <summary>Compute settings per VM, with the applied changes recorded.</summary>
internal sealed class FakeHyperVCompute : IHyperVCompute
{
    public Dictionary<Guid, ComputeState> States { get; } = [];

    public List<(Guid VmId, ComputeChange Change)> Applied { get; } = [];

    public Task<ComputeState> ReadAsync(Guid vmId, CancellationToken cancellationToken) =>
        Task.FromResult(States.TryGetValue(vmId, out var state) ? state : throw new Core.Power.VmNotFoundException(vmId));

    public Task ApplyAsync(Guid vmId, ComputeChange change, ComputeState desired, CancellationToken cancellationToken)
    {
        lock (Applied)
        {
            Applied.Add((vmId, change));
        }

        States[vmId] = desired;
        return Task.CompletedTask;
    }
}

/// <summary>Records disk copies and writes an empty file at each destination.</summary>
internal sealed class FakeDiskCopier : IDiskCopier
{
    /// <summary>File sizes in bytes; unknown files are 1 GB.</summary>
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Source, string Destination)> Copies { get; } = [];

    /// <summary>Fails the copy of this source, as a full volume would.</summary>
    public string? FailOn { get; set; }

    public long Length(string path) => Sizes.TryGetValue(path, out var size) ? size : 1L << 30;

    public Task CopyAsync(string source, string destination, Action<long> copied, CancellationToken cancellationToken)
    {
        if (string.Equals(source, FailOn, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("There is not enough space on the disk.");
        }

        File.WriteAllBytes(destination, []);
        lock (Copies)
        {
            Copies.Add((source, destination));
        }

        copied(Length(source));
        return Task.CompletedTask;
    }
}
