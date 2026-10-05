namespace HyperHarbor.Host.Core.Updates;

/// <summary>
/// What an update restart would interrupt: state-changing requests in flight (provisioning, rotations,
/// uploads, exports, console tunnels), plus the work <paramref name="otherWorkInProgress"/> reports (running jobs,
/// a pairing waiting for its PIN). Reads do not count, so a client polling the host does not block updates.
/// Closing the gate for an update refuses new state-changing requests; it closes only when nothing is in
/// progress, so nothing is cut off.
/// </summary>
public sealed class HostActivity(Func<bool> otherWorkInProgress, TimeProvider time)
{
    private readonly object _gate = new();
    private int _requests;
    private bool _closed;
    private DateTimeOffset _idleSince = time.GetUtcNow();

    public bool InProgress
    {
        get
        {
            lock (_gate)
            {
                return _requests > 0 || otherWorkInProgress();
            }
        }
    }

    /// <summary>Since when nothing has been in progress, as far as requests ending and <see cref="Observe"/> tell.</summary>
    public DateTimeOffset IdleSince
    {
        get
        {
            lock (_gate)
            {
                return _idleSince;
            }
        }
    }

    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <summary>Counts a state-changing request until the result is disposed; null when the gate is closed.</summary>
    public IDisposable? TryBegin()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return null;
            }

            _requests++;
            return new Request(this);
        }
    }

    /// <summary>Moves the idle start forward while jobs or pairing (which do not end through this class) are in progress.</summary>
    public void Observe()
    {
        lock (_gate)
        {
            if (_requests > 0 || otherWorkInProgress())
            {
                _idleSince = time.GetUtcNow();
            }
        }
    }

    /// <summary>Closes the gate when nothing is in progress.</summary>
    /// <returns>False, leaving it open, when something is in progress.</returns>
    public bool TryClose()
    {
        lock (_gate)
        {
            if (_requests > 0 || otherWorkInProgress())
            {
                return false;
            }

            _closed = true;
            return true;
        }
    }

    public void Reopen()
    {
        lock (_gate)
        {
            _closed = false;
        }
    }

    private void End()
    {
        lock (_gate)
        {
            _requests--;
            _idleSince = time.GetUtcNow();
        }
    }

    private sealed class Request(HostActivity activity) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                activity.End();
            }
        }
    }
}
