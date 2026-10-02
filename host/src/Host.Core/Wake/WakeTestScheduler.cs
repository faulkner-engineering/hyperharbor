using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Wake;

public sealed class WakeTestAlreadyScheduledException : Exception
{
    public WakeTestAlreadyScheduledException(DateTimeOffset sleepAt)
        : base($"A wake test is already scheduled for {sleepAt:u}.")
    {
        SleepAt = sleepAt;
    }

    public DateTimeOffset SleepAt { get; }
}

/// <summary>
/// Puts the host to sleep after a delay so a client can verify that it can wake it.
/// Only one test can be scheduled at a time.
/// </summary>
public sealed class WakeTestScheduler : IDisposable
{
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(300);

    private readonly ISleepController _sleep;
    private readonly TimeProvider _time;
    private readonly ILogger<WakeTestScheduler> _logger;
    private readonly object _gate = new();
    private ITimer? _timer;
    private DateTimeOffset? _sleepAt;

    public WakeTestScheduler(ISleepController sleep, TimeProvider time, ILogger<WakeTestScheduler> logger)
    {
        _sleep = sleep;
        _time = time;
        _logger = logger;
    }

    /// <returns>When the host will go to sleep.</returns>
    /// <exception cref="InvalidWakeRequestException">The delay is outside 5 to 300 seconds.</exception>
    /// <exception cref="WakeTestAlreadyScheduledException">A test is already scheduled.</exception>
    public DateTimeOffset Schedule(TimeSpan delay, string requestedBy)
    {
        if (delay < MinimumDelay || delay > MaximumDelay)
        {
            throw new InvalidWakeRequestException("The delay must be between 5 and 300 seconds.");
        }

        lock (_gate)
        {
            if (_sleepAt is { } pending)
            {
                throw new WakeTestAlreadyScheduledException(pending);
            }

            var sleepAt = _time.GetUtcNow() + delay;
            _sleepAt = sleepAt;
            _timer = _time.CreateTimer(_ => SleepNow(), null, delay, Timeout.InfiniteTimeSpan);
            _logger.LogInformation("Wake test requested by {Device}; sleeping at {SleepAt:u}.", requestedBy, sleepAt);
            return sleepAt;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private void SleepNow()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _sleepAt = null;
        }

        try
        {
            _logger.LogInformation("Entering sleep for a wake test.");
            _sleep.Sleep();
            _logger.LogInformation("Resumed from sleep.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not put the computer to sleep for a wake test.");
        }
    }
}
