using System.Security.Cryptography;
using System.Text;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Profiles;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>The profiles the host ships for the Lean host action.</summary>
public static class HostLeanProfiles
{
    public const string LeanResource = "HyperHarbor.Profiles.host-gaming.yaml";

    public static string LeanYaml()
    {
        using var stream = typeof(HostLeanProfiles).Assembly.GetManifestResourceStream(LeanResource)
            ?? throw new InvalidOperationException($"{LeanResource} is missing from the host.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// The Lean host action: applies the shipped host profile to this PC, safely.
/// <list type="bullet">
/// <item>A dry run reads the host and lists what would change, and what a guard kept. Nothing is changed.</item>
/// <item>An apply is refused unless a dry run of the same profile was done in the last 24 hours and the host still gives its fingerprint.</item>
/// <item>The first apply makes a system restore point and exports the registry keys it touches; if either fails, nothing is changed.</item>
/// <item>Every handler changes only what differs, so applying again changes nothing. The diff becomes an undo profile (earliest values win across applies).</item>
/// <item>Idle memory and process count are recorded before and after.</item>
/// <item>Once applied, a monthly re-apply keeps the profile in place after Windows updates, as long as the shipped profile is the one a person approved.</item>
/// </list>
/// </summary>
public sealed class HostLeanService(
    IHostSystem system,
    ISteamLibrary steam,
    HostLeanStore store,
    SetupProfilePlanner planner,
    ProfileValidator validator,
    HostDiffer differ,
    IHostMetrics metrics,
    TimeProvider time,
    ILogger<HostLeanService> logger,
    IAuditLog? audit = null,
    TimeSpan? settleTime = null)
{
    public static readonly TimeSpan DryRunValidity = TimeSpan.FromHours(24);
    public static readonly TimeSpan ReapplyInterval = TimeSpan.FromDays(30);
    public const string RestorePointName = "HyperHarbor Lean host";

    private readonly TimeSpan _settle = settleTime ?? TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private string? _busy;

    public event EventHandler? Changed;

    public HostLeanStatus Status()
    {
        var state = store.Load();
        string? busy;
        lock (_gate)
        {
            busy = _busy;
        }

        var supported = system.CanModify;
        var dryRun = state.DryRun is { } stored && time.GetUtcNow() - stored.At <= DryRunValidity
            ? stored.Summary with { CanApply = supported && stored.Summary.Changes.Count > 0 }
            : null;
        return new HostLeanStatus(
            supported,
            supported ? null : "The Lean host action needs the HyperHarbor host to run as the installed service, or as an administrator.",
            busy,
            LeanProfileName,
            state.Baseline is not null,
            state.UndoAvailable,
            state.ScheduleEnabled,
            state.Baseline is not null && state.ScheduleEnabled && state.LastApplyAt is { } last ? last + ReapplyInterval : null,
            dryRun,
            state.LastRun);
    }

    private static readonly Lazy<string> ProfileDisplayName = new(() => ProfileYamlReader.Read(HostLeanProfiles.LeanYaml()).Name);

    private static string LeanProfileName => ProfileDisplayName.Value;

    public void SetSchedule(bool enabled)
    {
        store.Update(state => state with { ScheduleEnabled = enabled });
        Audit("hostLeanSchedule", enabled ? "Monthly re-apply turned on." : "Monthly re-apply turned off.", AuditOutcome.Succeeded);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the host and reports what the profile would change. Changes nothing.</summary>
    public async Task<HostLeanPlanSummary> DryRunAsync(string source, CancellationToken cancellationToken)
    {
        using var busy = Begin("dryRun");
        var profile = LoadProfile(source);
        HostLeanPlanSummary summary;
        try
        {
            var inspection = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
            summary = Summarize(source, inspection.Diff, canApply: system.CanModify);
            store.Update(state => state with
            {
                DryRun = new StoredDryRun(source, Hash(profile), inspection.Diff.Fingerprint(), summary.At, summary),
            });
        }
        catch (HostLeanException ex)
        {
            // Reading the PC failed (usually: not an administrator). The page shows why, in place of a list of changes.
            logger.LogWarning("The Lean host dry run could not read this PC: {Message}", ex.Message);
            summary = new HostLeanPlanSummary(source, time.GetUtcNow(), false, [], [], [Limit($"This PC could not be read: {ex.Message}")], 0);
            store.Update(state => state with { DryRun = new StoredDryRun(source, Hash(profile), "", summary.At, summary) });
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return summary;
    }

    /// <summary>
    /// Applies the profile. Failures that a person can act on (no recent dry run, no restore point, not an administrator)
    /// do not throw: they become the last run's problems, so the tray shows them.
    /// </summary>
    public async Task<HostLeanRunSummary> ApplyAsync(string source, CancellationToken cancellationToken, bool scheduled = false)
    {
        using var busy = Begin(source == HostLeanSources.Undo ? "undo" : "apply");
        HostLeanRunSummary summary;
        try
        {
            summary = await ApplyCoreAsync(source, scheduled, cancellationToken).ConfigureAwait(false);
        }
        catch (HostLeanException ex)
        {
            logger.LogWarning("The Lean host {Source} was not applied: {Message}", source, ex.Message);
            summary = new HostLeanRunSummary(source, time.GetUtcNow(), scheduled, 0, [Limit(ex.Message)], null, null, null);
            store.Update(state => state with { LastRun = summary, LastApplyAt = scheduled ? time.GetUtcNow() : state.LastApplyAt });
        }

        Audit(ActionName(source, scheduled),
            $"changed={summary.Changed}, problems={summary.Problems.Count}",
            summary.Problems.Count == 0 ? AuditOutcome.Succeeded : AuditOutcome.Failed);
        Changed?.Invoke(this, EventArgs.Empty);
        return summary;
    }

    /// <summary>Called by the scheduler: re-applies the Lean profile once a month, if a person approved this very profile.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var state = store.Load();
        if (!state.ScheduleEnabled || state.Baseline is null || state.LastApplyAt is not { } last || time.GetUtcNow() - last < ReapplyInterval || !system.CanModify)
        {
            return;
        }

        lock (_gate)
        {
            if (_busy is not null)
            {
                return;
            }
        }

        await ApplyAsync(HostLeanSources.Lean, cancellationToken, scheduled: true).ConfigureAwait(false);
    }

    private async Task<HostLeanRunSummary> ApplyCoreAsync(string source, bool scheduled, CancellationToken cancellationToken)
    {
        if (!system.CanModify)
        {
            throw new HostLeanUnsupportedException("The Lean host action needs the HyperHarbor host to run as the installed service, or as an administrator.");
        }

        var profile = LoadProfile(source);
        var hash = Hash(profile);
        var state = store.Load();
        var inspection = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        var diff = inspection.Diff;
        var fingerprint = diff.Fingerprint();

        if (scheduled)
        {
            if (source != HostLeanSources.Lean || state.Baseline is null || state.ApprovedProfileHash != hash)
            {
                throw new DryRunRequiredException("The monthly re-apply was skipped: this version of the Lean profile has not been reviewed and applied by a person yet. Run a dry run and apply it from the tray.");
            }
        }
        else if (!diff.IsEmpty)
        {
            var dry = state.DryRun;
            if (dry is null || dry.Source != source || dry.ProfileHash != hash || time.GetUtcNow() - dry.At > DryRunValidity)
            {
                throw new DryRunRequiredException("Run a dry run and review it before applying.");
            }

            if (dry.Fingerprint != fingerprint)
            {
                var fresh = Summarize(source, diff, canApply: true);
                store.Update(current => current with { DryRun = new StoredDryRun(source, hash, fingerprint, fresh.At, fresh) });
                throw new DryRunRequiredException("This PC changed since the dry run. Review the new dry run, then apply again.");
            }
        }

        if (!diff.IsEmpty)
        {
            // Like every state-changing request, nothing changes if the audit log cannot be written first.
            RecordRequested(ActionName(source, scheduled), $"changes={diff.ChangeCount}");
        }

        var before = await metrics.SampleAsync(cancellationToken).ConfigureAwait(false);
        HostLeanBaseline? baseline = null;
        string? restorePoint = null;
        if (state.Baseline is null && !diff.IsEmpty)
        {
            var folder = store.NewBackupFolder(time.GetUtcNow());
            try
            {
                restorePoint = await system.CreateRestorePointAsync(RestorePointName, cancellationToken).ConfigureAwait(false);
                var files = await system.ExportRegistryAsync(BackupKeys(diff, inspection.Snapshot), folder, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Lean host: made {RestorePoint} and exported {Count} registry keys to {Folder}.", restorePoint, files.Count, folder);
            }
            catch (HostLeanException ex)
            {
                throw new HostLeanException($"Nothing was changed, because the restore point and registry export could not be made first. {ex.Message}");
            }

            baseline = new HostLeanBaseline(time.GetUtcNow(), restorePoint, folder, before);
        }

        var results = diff.IsEmpty ? [] : await system.ApplyAsync(diff, cancellationToken).ConfigureAwait(false);
        var problems = diff.Problems.Select(Limit).ToList();
        problems.AddRange(results.Where(result => !result.Ok).Select(result => Limit($"{result.Item}: {result.Error ?? "failed"}")));

        // The undo profile: what this apply changed, merged with what earlier applies changed.
        var undoAvailable = state.UndoAvailable;
        if (source == HostLeanSources.Lean && !diff.IsEmpty)
        {
            var existing = store.ReadUndo() is { } yaml ? ProfileYamlReader.Read(yaml) : null;
            var merged = UndoProfileBuilder.Merge(existing, UndoProfileBuilder.FromDiff(diff));
            if (UndoProfileBuilder.IsEmpty(merged))
            {
                undoAvailable = false;
            }
            else
            {
                store.WriteUndo(ProfileYamlWriter.Write(validator.Normalize(merged)));
                undoAvailable = true;
            }
        }
        else if (source == HostLeanSources.Undo && problems.Count == 0)
        {
            store.DeleteUndo();
            undoAvailable = false;
        }

        if (_settle > TimeSpan.Zero)
        {
            await Task.Delay(_settle, time, cancellationToken).ConfigureAwait(false);
        }

        var after = await metrics.SampleAsync(cancellationToken).ConfigureAwait(false);

        // Idempotence check: a second look should find nothing left to change, other than what failed above.
        if (!diff.IsEmpty)
        {
            var verification = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!verification.Diff.IsEmpty && problems.Count == 0)
            {
                var lines = verification.Diff.Lines();
                problems.Add(Limit($"{lines.Count} change{(lines.Count == 1 ? "" : "s")} did not stay in place, for example {lines[0].Item}. Windows may be undoing them; they apply again on the next run."));
            }
        }

        var now = time.GetUtcNow();
        var changed = results.Count(result => result.Ok);
        var summary = new HostLeanRunSummary(source, now, scheduled, changed, problems, before, after, restorePoint);
        store.Update(current => current with
        {
            Baseline = current.Baseline ?? baseline,
            ScheduleEnabled = current.ScheduleEnabled || (source == HostLeanSources.Lean && current.Baseline is null && baseline is not null),
            LastApplyAt = source == HostLeanSources.Lean ? now : current.LastApplyAt,
            ApprovedProfileHash = source == HostLeanSources.Lean && !scheduled ? hash : current.ApprovedProfileHash,
            DryRun = null,
            LastRun = summary,
            UndoAvailable = undoAvailable,
        });
        return summary;
    }

    private SetupProfile LoadProfile(string source)
    {
        if (!HostLeanSources.IsKnown(source))
        {
            throw new HostLeanUnsupportedException($"Unknown Lean host profile \"{source}\".");
        }

        string yaml;
        if (source == HostLeanSources.Lean)
        {
            yaml = HostLeanProfiles.LeanYaml();
        }
        else
        {
            yaml = store.ReadUndo() ?? throw new HostLeanUnsupportedException("There is nothing to undo.");
        }

        var profile = ProfileYamlReader.Read(yaml);
        if (profile.Target != ProfileTarget.Host)
        {
            throw new HostLeanUnsupportedException("The Lean host action runs host profiles only.");
        }

        return validator.Normalize(profile);
    }

    private async Task<Inspection> InspectAsync(SetupProfile profile, CancellationToken cancellationToken)
    {
        var plan = planner.Plan(profile);
        var snapshot = await system.InspectAsync(HostDiffer.Probes(plan), cancellationToken).ConfigureAwait(false);
        return new Inspection(snapshot, differ.Diff(plan, snapshot, steam.InstalledGames()));
    }

    private HostLeanPlanSummary Summarize(string source, HostDiff diff, bool canApply) => new(
        source,
        time.GetUtcNow(),
        canApply && !diff.IsEmpty,
        diff.Lines(),
        diff.Kept,
        diff.Problems.Select(Limit).ToList(),
        diff.AlreadyInPlace);

    /// <summary>The registry keys the apply is about to change, to export first.</summary>
    internal static IReadOnlyList<string> BackupKeys(HostDiff diff, HostSnapshot snapshot)
    {
        var sids = snapshot.Registry.Values.SelectMany(hives => hives).Select(hive => hive.Hive)
            .Concat(snapshot.Startup.Select(entry => entry.Scope).Where(scope => scope.StartsWith("user:", StringComparison.Ordinal)).Select(scope => scope[5..]))
            .Where(hive => hive.StartsWith("S-1-5-21-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var keys = new List<string>();
        foreach (var change in diff.Registry)
        {
            if (change.Write.Target == RegistryTarget.Machine)
            {
                keys.Add($@"HKLM\{change.Write.Key}");
            }
            else
            {
                keys.AddRange(sids.Select(sid => $@"HKU\{sid}\{change.Write.Key}"));
            }
        }

        keys.AddRange(diff.Services.Select(change => $@"HKLM\SYSTEM\CurrentControlSet\Services\{change.Name}"));
        if (diff.Startup.Count > 0)
        {
            keys.Add(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved");
            keys.AddRange(sids.Select(sid => $@"HKU\{sid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved"));
        }

        if (diff.Power is not null)
        {
            keys.Add(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
        }

        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Hash(SetupProfile profile) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProfileYamlWriter.Write(profile))));

    private static string Limit(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private IDisposable Begin(string activity)
    {
        lock (_gate)
        {
            if (_busy is not null)
            {
                throw new HostLeanUnsupportedException("The Lean host action is already running.");
            }

            _busy = activity;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new Busy(this);
    }

    private static string ActionName(string source, bool scheduled) =>
        scheduled ? "hostLeanScheduled" : source == HostLeanSources.Undo ? "hostLeanUndo" : "hostLeanApply";

    private void RecordRequested(string action, string detail)
    {
        try
        {
            audit?.Write(new AuditEntry(time.GetUtcNow(), action, AuditOutcome.Requested, Detail: detail));
        }
        catch (AuditUnavailableException)
        {
            throw new HostLeanException("The audit log cannot be written, so nothing was changed.");
        }
    }

    private void Audit(string action, string detail, AuditOutcome outcome)
    {
        if (audit is null)
        {
            return;
        }

        try
        {
            audit.Write(new AuditEntry(time.GetUtcNow(), action, outcome, null, null, null, null, Detail: detail));
        }
        catch (AuditUnavailableException ex)
        {
            logger.LogError(ex, "Could not write the audit entry for {Action}.", action);
        }
    }

    private sealed record Inspection(HostSnapshot Snapshot, HostDiff Diff);

    private sealed class Busy(HostLeanService owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._busy = null;
            }

            owner.Changed?.Invoke(owner, EventArgs.Empty);
        }
    }
}
