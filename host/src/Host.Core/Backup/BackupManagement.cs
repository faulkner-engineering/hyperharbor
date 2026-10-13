using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>The keys a target needs, supplied once when it is created or changed. They go straight into the DPAPI vault.</summary>
public sealed record TargetCredentials(string? S3AccessKey = null, string? S3SecretKey = null, string? AzureKey = null)
{
    public override string ToString() => "TargetCredentials { ***** }";
}

/// <summary>What the user changes on a target after it exists. Null leaves a value as it is.</summary>
public sealed record TargetChanges(
    string? Name = null,
    bool? Enabled = null,
    ScheduleSpec? Schedule = null,
    RetentionPolicy? Retention = null,
    int? BandwidthKbps = null,
    bool ClearBandwidth = false,
    RunConditions? Conditions = null,
    TargetCredentials? Credentials = null);

/// <summary>A VM found in a repository: its newest snapshots and the name it had when last backed up.</summary>
public sealed record CatalogVm(Guid VmId, string? Name, IReadOnlyList<KopiaSnapshot> Snapshots);

public sealed record BackupCatalog(IReadOnlyList<CatalogVm> Vms, IReadOnlyList<KopiaSnapshot> HostSnapshots);

/// <summary>
/// Creates, changes, and removes backup targets and policies, and walks a user through a target's recovery key. One
/// recovery key protects both of a target's repositories (VMs and host state), so the user keeps one string per target.
/// </summary>
public sealed class BackupManagementService(
    BackupConfigStore config,
    IBackupSecretStore secrets,
    RecoveryKeyService keys,
    BackupTargetTester tester,
    IBackupRepositories repositories,
    KopiaCli kopia,
    string dataDirectory)
{
    public BackupConfig Load() => config.Load();

    public BackupTarget Find(string targetId) =>
        config.FindTarget(targetId) ?? throw new BackupTargetNotFoundException();

    public BackupTarget AddTarget(BackupTarget draft, TargetCredentials credentials)
    {
        var id = Guid.NewGuid().ToString("N");
        var target = Normalize(draft with { Id = id }, id);
        Validate(target, credentials, requireCredentials: true);
        StoreCredentials(target, credentials);
        config.Update(c => c with { Targets = [.. c.Targets, target] });
        return target;
    }

    public BackupTarget UpdateTarget(string targetId, TargetChanges changes)
    {
        var current = Find(targetId);
        var next = current with
        {
            Name = changes.Name ?? current.Name,
            Enabled = changes.Enabled ?? current.Enabled,
            Schedule = changes.Schedule ?? current.Schedule,
            DefaultRetention = changes.Retention ?? current.DefaultRetention,
            DefaultBandwidthKbps = changes.ClearBandwidth ? null : changes.BandwidthKbps ?? current.DefaultBandwidthKbps,
            DefaultConditions = changes.Conditions ?? current.DefaultConditions,
        };
        Validate(next, changes.Credentials ?? new TargetCredentials(), requireCredentials: false);
        if (changes.Credentials is { } credentials)
        {
            StoreCredentials(next, credentials);
        }

        config.Update(c => c with { Targets = c.Targets.Select(t => t.Id == targetId ? next : t).ToList() });
        return next;
    }

    /// <summary>
    /// Removes the target, its policies, and its keys. Backups already in the storage stay there and can be reached again by
    /// adding the target with its recovery key.
    /// </summary>
    public void DeleteTarget(string targetId)
    {
        var target = Find(targetId);
        config.Update(c => c with
        {
            Targets = c.Targets.Where(t => t.Id != targetId).ToList(),
            Policies = c.Policies.Where(p => p.TargetId != targetId).ToList(),
        });
        foreach (var reference in References(target))
        {
            secrets.Remove(reference);
        }

        foreach (var repo in new[] { target.VmRepository, target.HostRepository }.OfType<RepositoryRef>())
        {
            secrets.Remove(RecoveryKeyService.PassphraseReference(repo.RepoId));
            TryDelete(Path.Combine(dataDirectory, "backup", repo.RepoId));
        }
    }

    public Task<TargetTestResult> TestAsync(string targetId, CancellationToken cancellationToken) =>
        tester.TestAsync(Find(targetId), cancellationToken);

    /// <summary>Generates the target's recovery key. The caller shows it once; nothing is stored until it is confirmed.</summary>
    public RecoveryKeyOffer BeginKey(string targetId)
    {
        var target = Find(targetId);
        if (target.VmRepository is not { KeyState: RepositoryKeyState.PendingKey } repository)
        {
            throw new LifecycleConflictException("This target already has a confirmed recovery key.", "recoveryKeyConfirmed");
        }

        return keys.Begin(repository.RepoId);
    }

    /// <summary>Stores the passphrase once the user typed the key's last groups back, and makes both repositories usable.</summary>
    /// <exception cref="RecoveryKeyException">The entry did not match, or the setup expired.</exception>
    public BackupTarget ConfirmKey(string setupId, string typedBack)
    {
        var repoId = keys.Confirm(setupId, typedBack);
        var passphrase = secrets.Get(RecoveryKeyService.PassphraseReference(repoId))!;
        var target = config.Load().Targets.FirstOrDefault(t => t.VmRepository?.RepoId == repoId)
            ?? throw new RecoveryKeyException("The target was removed while its key was being set up.");
        secrets.Set(RecoveryKeyService.PassphraseReference(target.HostRepository!.RepoId), passphrase);
        var confirmed = target with
        {
            VmRepository = target.VmRepository! with { KeyState = RepositoryKeyState.Confirmed },
            HostRepository = target.HostRepository with { KeyState = RepositoryKeyState.Confirmed },
        };
        config.Update(c => c with { Targets = c.Targets.Select(t => t.Id == target.Id ? confirmed : t).ToList() });
        return confirmed;
    }

    public void CancelKey(string setupId) => keys.Cancel(setupId);

    /// <summary>
    /// Adds a target that already holds backups (a new PC, or a target that was removed) using the recovery key the user
    /// saved. The key is checked against the storage before anything is kept.
    /// </summary>
    public async Task<BackupTarget> AttachExistingAsync(
        BackupTarget draft, TargetCredentials credentials, string recoveryKey, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var target = Normalize(draft with { Id = id }, id);
        Validate(target, credentials, requireCredentials: true);
        var passphrase = RecoveryKeyService.Canonical(recoveryKey);
        if (passphrase.Length == 0)
        {
            throw new LifecycleValidationException("Enter the recovery key.", [new ValidationIssue("recoveryKey", "Enter the recovery key.")]);
        }

        StoreCredentials(target, credentials);
        try
        {
            await VerifyAsync(target, passphrase, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var reference in References(target))
            {
                secrets.Remove(reference);
            }

            throw;
        }

        secrets.Set(RecoveryKeyService.PassphraseReference(target.VmRepository!.RepoId), passphrase);
        secrets.Set(RecoveryKeyService.PassphraseReference(target.HostRepository!.RepoId), passphrase);
        var confirmed = target with
        {
            VmRepository = target.VmRepository with { KeyState = RepositoryKeyState.Confirmed },
            HostRepository = target.HostRepository with { KeyState = RepositoryKeyState.Confirmed },
        };
        config.Update(c => c with { Targets = [.. c.Targets, confirmed] });
        return confirmed;
    }

    public BackupPolicy SetPolicy(BackupPolicy policy)
    {
        Find(policy.TargetId);
        if (!Guid.TryParse(policy.VmId, out var vmId))
        {
            throw new LifecycleValidationException("The VM id is not valid.", [new ValidationIssue("vmId", "The VM id is not valid.")]);
        }

        if (policy.Retention.Daily + policy.Retention.Weekly + policy.Retention.Monthly == 0
            || policy.Retention.Daily < 0 || policy.Retention.Weekly < 0 || policy.Retention.Monthly < 0)
        {
            throw new LifecycleValidationException("Retention needs at least one snapshot to keep.", [new ValidationIssue("retention", "Retention needs at least one snapshot to keep.")]);
        }

        if (policy.BandwidthKbps is <= 0 || policy.Triggers.DebounceMinutes is < 1 or > 1440)
        {
            throw new LifecycleValidationException("The bandwidth limit must be positive and the shutdown delay between 1 minute and 24 hours.");
        }

        var normalized = policy with { VmId = vmId.ToString("D") };
        config.Update(c => c with
        {
            Policies = [.. c.Policies.Where(p => !(p.TargetId == policy.TargetId && p.VmId.Equals(normalized.VmId, StringComparison.OrdinalIgnoreCase))), normalized],
        });
        return normalized;
    }

    public void RemovePolicy(Guid vmId, string targetId) =>
        config.Update(c => c with
        {
            Policies = c.Policies.Where(p => !(p.TargetId == targetId && Guid.TryParse(p.VmId, out var id) && id == vmId)).ToList(),
        });

    /// <summary>Lists what a target's repositories hold, for the guided restore.</summary>
    public async Task<BackupCatalog> ScanAsync(string targetId, CancellationToken cancellationToken)
    {
        var target = Find(targetId);
        var vms = new List<CatalogVm>();
        if (target.VmRepository?.KeyState == RepositoryKeyState.Confirmed)
        {
            var repository = await repositories.OpenAsync(target, RepositoryRole.Vm, cancellationToken).ConfigureAwait(false);
            var snapshots = await kopia.ListSnapshotsAsync(repository.Repository, repository.Passphrase, cancellationToken).ConfigureAwait(false);
            vms.AddRange(SnapshotCatalog.GroupByVm(snapshots).Select(set => new CatalogVm(set.VmId, NameOf(set.Snapshots), set.Snapshots)));
        }

        IReadOnlyList<KopiaSnapshot> host = [];
        if (target.HostRepository?.KeyState == RepositoryKeyState.Confirmed)
        {
            var repository = await repositories.OpenAsync(target, RepositoryRole.Host, cancellationToken).ConfigureAwait(false);
            host = (await kopia.ListSnapshotsAsync(repository.Repository, repository.Passphrase, cancellationToken).ConfigureAwait(false))
                .OrderByDescending(s => s.StartTime).ToList();
        }

        return new BackupCatalog(vms, host);
    }

    /// <summary>The VM name in the newest snapshot's description ("hyperharbor vm=&lt;id&gt; name=&lt;name&gt; run=&lt;id&gt;").</summary>
    public static string? NameOf(IReadOnlyList<KopiaSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var description = snapshot.Description;
            var start = description?.IndexOf(" name=", StringComparison.Ordinal) ?? -1;
            var end = description?.LastIndexOf(" run=", StringComparison.Ordinal) ?? -1;
            if (description is not null && start >= 0 && end > start + 6)
            {
                return description[(start + 6)..end];
            }
        }

        return null;
    }

    private async Task VerifyAsync(BackupTarget target, string passphrase, CancellationToken cancellationToken)
    {
        KopiaException? last = null;
        foreach (var role in new[] { RepositoryRole.Vm, RepositoryRole.Host })
        {
            var probe = KopiaBackendFactory.For(target, role, $"verify-{Guid.NewGuid():N}", dataDirectory, secrets);
            try
            {
                await kopia.ConnectAsync(probe, passphrase, readOnly: true, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (KopiaException ex) when (ex.Failure == KopiaFailure.NotARepository)
            {
                last = ex;
            }
            catch (KopiaException ex) when (ex.Failure == KopiaFailure.BadPassphrase)
            {
                throw new RecoveryKeyException("The recovery key does not open the backups at this location. Check it against your saved copy.");
            }
            catch (KopiaException ex)
            {
                throw new BackupException(
                    ex.Failure switch
                    {
                        KopiaFailure.AccessDenied => "The storage refused the keys. Check the access key, secret key, and permissions.",
                        KopiaFailure.Unreachable => "The storage could not be reached. Check the endpoint and your network.",
                        KopiaFailure.TimedOut => "The storage did not answer in time.",
                        _ => ex.Message,
                    },
                    ex);
            }
            finally
            {
                TryDelete(probe.StateFolder);
            }
        }

        throw new BackupException("No HyperHarbor backups were found at this location.", last) { Code = "noBackupsFound" };
    }

    private static BackupTarget Normalize(BackupTarget draft, string id) =>
        draft with
        {
            Id = id,
            Name = draft.Name?.Trim() ?? string.Empty,
            S3 = draft.S3 is null ? null : draft.S3 with { AccessKeyRef = $"target:{id}:s3-access", SecretKeyRef = $"target:{id}:s3-secret" },
            Azure = draft.Azure is null ? null : draft.Azure with { KeyRef = $"target:{id}:azure-key" },
            VmRepository = new RepositoryRef(Guid.NewGuid().ToString("N"), RepositoryKeyState.PendingKey),
            HostRepository = new RepositoryRef(Guid.NewGuid().ToString("N"), RepositoryKeyState.PendingKey),
        };

    private void Validate(BackupTarget target, TargetCredentials credentials, bool requireCredentials)
    {
        var problems = target.Validate().Select(text => new ValidationIssue("target", text)).ToList();
        var others = config.Load().Targets.Where(t => t.Id != target.Id);
        if (others.Any(t => string.Equals(t.Name, target.Name, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(new ValidationIssue("name", "Another target already has this name."));
        }

        if (requireCredentials)
        {
            if (target.Kind == BackupTargetKind.S3 && (string.IsNullOrEmpty(credentials.S3AccessKey) || string.IsNullOrEmpty(credentials.S3SecretKey)))
            {
                problems.Add(new ValidationIssue("credentials", "An S3 target needs an access key and a secret key."));
            }

            if (target.Kind == BackupTargetKind.Azure && string.IsNullOrEmpty(credentials.AzureKey))
            {
                problems.Add(new ValidationIssue("credentials", "An Azure target needs the storage account key."));
            }
        }

        if (problems.Count > 0)
        {
            throw new LifecycleValidationException(problems[0].Message, problems);
        }
    }

    private void StoreCredentials(BackupTarget target, TargetCredentials credentials)
    {
        if (target.S3 is { } s3)
        {
            if (!string.IsNullOrEmpty(credentials.S3AccessKey))
            {
                secrets.Set(s3.AccessKeyRef, credentials.S3AccessKey);
            }

            if (!string.IsNullOrEmpty(credentials.S3SecretKey))
            {
                secrets.Set(s3.SecretKeyRef, credentials.S3SecretKey);
            }
        }

        if (target.Azure is { } azure && !string.IsNullOrEmpty(credentials.AzureKey))
        {
            secrets.Set(azure.KeyRef, credentials.AzureKey);
        }
    }

    private static IEnumerable<string> References(BackupTarget target)
    {
        if (target.S3 is { } s3)
        {
            yield return s3.AccessKeyRef;
            yield return s3.SecretKeyRef;
        }

        if (target.Azure is { } azure)
        {
            yield return azure.KeyRef;
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>One thing that is backed up to one target, and how its last run went.</summary>
public sealed record BackupItemStatus(
    string Item,
    Guid? VmId,
    string? VmName,
    string TargetId,
    string TargetName,
    BackupRunRecord? LastRun,
    DateTimeOffset? LastSuccessAt,
    bool Overdue);

/// <summary>Builds the status view from configuration and history. Pure.</summary>
public static class BackupStatusBuilder
{
    /// <summary>A backup is overdue when its last success is older than twice the longest gap in the target's schedule.</summary>
    public static TimeSpan OverdueAfter(ScheduleSpec schedule)
    {
        var days = schedule.Days is { Count: > 0 } list ? list.Select(d => (int)d).Distinct().Order().ToList() : [0, 1, 2, 3, 4, 5, 6];
        var longest = 1;
        for (var i = 0; i < days.Count; i++)
        {
            var next = days[(i + 1) % days.Count];
            var gap = (next - days[i] + 7) % 7;
            longest = Math.Max(longest, gap == 0 ? 7 : gap);
        }

        return TimeSpan.FromDays(longest * 2);
    }

    public static IReadOnlyList<BackupItemStatus> Build(
        BackupConfig config, BackupHistoryStore history, IReadOnlyDictionary<Guid, string> vmNames, DateTimeOffset now)
    {
        var result = new List<BackupItemStatus>();
        foreach (var target in config.Targets.Where(t => t.Enabled && t.VmRepository?.KeyState == RepositoryKeyState.Confirmed))
        {
            result.Add(Entry(BackupCoordinator.HostItem, null, null, target, history, now));
            foreach (var policy in config.Policies.Where(p => p.Enabled && p.TargetId == target.Id))
            {
                if (!Guid.TryParse(policy.VmId, out var vmId))
                {
                    continue;
                }

                result.Add(Entry(BackupCoordinator.VmItem(vmId), vmId, vmNames.GetValueOrDefault(vmId), target, history, now));
            }
        }

        return result;
    }

    private static BackupItemStatus Entry(string item, Guid? vmId, string? vmName, BackupTarget target, BackupHistoryStore history, DateTimeOffset now)
    {
        var last = history.Last(item, target.Id);
        var success = history.LastSuccess(item, target.Id)?.FinishedAt;
        var overdue = success is { } at && now - at > OverdueAfter(target.Schedule);
        return new BackupItemStatus(item, vmId, vmName ?? last?.VmName, target.Id, target.Name, last, success, overdue);
    }
}
