using System.Globalization;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts;
using Wire = HyperHarbor.Shared.Contracts.Backup;
using Engine = HyperHarbor.Host.Core.Backup;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Converts between the wire types of docs/api.yaml and the backup types of Host.Core.</summary>
public static class BackupMapping
{
    private const string TimeFormat = "HH:mm";

    public static Wire.BackupTarget ToContract(this Engine.BackupTarget target) => new(
        target.Id,
        target.Name,
        target.Kind.ToContract(),
        target.Enabled,
        target.VmRepository?.KeyState == RepositoryKeyState.Confirmed && target.HostRepository?.KeyState == RepositoryKeyState.Confirmed
            ? Wire.BackupKeyState.Confirmed
            : Wire.BackupKeyState.PendingKey,
        target.Schedule.ToContract(),
        target.DefaultRetention.ToContract(),
        target.DefaultConditions.ToContract(),
        target.Local is { } local ? new Wire.BackupLocalSettings(local.Path) : null,
        target.S3 is { } s3 ? new Wire.BackupS3Settings(s3.Endpoint, s3.Bucket, s3.Region, s3.Prefix, s3.UseTls) : null,
        target.Azure is { } azure ? new Wire.BackupAzureSettings(azure.Account, azure.Container, azure.Prefix) : null,
        target.DefaultBandwidthKbps);

    /// <summary>A new target's settings and keys. The id, key references, and repositories are assigned by the service.</summary>
    /// <exception cref="LifecycleValidationException">A time is not HH:mm, or the settings of the kind are missing.</exception>
    public static (Engine.BackupTarget Draft, TargetCredentials Credentials) ToCore(this Wire.NewBackupTarget request)
    {
        var draft = new Engine.BackupTarget
        {
            Id = string.Empty,
            Name = request.Name,
            Kind = request.Kind.ToCore(),
            Local = request.Local is { } local ? new LocalTargetSettings(local.Path) : null,
            S3 = request.S3 is { } s3 ? new S3TargetSettings(s3.Endpoint, s3.Bucket, s3.Region, s3.Prefix, s3.UseTls, string.Empty, string.Empty) : null,
            Azure = request.Azure is { } azure ? new AzureTargetSettings(azure.Account, azure.Container, azure.Prefix, string.Empty) : null,
            Schedule = request.Schedule is { } schedule ? schedule.ToCore("schedule") : ScheduleSpec.Nightly,
            DefaultRetention = request.Retention?.ToCore() ?? RetentionPolicy.Default,
            DefaultBandwidthKbps = request.BandwidthKbps,
            DefaultConditions = request.Conditions?.ToCore() ?? RunConditions.Default,
        };
        return (draft, request.Credentials.ToCore());
    }

    public static TargetChanges ToCore(this Wire.UpdateBackupTargetRequest request) => new(
        request.Name,
        request.Enabled,
        request.Schedule?.ToCore("schedule"),
        request.Retention?.ToCore(),
        request.BandwidthKbps,
        request.ClearBandwidth,
        request.Conditions?.ToCore(),
        request.Credentials is null ? null : request.Credentials.ToCore());

    public static TargetCredentials ToCore(this Wire.BackupCredentials? credentials) =>
        credentials is null ? new TargetCredentials() : new TargetCredentials(credentials.S3AccessKey, credentials.S3SecretKey, credentials.AzureKey);

    public static Wire.BackupTargetTest ToContract(this TargetTestResult result) =>
        new(result.Passed, result.Steps.Select(step => new Wire.BackupTestStep(step.Name, step.Passed, step.Detail)).ToList());

    public static Wire.RecoveryKeyOffer ToContract(this Engine.RecoveryKeyOffer offer) =>
        new(offer.SetupId, offer.RecoveryKey, offer.ExpiresAt);

    public static Wire.BackupPolicy ToContract(this Engine.BackupPolicy policy) => new(
        Guid.Parse(policy.VmId),
        policy.TargetId,
        policy.Enabled,
        policy.Retention.ToContract(),
        policy.BandwidthKbps,
        policy.Conditions.ToContract(),
        new Wire.BackupTriggers(policy.Triggers.Schedule, policy.Triggers.OnShutdown, policy.Triggers.DebounceMinutes),
        policy.GpuLiveCopy == GpuLiveCopy.AllowLive ? Wire.BackupGpuMode.AllowLive : Wire.BackupGpuMode.RequireOff);

    /// <summary>Settings left out of the request take the target's defaults.</summary>
    public static Engine.BackupPolicy ToCore(this Wire.BackupPolicy policy, Engine.BackupTarget target) => new()
    {
        VmId = policy.VmId.ToString("D"),
        TargetId = policy.TargetId,
        Enabled = policy.Enabled,
        Retention = policy.Retention?.ToCore() ?? target.DefaultRetention,
        BandwidthKbps = policy.BandwidthKbps ?? target.DefaultBandwidthKbps,
        Conditions = policy.Conditions?.ToCore() ?? target.DefaultConditions,
        Triggers = policy.Triggers is { } triggers ? new Engine.BackupTriggers(triggers.Schedule, triggers.OnShutdown, triggers.DebounceMinutes) : new Engine.BackupTriggers(),
        GpuLiveCopy = policy.GpuMode == Wire.BackupGpuMode.AllowLive ? GpuLiveCopy.AllowLive : GpuLiveCopy.RequireOff,
    };

    public static Wire.BackupRun ToContract(this BackupRunRecord run) => new(
        run.RunId,
        run.Item,
        run.TargetId,
        run.Trigger switch
        {
            BackupTrigger.Shutdown => Wire.BackupRunTrigger.Shutdown,
            BackupTrigger.Manual => Wire.BackupRunTrigger.Manual,
            _ => Wire.BackupRunTrigger.Schedule,
        },
        run.StartedAt,
        run.FinishedAt,
        run.Outcome switch
        {
            BackupOutcome.Failed => Wire.BackupRunOutcome.Failed,
            BackupOutcome.Skipped => Wire.BackupRunOutcome.Skipped,
            BackupOutcome.Partial => Wire.BackupRunOutcome.Partial,
            _ => Wire.BackupRunOutcome.Succeeded,
        },
        run.TotalBytes,
        run.Warnings,
        run.VmId,
        run.VmName,
        run.Reason,
        run.SnapshotId);

    public static Wire.BackupItemStatus ToContract(this Engine.BackupItemStatus item) => new(
        item.Item, item.TargetId, item.TargetName, item.Overdue, item.VmId, item.VmName, item.LastRun?.ToContract(), item.LastSuccessAt);

    public static Wire.BackupCatalog ToContract(this Engine.BackupCatalog catalog) => new(
        catalog.Vms.Select(vm => new Wire.BackupCatalogVm(vm.VmId, vm.Snapshots.Select(ToContract).ToList(), vm.Name)).ToList(),
        catalog.HostSnapshots.Select(ToContract).ToList());

    public static Wire.RestoreResult ToContract(this RestoreVmResult result) => new(
        result.Outcome switch
        {
            RestoreOutcome.NeedsConsole => Wire.RestoreVmOutcome.NeedsConsole,
            RestoreOutcome.SucceededWithWarnings => Wire.RestoreVmOutcome.SucceededWithWarnings,
            _ => Wire.RestoreVmOutcome.Succeeded,
        },
        result.Warnings,
        result.NeedsDriverResync,
        result.BitLockerRecoveryMayBeNeeded,
        RestartRequired: false,
        result.VmId,
        result.VmName);

    public static Wire.RestoreResult ToContract(this HostStateRestoreResult result) => new(
        result.Warnings.Count > 0 ? Wire.RestoreVmOutcome.SucceededWithWarnings : Wire.RestoreVmOutcome.Succeeded,
        result.Warnings,
        NeedsDriverResync: false,
        BitLockerRecoveryMayBeNeeded: false,
        result.RestartRequired);

    public static Wire.BackupKind ToContract(this BackupTargetKind kind) => kind switch
    {
        BackupTargetKind.S3 => Wire.BackupKind.S3,
        BackupTargetKind.Azure => Wire.BackupKind.Azure,
        _ => Wire.BackupKind.Local,
    };

    public static BackupTargetKind ToCore(this Wire.BackupKind kind) => kind switch
    {
        Wire.BackupKind.S3 => BackupTargetKind.S3,
        Wire.BackupKind.Azure => BackupTargetKind.Azure,
        _ => BackupTargetKind.Local,
    };

    private static Wire.BackupSnapshot ToContract(Engine.Kopia.KopiaSnapshot snapshot) => new(snapshot.Id, snapshot.StartTime, snapshot.TotalSize);

    private static Wire.BackupSchedule ToContract(this ScheduleSpec schedule) =>
        new(schedule.Time.ToString(TimeFormat, CultureInfo.InvariantCulture), schedule.Days);

    private static Wire.BackupRetention ToContract(this RetentionPolicy retention) => new(retention.Daily, retention.Weekly, retention.Monthly);

    private static Wire.BackupConditions ToContract(this RunConditions conditions) => new(
        conditions.NoActiveSession,
        conditions.NotInGame,
        conditions.Window is { } window
            ? new Wire.BackupTimeWindow(window.Start.ToString(TimeFormat, CultureInfo.InvariantCulture), window.End.ToString(TimeFormat, CultureInfo.InvariantCulture))
            : null);

    private static ScheduleSpec ToCore(this Wire.BackupSchedule schedule, string field) =>
        new(ParseTime(schedule.Time, field), schedule.Days is { Count: > 0 } days ? days.Distinct().Order().ToList() : null);

    private static RetentionPolicy ToCore(this Wire.BackupRetention retention) => new(retention.Daily, retention.Weekly, retention.Monthly);

    private static RunConditions ToCore(this Wire.BackupConditions conditions) => new(
        conditions.NoActiveSession,
        conditions.NotInGame,
        conditions.Window is { } window ? new TimeWindow(ParseTime(window.Start, "conditions.window.start"), ParseTime(window.End, "conditions.window.end")) : null);

    public static Wire.RestoreTestSettings ToContract(this RestoreTestSettings settings) => new(
        settings.Enabled,
        settings.IntervalDays,
        new Wire.BackupTimeWindow(settings.Window.Start.ToString(TimeFormat, CultureInfo.InvariantCulture), settings.Window.End.ToString(TimeFormat, CultureInfo.InvariantCulture)));

    /// <exception cref="LifecycleValidationException">A time is not HH:mm, or the interval is out of range.</exception>
    public static RestoreTestSettings ToCore(this Wire.RestoreTestSettings settings)
    {
        var core = new RestoreTestSettings
        {
            Enabled = settings.Enabled,
            IntervalDays = settings.IntervalDays,
            Window = new TimeWindow(ParseTime(settings.Window.Start, "window.start"), ParseTime(settings.Window.End, "window.end")),
        };
        var problems = core.Validate();
        if (problems.Count > 0)
        {
            throw new LifecycleValidationException(problems[0], problems.Select(p => new ValidationIssue("intervalDays", p)).ToList());
        }

        return core;
    }

    public static Wire.RestoreTestResult ToContract(this RestoreTestRecord record) => new(
        record.RunId,
        record.VmName,
        record.TargetId,
        record.Trigger switch
        {
            BackupTrigger.Shutdown => Wire.BackupRunTrigger.Shutdown,
            BackupTrigger.Manual => Wire.BackupRunTrigger.Manual,
            _ => Wire.BackupRunTrigger.Schedule,
        },
        record.StartedAt,
        record.FinishedAt,
        record.Outcome switch
        {
            RestoreTestOutcome.Failed => Wire.RestoreTestOutcome.Failed,
            RestoreTestOutcome.Skipped => Wire.RestoreTestOutcome.Skipped,
            _ => Wire.RestoreTestOutcome.Passed,
        },
        record.Warnings,
        record.VmId,
        record.SnapshotId,
        record.Reason);

    public static Wire.BackupBrowseSession ToContract(this BrowseSessionInfo session) => new(
        session.Id,
        session.State switch
        {
            BrowseState.Ready => Wire.BackupBrowseState.Ready,
            BrowseState.Failed => Wire.BackupBrowseState.Failed,
            _ => Wire.BackupBrowseState.Preparing,
        },
        session.Step,
        session.Volumes.Select(v => new Wire.BackupBrowseVolume(v.Drive, v.SizeBytes, v.Label)).ToList(),
        session.ExpiresAt,
        session.VmName,
        session.Error);

    public static Wire.BackupBrowseEntry ToContract(this BrowseEntry entry) => new(entry.Name, entry.IsDirectory, entry.Size, entry.Modified);

    private static TimeOnly ParseTime(string text, string field) =>
        TimeOnly.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new LifecycleValidationException(
                $"The time {text} is not valid. Use HH:mm, for example 02:00.",
                [new ValidationIssue(field, $"The time {text} is not valid. Use HH:mm, for example 02:00.")]);
}
