using System.Globalization;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Installation;

/// <summary>
/// The update helper's state machine (hh-update.exe update-run, as SYSTEM). It saves each phase before doing it,
/// so <see cref="RunAsync"/> after a crash or power loss picks up where it stopped:
/// <code>
/// HandingOff -> Stopping -> BackingUp -> Flipping -> Starting(1) -> Starting(2) -> idle, "updated"
///                  |            |           |            \ both failed
///                  +------------+-----------+--> abandon: current back on From, service started, data untouched
///                                                       RollingBack -> idle, "rolled back" (To is skipped from now on)
///                                                                   \-> Failed (From did not start either)
/// </code>
/// Until the new version first starts, the data is untouched, so an interruption only puts the junction back.
/// Once it has started it may have migrated the data, so failing from then on restores the backup taken
/// before the flip (logs and the audit trail are kept, see <see cref="DataBackup"/>).
/// </summary>
public sealed class UpdateApplier(
    InstallLayout layout,
    string dataDirectory,
    UpdateStateStore store,
    IServiceControl service,
    IHealthProbe health,
    TimeProvider time,
    ILogger logger)
{
    public const int StartAttempts = 2;
    public const int BackupsKept = 2;
    public static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UnlockTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan HealthTimeout { get; init; } = DefaultHealthTimeout;

    /// <summary>Does whatever the saved state calls for: apply a handed-off update, or finish an interrupted one.</summary>
    public async Task<UpdateState> RunAsync(CancellationToken cancellationToken)
    {
        var state = store.Load();
        switch (state.Phase)
        {
            case UpdatePhase.HandingOff:
                return await ApplyAsync(state, cancellationToken).ConfigureAwait(false);
            case UpdatePhase.Stopping or UpdatePhase.BackingUp or UpdatePhase.Flipping:
                logger.LogWarning("An update to {To} was interrupted while {Phase}; keeping {From}.", state.To, state.Phase, state.From);
                return await AbandonAsync(state, $"The update to {state.To} was interrupted before it started, so {state.From} was kept.", cancellationToken).ConfigureAwait(false);
            case UpdatePhase.Starting:
                // The helper stopped while the new version was starting. If it came up (the service starts with
                // Windows), the update stands; otherwise roll back.
                logger.LogWarning("An update to {To} was interrupted while starting; checking it before rolling back.", state.To);
                var to = SemanticVersion.Parse(state.To!);
                return await StartHealthyAsync(to, state.UpdatedAt ?? time.GetUtcNow(), cancellationToken).ConfigureAwait(false)
                    ? Commit(state, SemanticVersion.Parse(state.From!), to)
                    : await RollBackAsync(state, cancellationToken).ConfigureAwait(false);
            case UpdatePhase.RollingBack:
                logger.LogWarning("A rollback from {To} to {From} was interrupted; finishing it.", state.To, state.From);
                return await RollBackAsync(state, cancellationToken).ConfigureAwait(false);
            default:
                return state;
        }
    }

    private async Task<UpdateState> ApplyAsync(UpdateState state, CancellationToken cancellationToken)
    {
        var from = SemanticVersion.Parse(state.From!);
        var to = SemanticVersion.Parse(state.To!);
        logger.LogInformation("Updating HyperHarbor from {From} to {To}.", from, to);
        try
        {
            state = Save(state with { Phase = UpdatePhase.Stopping });
            await service.StopAsync(cancellationToken).ConfigureAwait(false);

            var backup = Path.Combine(DataBackup.BackupsFolder(dataDirectory), $"{from}-to-{to}-{time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}");
            state = Save(state with { Phase = UpdatePhase.BackingUp, Backup = backup });
            DataBackup.Copy(dataDirectory, backup);

            state = Save(state with { Phase = UpdatePhase.Flipping });
            layout.Stage(state.StagedExecutable!, to, UnlockTimeout);
            layout.Activate(to);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The update to {To} failed before it started; keeping {From}.", to, from);
            return await AbandonAsync(state, $"The update to {to} failed before it started, so {from} was kept: {ex.Message}", cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 1; attempt <= StartAttempts; attempt++)
        {
            state = Save(state with { Phase = UpdatePhase.Starting, Attempt = attempt });
            if (await StartHealthyAsync(to, cancellationToken).ConfigureAwait(false))
            {
                return Commit(state, from, to);
            }

            logger.LogWarning("Version {To} did not become healthy (attempt {Attempt} of {Attempts}).", to, attempt, StartAttempts);
        }

        return await RollBackAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private UpdateState Commit(UpdateState state, SemanticVersion from, SemanticVersion to)
    {
        // Housekeeping only: a failure here does not undo a healthy update.
        TryAll(
            () => layout.InstallHelper(layout.CurrentExecutable),
            () => layout.PruneExcept(from, to),
            () => DataBackup.PruneBackups(dataDirectory, BackupsKept),
            () => File.Delete(state.StagedExecutable!));
        logger.LogInformation("Updated HyperHarbor from {From} to {To}.", from, to);
        return Save(state.Finished($"Updated from {from} to {to}.", time.GetUtcNow()));
    }

    private async Task<UpdateState> RollBackAsync(UpdateState state, CancellationToken cancellationToken)
    {
        var from = SemanticVersion.Parse(state.From!);
        var to = state.To!;
        state = Save(state with { Phase = UpdatePhase.RollingBack });
        try
        {
            await StopQuietlyAsync(cancellationToken).ConfigureAwait(false);
            if (state.Backup is { } backup && Directory.Exists(backup))
            {
                DataBackup.Restore(backup, dataDirectory);
            }

            layout.Activate(from);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Rolling back from {To} to {From} failed.", to, from);
            return Save(state with { Phase = UpdatePhase.Failed, LastResult = $"Version {to} did not start, and rolling back to {from} failed: {ex.Message}", UpdatedAt = time.GetUtcNow() });
        }

        var skipped = state.RolledBack.Contains(to) ? state.RolledBack : [.. state.RolledBack, to];
        if (await StartHealthyAsync(from, cancellationToken).ConfigureAwait(false))
        {
            logger.LogWarning("Rolled back from {To} to {From}; {To} will not be offered again.", to, from, to);
            TryAll(
                () => File.Delete(state.StagedExecutable!),
                () => Directory.Delete(layout.VersionFolder(SemanticVersion.Parse(to)), recursive: true));
            return Save((state with { RolledBack = skipped }).Finished($"Version {to} did not start, so {from} was restored.", time.GetUtcNow()));
        }

        logger.LogCritical("Version {From} did not start after the rollback from {To}.", from, to);
        return Save(state with
        {
            Phase = UpdatePhase.Failed,
            RolledBack = skipped,
            LastResult = $"Version {to} did not start, and {from} did not start after the rollback either. Reinstall HyperHarbor.",
            UpdatedAt = time.GetUtcNow(),
        });
    }

    /// <summary>The new version never ran: put current back on From if it moved, remove a partial backup, start From.</summary>
    private async Task<UpdateState> AbandonAsync(UpdateState state, string result, CancellationToken cancellationToken)
    {
        var from = SemanticVersion.Parse(state.From!);
        TryAll(
            () =>
            {
                if (layout.CurrentVersion != from)
                {
                    layout.Activate(from);
                }
            },
            () =>
            {
                if (state.Backup is { } backup)
                {
                    DataBackup.Delete(backup);
                }
            });

        if (!await StartHealthyAsync(from, cancellationToken).ConfigureAwait(false))
        {
            logger.LogCritical("Version {From} did not start after an abandoned update.", from);
            return Save(state with { Phase = UpdatePhase.Failed, LastResult = result + $" {from} did not start afterwards. Reinstall HyperHarbor.", UpdatedAt = time.GetUtcNow() });
        }

        return Save(state.Finished(result, time.GetUtcNow()));
    }

    private Task<bool> StartHealthyAsync(SemanticVersion version, CancellationToken cancellationToken) =>
        StartHealthyAsync(version, time.GetUtcNow(), cancellationToken);

    /// <summary>Starts the service (a running one is left running) and waits for a health report newer than <paramref name="since"/>.</summary>
    private async Task<bool> StartHealthyAsync(SemanticVersion version, DateTimeOffset since, CancellationToken cancellationToken)
    {
        try
        {
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Starting version {Version} failed: {Message}", version, ex.Message);
            return false;
        }

        if (await health.WaitHealthyAsync(version, since, HealthTimeout, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        await StopQuietlyAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task StopQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await service.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Stopping the service failed: {Message}", ex.Message);
        }
    }

    private UpdateState Save(UpdateState state)
    {
        state = state with { UpdatedAt = time.GetUtcNow() };
        store.Save(state);
        return state;
    }

    private void TryAll(params Action[] actions)
    {
        foreach (var action in actions)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "An update clean-up step failed.");
            }
        }
    }
}
