using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.GameAware;

/// <summary>
/// Receives the tray's foreground signals (about every two seconds), decides whether a game is in the foreground, smooths
/// the answer, and hands the result to <see cref="GameAwareController"/> and to the consumers that only need to know that a
/// game runs (the scheduled sleep and the backup conditions). The combination logic lives here, on the service, so the
/// allowlist and ignore list are read where they are saved.
/// </summary>
public sealed class GameAwareMonitor(
    GameAwareStore settings,
    GameAwareController controller,
    Action<bool> gameActive,
    TimeProvider time)
{
    private readonly object _gate = new();
    private readonly GamingDebouncer _debouncer = new();
    private GamingVerdict? _verdict;
    private string? _gamingProcess;

    /// <summary>The last decision, for the host window ("why is it gaming?").</summary>
    public GamingVerdict? LastVerdict
    {
        get
        {
            lock (_gate)
            {
                return _verdict;
            }
        }
    }

    public async Task OnSignalsAsync(ForegroundSignals signals, CancellationToken cancellationToken)
    {
        var current = settings.Load();
        var verdict = GamingDetector.Evaluate(signals, current.Allowlist, current.IgnoreList, current.ExtraLaunchers);
        bool gaming;
        string? process;
        lock (_gate)
        {
            _verdict = verdict;
            gaming = _debouncer.Observe(verdict.Gaming, time.GetUtcNow());
            if (verdict.Gaming)
            {
                _gamingProcess = verdict.Process ?? _gamingProcess;
            }

            process = _gamingProcess;
            if (!gaming)
            {
                _gamingProcess = null;
            }
        }

        gameActive(gaming);
        await controller.ReportAsync(new GameStateReport(gaming, gaming ? process : null, verdict.Reason), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// "This was not a game": puts the program that started the current game state on the ignore list and ends the game
    /// state at once, which resumes the VMs.
    /// </summary>
    /// <returns>The program added to the ignore list, or null when no game state was active.</returns>
    public async Task<string?> NotAGameAsync(CancellationToken cancellationToken)
    {
        string? process;
        lock (_gate)
        {
            process = _gamingProcess;
            _debouncer.Reset();
            _gamingProcess = null;
        }

        if (process is null)
        {
            return null;
        }

        settings.Update(s => s with
        {
            IgnoreList = [.. s.IgnoreList, process],
            Allowlist = s.Allowlist.Where(name => name != process).ToList(),
        });
        gameActive(false);
        await controller.ReportAsync(new GameStateReport(false, null, "The user said it was not a game."), cancellationToken).ConfigureAwait(false);
        return process;
    }
}
