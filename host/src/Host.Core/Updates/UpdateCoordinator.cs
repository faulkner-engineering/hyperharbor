using HyperHarbor.Host.Core.Installation;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Updates;

public enum UpdateActivity
{
    Idle,
    Checking,
    Preparing,

    /// <summary>A verified, self-tested version waits to be installed.</summary>
    Ready,

    /// <summary>Handed to the update helper, which restarts the service.</summary>
    Installing,
}

/// <summary>What the tray and the API show about updates.</summary>
public sealed record UpdateStatus(
    UpdateMode Mode,
    string Channel,
    string CurrentVersion,
    string? AvailableVersion,
    string? NotesUrl,
    UpdateActivity Activity,
    DateTimeOffset? LastCheck,
    string? Message,
    string? LastResult,
    IReadOnlyList<string> RolledBack);

/// <summary>
/// The service side of updates, ticked by the host every minute: checks the channel when due (or when asked),
/// prepares an available version (download, SHA-256, signatures, self-test), and hands it to the update helper
/// when <see cref="UpdateSchedule"/> allows or the owner asked to install now. Handing off closes the
/// <see cref="HostActivity"/> gate first, so nothing in progress is interrupted. The helper then owns
/// update\state.json until the update ends; if it never starts, the gate reopens after
/// <see cref="HandoffTimeout"/>.
/// </summary>
public sealed class UpdateCoordinator(
    UpdatePreparer preparer,
    UpdateStateStore store,
    HostActivity activity,
    Func<UpdateOptions> options,
    SemanticVersion current,
    Action startHelper,
    TimeProvider time,
    ILogger logger)
{
    public static readonly TimeSpan HandoffTimeout = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _requested = new(0);
    private Task? _pendingRequest;
    private DateTimeOffset? _lastCheck;
    private UpdateManifest? _manifest;
    private UpdateDecision? _decision;
    private PreparedUpdate? _prepared;
    private string? _preparationFailedFor;
    private UpdateActivity _activity;
    private string? _message;
    private bool _checkRequested;
    private bool _installRequested;
    private DateTimeOffset? _handedOffAt;

    public UpdateStatus Status
    {
        get
        {
            var settings = options();
            var state = store.Load();
            lock (_gate)
            {
                return new UpdateStatus(
                    settings.Mode,
                    settings.Channel,
                    current.ToString(),
                    _decision?.Kind is UpdateDecisionKind.Available or UpdateDecisionKind.NeedsIntermediateVersion or UpdateDecisionKind.Skipped ? _decision.Offered.ToString() : null,
                    _decision?.Kind == UpdateDecisionKind.Available ? _manifest?.NotesUrl : null,
                    // A requested check runs at the next tick; report it at once, so whoever asked sees it started.
                    _checkRequested && _activity is UpdateActivity.Idle or UpdateActivity.Ready ? UpdateActivity.Checking : _activity,
                    _lastCheck,
                    _message,
                    state.LastResult,
                    state.RolledBack);
            }
        }
    }

    /// <summary>Checks the channel at the next tick, which starts now.</summary>
    public void RequestCheck()
    {
        lock (_gate)
        {
            _checkRequested = true;
        }

        _requested.Release();
    }

    /// <summary>A version has been downloaded and tested, so <see cref="RequestInstall"/> would accept.</summary>
    public bool IsReadyToInstall
    {
        get
        {
            lock (_gate)
            {
                return _prepared is not null;
            }
        }
    }

    /// <summary>Installs the prepared version as soon as nothing is in progress (Install now, from the tray or a client).</summary>
    /// <returns>False when no version is ready.</returns>
    public bool RequestInstall()
    {
        lock (_gate)
        {
            if (_prepared is null)
            {
                return false;
            }

            _installRequested = true;
        }

        _requested.Release();
        return true;
    }

    /// <summary>
    /// Waits until <paramref name="interval"/> passes or a check or install is requested, whichever comes first. The
    /// wait for a request carries over to the next call when the interval wins: a fresh wait every minute would leave
    /// the old ones queued on the semaphore, and they would swallow later requests (Check now did nothing once the
    /// service had run for a while).
    /// </summary>
    public async Task WaitForNextTickAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        _pendingRequest ??= _requested.WaitAsync(cancellationToken);
        var finished = await Task.WhenAny(Task.Delay(interval, time, cancellationToken), _pendingRequest).ConfigureAwait(false);
        if (finished == _pendingRequest)
        {
            _pendingRequest = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var settings = options();
        var now = time.GetUtcNow();
        activity.Observe();

        var state = store.Load();
        if (state.Phase != UpdatePhase.Idle)
        {
            ReclaimAbandonedHandoff(state, now);
            return;
        }

        bool checkRequested;
        lock (_gate)
        {
            checkRequested = _checkRequested;
            _checkRequested = false;
        }

        if (checkRequested || (settings.Mode != UpdateMode.Off && UpdateSchedule.IsCheckDue(_lastCheck, TimeSpan.FromHours(settings.CheckIntervalHours), now)))
        {
            await CheckAsync(settings, state, cancellationToken).ConfigureAwait(false);
        }

        if (settings.Mode != UpdateMode.Off && _decision?.Kind == UpdateDecisionKind.Available && _prepared is null && _preparationFailedFor != _manifest!.Version)
        {
            await PrepareAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_prepared is not null && (_installRequested || UpdateSchedule.MayInstallAutomatically(
            settings.Mode,
            activity.InProgress,
            activity.IdleSince,
            now,
            TimeSpan.FromMinutes(settings.IdleMinutes),
            settings.ParsedMaintenanceTime,
            TimeSpan.FromMinutes(settings.MaintenanceWindowMinutes),
            TimeZoneInfo.Local)))
        {
            HandOff(state, now);
        }
    }

    private async Task CheckAsync(UpdateOptions settings, UpdateState state, CancellationToken cancellationToken)
    {
        Set(UpdateActivity.Checking, null);
        try
        {
            var rolledBack = state.RolledBack.Select(SemanticVersion.Parse).ToList();
            var (manifest, decision) = await preparer.CheckAsync(settings.Channel, current, rolledBack, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (manifest.Version != _manifest?.Version)
                {
                    _prepared = null;
                    _preparationFailedFor = null;
                }

                _manifest = manifest;
                _decision = decision;
            }

            Set(_prepared is null ? UpdateActivity.Idle : UpdateActivity.Ready, decision.Detail);
            logger.LogInformation("Update check on {Channel}: {Detail}", settings.Channel, decision.Detail);
        }
        catch (Exception ex) when (ex is UpdateRejectedException or HttpRequestException or TaskCanceledException or IOException)
        {
            Set(UpdateActivity.Idle, $"The update check failed: {ex.Message}");
            logger.LogWarning("The update check on {Channel} failed: {Message}", settings.Channel, ex.Message);
        }
        finally
        {
            _lastCheck = time.GetUtcNow();
        }
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        var manifest = _manifest!;
        Set(UpdateActivity.Preparing, $"Downloading and testing version {manifest.Version}.");
        try
        {
            var prepared = await preparer.PrepareAsync(manifest, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _prepared = prepared;
            }

            Set(UpdateActivity.Ready, $"Version {manifest.Version} is ready to install.");
            logger.LogInformation("Version {Version} is ready to install. {Signature}", manifest.Version, prepared.SignatureDetail);
        }
        catch (Exception ex) when (ex is UpdateRejectedException or HttpRequestException or TaskCanceledException or IOException)
        {
            _preparationFailedFor = manifest.Version;
            Set(UpdateActivity.Idle, ex.Message);
            logger.LogWarning("Version {Version} was not prepared: {Message}", manifest.Version, ex.Message);
        }
    }

    private void HandOff(UpdateState state, DateTimeOffset now)
    {
        if (!activity.TryClose())
        {
            Set(UpdateActivity.Ready, $"Version {_prepared!.Manifest.Version} installs once the work in progress finishes.");
            return;
        }

        var prepared = _prepared!;
        try
        {
            store.Save(new UpdateState
            {
                Phase = UpdatePhase.HandingOff,
                From = current.ToString(),
                To = prepared.Manifest.Version,
                StagedExecutable = prepared.ExecutablePath,
                RolledBack = state.RolledBack,
                UpdatedAt = now,
            });
            startHelper();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            store.Save(state);
            activity.Reopen();
            Set(UpdateActivity.Ready, $"The update helper could not be started: {ex.Message}");
            logger.LogError(ex, "Handing version {Version} to the update helper failed.", prepared.Manifest.Version);
            return;
        }

        lock (_gate)
        {
            _installRequested = false;
            _handedOffAt = now;
        }

        Set(UpdateActivity.Installing, $"Installing version {prepared.Manifest.Version}. The host restarts.");
        logger.LogInformation("Handed version {Version} to the update helper.", prepared.Manifest.Version);
    }

    /// <summary>The helper normally stops this service within moments; if it never ran, take the update back.</summary>
    private void ReclaimAbandonedHandoff(UpdateState state, DateTimeOffset now)
    {
        if (state.Phase != UpdatePhase.HandingOff || _handedOffAt is not { } handedOffAt || now - handedOffAt < HandoffTimeout)
        {
            return;
        }

        store.Save(state.Finished("The update helper did not start, so the update was not installed.", now));
        activity.Reopen();
        lock (_gate)
        {
            _handedOffAt = null;
        }

        Set(UpdateActivity.Ready, "The update helper did not start. Use Install now to try again.");
        logger.LogError("The update helper did not pick up version {Version} within {Timeout}.", state.To, HandoffTimeout);
    }

    private void Set(UpdateActivity activityNow, string? message)
    {
        lock (_gate)
        {
            _activity = activityNow;
            _message = message;
        }
    }
}
