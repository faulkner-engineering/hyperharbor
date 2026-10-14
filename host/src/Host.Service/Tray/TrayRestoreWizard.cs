using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.Tray;

/// <summary>
/// The host window's Restore page for a new PC: it takes the backup location and recovery key, lists what the location
/// holds, and restores host settings or a VM. It runs under the local administrator the tray pipe admits, so it needs no
/// paired client; everything it does is also what the elevated API routes do. One restore at a time.
/// </summary>
public sealed class TrayRestoreWizard(
    BackupManagementService backups,
    RestoreCoordinator restore,
    HostStateRestorer hostRestore,
    VmStorageLocation storage,
    IHyperVHost hyperV,
    ILogger<TrayRestoreWizard> logger)
{
    private readonly object _gate = new();
    private RestoreWizardStatus _status = Idle();
    private BackupTarget? _target;

    /// <summary>Raised after every change of <see cref="Status"/>.</summary>
    public event EventHandler? Changed;

    public RestoreWizardStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Starts checking the key and listing the backups in the background.</summary>
    public void Attach(RestoreWizardForm form)
    {
        if (!Begin("attaching", "Checking the recovery key and opening the backups"))
        {
            return;
        }

        _ = Task.Run(() => AttachAsync(form));
    }

    /// <summary>Starts restoring one snapshot in the background.</summary>
    public void Restore(RestoreWizardRestoreMessage request)
    {
        BackupTarget target;
        lock (_gate)
        {
            if (_target is null || _status.Phase != "ready")
            {
                Set(_status with { Error = "Look for backups first." });
                return;
            }

            target = _target;
        }

        if (!Begin("restoring", "Starting"))
        {
            return;
        }

        _ = Task.Run(() => RestoreAsync(target, request));
    }

    /// <summary>Back to the first step, for another location. The attached target stays configured.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_status.Phase is "attaching" or "restoring")
            {
                return;
            }

            _target = null;
            _status = Idle();
        }

        Raise();
    }

    private static RestoreWizardStatus Idle() => new("idle", string.Empty, 0, null, null, [], [], [], false);

    private bool Begin(string phase, string step)
    {
        lock (_gate)
        {
            if (_status.Phase is "attaching" or "restoring")
            {
                return false;
            }

            _status = _status with { Phase = phase, Step = step, Percent = 0, Error = null, Messages = [] };
        }

        Raise();
        return true;
    }

    private void Set(RestoreWizardStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private async Task AttachAsync(RestoreWizardForm form)
    {
        try
        {
            var (draft, credentials) = ToTarget(form);
            var existing = backups.Load().Targets.FirstOrDefault(t => SameStorage(t, draft));
            var target = existing is { VmRepository.KeyState: RepositoryKeyState.Confirmed, HostRepository.KeyState: RepositoryKeyState.Confirmed }
                ? existing
                : await backups.AttachExistingAsync(draft, credentials, form.RecoveryKey, CancellationToken.None).ConfigureAwait(false);
            Set(_status with { Step = "Listing the backups" });
            var catalog = await backups.ScanAsync(target.Id, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _target = target;
            }

            Set(new RestoreWizardStatus(
                "ready", string.Empty, 0, null, target.Name,
                catalog.Vms.Select(vm => new RestoreWizardVm(vm.VmId, vm.Name, vm.Snapshots.Select(Map).ToList())).ToList(),
                catalog.HostSnapshots.Select(Map).ToList(),
                [], false));
        }
        catch (Exception ex) when (ex is BackupException or KopiaException or LifecycleValidationException or RestoreException or RecoveryKeyException)
        {
            Set(Idle() with { Error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Attaching a backup location from the tray failed.");
            Set(Idle() with { Error = "The backups could not be opened. The host log has the details." });
        }
    }

    private async Task RestoreAsync(BackupTarget target, RestoreWizardRestoreMessage request)
    {
        void Progress(string step, int percent) => Set(_status with { Step = step, Percent = percent });

        try
        {
            var messages = new List<string>();
            var restart = false;
            if (request.Kind == "host")
            {
                var result = await hostRestore.RestoreAsync(target, request.SnapshotId, Progress, CancellationToken.None).ConfigureAwait(false);
                restart = result.RestartRequired;
                messages.Add($"Host settings restored ({result.Restored.Count} items).");
                messages.AddRange(result.Warnings);
            }
            else
            {
                var destination = storage.RootFolder ?? (await hyperV.GetDefaultsAsync(CancellationToken.None).ConfigureAwait(false)).ConfigurationFolder;
                var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
                var result = await restore.RestoreAsync(
                    new RestoreVmRequest(target, request.SnapshotId, destination, name, request.Start), Progress, CancellationToken.None).ConfigureAwait(false);
                messages.Add(result.Outcome switch
                {
                    RestoreOutcome.NeedsConsole => $"{result.VmName} was restored and started, but has not responded. Open its console to see what it waits for.",
                    RestoreOutcome.SucceededWithWarnings => $"{result.VmName} was restored, with things to check.",
                    _ => $"{result.VmName} was restored.",
                });
                messages.AddRange(result.Warnings);
                if (result.NeedsDriverResync)
                {
                    messages.Add("It has a GPU partition. Run guest setup in Performance mode to copy the host's driver again.");
                }
            }

            Set(_status with { Phase = "ready", Step = string.Empty, Percent = 0, Error = null, Messages = messages, RestartRequired = _status.RestartRequired || restart });
        }
        catch (Exception ex) when (ex is BackupException or KopiaException or RestoreException)
        {
            Set(_status with { Phase = "ready", Step = string.Empty, Percent = 0, Error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restoring from the tray failed.");
            Set(_status with { Phase = "ready", Step = string.Empty, Percent = 0, Error = "The restore failed. The host log has the details." });
        }
    }

    private static (BackupTarget Draft, TargetCredentials Credentials) ToTarget(RestoreWizardForm form)
    {
        var kind = form.Kind switch
        {
            "s3" => BackupTargetKind.S3,
            "azure" => BackupTargetKind.Azure,
            _ => BackupTargetKind.Local,
        };
        var draft = new BackupTarget
        {
            Id = string.Empty,
            Name = string.IsNullOrWhiteSpace(form.Name) ? "Restored backups" : form.Name.Trim(),
            Kind = kind,
            Local = kind == BackupTargetKind.Local ? new LocalTargetSettings(form.Path?.Trim() ?? string.Empty) : null,
            S3 = kind == BackupTargetKind.S3
                ? new S3TargetSettings(form.Endpoint?.Trim() ?? string.Empty, form.Bucket?.Trim() ?? string.Empty, Blank(form.Region), Blank(form.Prefix), form.UseTls, string.Empty, string.Empty)
                : null,
            Azure = kind == BackupTargetKind.Azure
                ? new AzureTargetSettings(form.AzureAccount?.Trim() ?? string.Empty, form.AzureContainer?.Trim() ?? string.Empty, Blank(form.Prefix), string.Empty)
                : null,
        };
        return (draft, new TargetCredentials(form.AccessKey, form.SecretKey, form.AzureKey));
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>The same storage: the same folder, bucket and prefix, or container and prefix.</summary>
    internal static bool SameStorage(BackupTarget a, BackupTarget b) =>
        a.Kind == b.Kind
        && a.Kind switch
        {
            BackupTargetKind.Local => string.Equals(
                Path.TrimEndingDirectorySeparator(a.Local?.Path ?? string.Empty), Path.TrimEndingDirectorySeparator(b.Local?.Path ?? string.Empty), StringComparison.OrdinalIgnoreCase),
            BackupTargetKind.S3 => a.S3 is not null && b.S3 is not null
                && string.Equals(a.S3.Endpoint, b.S3.Endpoint, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.S3.Bucket, b.S3.Bucket, StringComparison.Ordinal)
                && string.Equals(a.S3.Prefix ?? string.Empty, b.S3.Prefix ?? string.Empty, StringComparison.Ordinal),
            _ => a.Azure is not null && b.Azure is not null
                && string.Equals(a.Azure.Account, b.Azure.Account, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Azure.Container, b.Azure.Container, StringComparison.Ordinal)
                && string.Equals(a.Azure.Prefix ?? string.Empty, b.Azure.Prefix ?? string.Empty, StringComparison.Ordinal),
        };

    private static RestoreWizardSnapshot Map(KopiaSnapshot snapshot) => new(snapshot.Id, snapshot.StartTime, snapshot.TotalSize);
}
