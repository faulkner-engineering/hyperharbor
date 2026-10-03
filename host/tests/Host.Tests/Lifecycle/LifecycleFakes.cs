using HyperHarbor.Host.Core.Lifecycle;

namespace HyperHarbor.Host.Tests.Lifecycle;

/// <summary>In-memory Hyper-V storage: disk attachments, parent links, and recorded deletions.</summary>
internal sealed class FakeHyperVStorage : IHyperVStorage
{
    public List<DiskAttachment> Disks { get; } = [];

    public Dictionary<Guid, int> Checkpoints { get; } = [];

    /// <summary>Child path to parent path.</summary>
    public Dictionary<string, string> Parents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Guid> DeletedVms { get; } = [];

    public List<Guid> CheckpointsDeleted { get; } = [];

    public Exception? ThrowOnDeleteVm { get; set; }

    /// <summary>Runs before DeleteVmAsync completes, for example to change state mid-job.</summary>
    public Action? BeforeDeleteVm { get; set; }

    public Task<StorageSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new StorageSnapshot(Disks.ToList(), new Dictionary<Guid, int>(Checkpoints)));

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

    /// <summary>Files whose Delete throws, as if another process opened them after the check.</summary>
    public HashSet<string> FailOnDelete { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path) => Files.Contains(path);

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
