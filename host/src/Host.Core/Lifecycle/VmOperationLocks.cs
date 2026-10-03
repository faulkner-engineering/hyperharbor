namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>Thrown when another operation holds the virtual machine.</summary>
public sealed class VmBusyException : Exception
{
    public VmBusyException(Guid vmId, string holder)
        : base($"Another operation ({holder}) is in progress on this virtual machine. Try again when it finishes.")
    {
        VmId = vmId;
        Holder = holder;
    }

    public Guid VmId { get; }

    /// <summary>What holds the virtual machine, for example "deleteVm".</summary>
    public string Holder { get; }
}

/// <summary>
/// One operation at a time per virtual machine. Power actions, deletion, and settings changes take the
/// lock, so a VM cannot be started while it is being deleted. Callers do not wait: a held lock is a
/// conflict the client reports, because lifecycle jobs can run for minutes.
/// </summary>
public sealed class VmOperationLocks
{
    private readonly Dictionary<Guid, string> _held = [];
    private readonly object _gate = new();

    /// <summary>Takes the lock for <paramref name="vmId"/> until the returned handle is disposed.</summary>
    /// <param name="operation">Shown to other callers while the lock is held, for example "deleteVm".</param>
    /// <exception cref="VmBusyException">Another operation holds the lock.</exception>
    public IDisposable Acquire(Guid vmId, string operation)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(vmId, out var holder))
            {
                throw new VmBusyException(vmId, holder);
            }

            _held[vmId] = operation;
        }

        return new Handle(this, vmId);
    }

    /// <summary>The operation holding <paramref name="vmId"/>, or null.</summary>
    public string? HolderOf(Guid vmId)
    {
        lock (_gate)
        {
            return _held.GetValueOrDefault(vmId);
        }
    }

    private void Release(Guid vmId)
    {
        lock (_gate)
        {
            _held.Remove(vmId);
        }
    }

    private sealed class Handle(VmOperationLocks owner, Guid vmId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(vmId);
            }
        }
    }
}
