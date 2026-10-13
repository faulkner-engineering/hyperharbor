using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http.HttpResults;
using Wire = HyperHarbor.Shared.Contracts.Backup;
using Engine = HyperHarbor.Host.Core.Backup;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps the Backup tag of docs/api.yaml and POST /vms/{vmId}/replace-disk.</summary>
public static class BackupEndpoints
{
    public const string BasePath = ContractInfo.BasePath + "/backup";

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var backup = endpoints.MapGroup(BasePath);

        backup.MapGet("/targets", ListTargets).WithName("listBackupTargets");
        backup.MapPost("/targets", CreateTargetAsync).WithName("createBackupTarget")
            .Audited<Wire.NewBackupTarget>(request => $"name={request.Name}, kind={request.Kind}")
            .RequireElevation();
        backup.MapPost("/targets/attach", AttachTargetAsync).WithName("attachBackupTarget")
            .Audited<Wire.AttachBackupTargetRequest>(request => $"name={request.Target.Name}, kind={request.Target.Kind}")
            .RequireElevation();
        backup.MapPatch("/targets/{targetId}", UpdateTarget).WithName("updateBackupTarget")
            .Audited<Wire.UpdateBackupTargetRequest>(request => $"name={request.Name}, enabled={request.Enabled}, keysReplaced={request.Credentials is not null}")
            .RequireElevation();
        backup.MapDelete("/targets/{targetId}", DeleteTarget).WithName("deleteBackupTarget")
            .Audited()
            .RequireElevation();
        backup.MapPost("/targets/{targetId}/test", TestTargetAsync).WithName("testBackupTarget")
            .Audited();
        backup.MapPost("/targets/{targetId}/recovery-key", CreateRecoveryKey).WithName("createRecoveryKey")
            .Audited()
            .RequireElevation();
        backup.MapPost("/recovery-key/confirm", ConfirmRecoveryKey).WithName("confirmRecoveryKey")
            .Audited<Wire.ConfirmRecoveryKeyRequest>(_ => "recovery key confirmation")
            .RequireElevation();

        backup.MapGet("/policies", ListPolicies).WithName("listBackupPolicies");
        backup.MapPut("/policies", SetPolicy).WithName("setBackupPolicy")
            .Audited<Wire.BackupPolicy>(request => $"vm={request.VmId}, target={request.TargetId}, enabled={request.Enabled}");
        backup.MapDelete("/policies/{targetId}/{vmId:guid}", DeletePolicy).WithName("deleteBackupPolicy")
            .Audited();

        backup.MapGet("/status", GetStatusAsync).WithName("getBackupStatus");
        backup.MapGet("/history", ListHistory).WithName("listBackupHistory");
        backup.MapPost("/runs", StartRunAsync).WithName("startBackupRun")
            .Audited<Wire.StartBackupRunRequest>(request => $"target={request.TargetId}, vm={request.VmId?.ToString() ?? "host"}");

        backup.MapPost("/scan", ScanAsync).WithName("scanBackups")
            .Audited<Wire.ScanBackupsRequest>(request => $"target={request.TargetId}")
            .RequireElevation();
        backup.MapPost("/restore", RestoreVmAsync).WithName("restoreVmFromBackup")
            .Audited<Wire.RestoreVmRequest>(request => $"target={request.TargetId}, snapshot={request.SnapshotId}, name={request.Name}, start={request.Start}, administratorGiven={request.Administrator is not null}")
            .RequireElevation();
        backup.MapPost("/restore/host-state", RestoreHostStateAsync).WithName("restoreHostState")
            .Audited<Wire.RestoreHostStateRequest>(request => $"target={request.TargetId}, snapshot={request.SnapshotId}")
            .RequireElevation();

        endpoints.MapGroup(VmEndpoints.BasePath).MapPost("/{vmId:guid}/replace-disk", ReplaceDiskAsync).WithName("replaceVmDisk")
            .Audited<Wire.ReplaceDiskRequest>(request => $"target={request.TargetId}, snapshot={request.SnapshotId}")
            .RequireElevation();
        return endpoints;
    }

    private static Ok<List<Wire.BackupTarget>> ListTargets(BackupManagementService backups) =>
        TypedResults.Ok(backups.Load().Targets.Select(t => t.ToContract()).ToList());

    private static Created<Wire.BackupTarget> CreateTargetAsync(Wire.NewBackupTarget request, BackupManagementService backups)
    {
        var (draft, credentials) = request.ToCore();
        var created = backups.AddTarget(draft, credentials).ToContract();
        return TypedResults.Created($"{BasePath}/targets/{created.Id}", created);
    }

    private static async Task<Created<Wire.BackupTarget>> AttachTargetAsync(
        Wire.AttachBackupTargetRequest request, BackupManagementService backups, CancellationToken cancellationToken)
    {
        var (draft, credentials) = request.Target.ToCore();
        var created = (await backups.AttachExistingAsync(draft, credentials, request.RecoveryKey, cancellationToken)).ToContract();
        return TypedResults.Created($"{BasePath}/targets/{created.Id}", created);
    }

    private static Ok<Wire.BackupTarget> UpdateTarget(string targetId, Wire.UpdateBackupTargetRequest request, BackupManagementService backups) =>
        TypedResults.Ok(backups.UpdateTarget(targetId, request.ToCore()).ToContract());

    private static NoContent DeleteTarget(string targetId, BackupManagementService backups)
    {
        backups.DeleteTarget(targetId);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<Wire.BackupTargetTest>> TestTargetAsync(string targetId, BackupManagementService backups, CancellationToken cancellationToken) =>
        TypedResults.Ok((await backups.TestAsync(targetId, cancellationToken)).ToContract());

    private static Ok<Wire.RecoveryKeyOffer> CreateRecoveryKey(string targetId, BackupManagementService backups) =>
        TypedResults.Ok(backups.BeginKey(targetId).ToContract());

    private static Ok<Wire.BackupTarget> ConfirmRecoveryKey(Wire.ConfirmRecoveryKeyRequest request, BackupManagementService backups) =>
        TypedResults.Ok(backups.ConfirmKey(request.SetupId, request.TypedBack).ToContract());

    private static Ok<List<Wire.BackupPolicy>> ListPolicies(BackupManagementService backups) =>
        TypedResults.Ok(backups.Load().Policies.Select(p => p.ToContract()).ToList());

    private static Ok<Wire.BackupPolicy> SetPolicy(Wire.BackupPolicy request, BackupManagementService backups) =>
        TypedResults.Ok(backups.SetPolicy(request.ToCore(backups.Find(request.TargetId))).ToContract());

    private static NoContent DeletePolicy(string targetId, Guid vmId, BackupManagementService backups)
    {
        backups.RemovePolicy(vmId, targetId);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<Wire.BackupStatus>> GetStatusAsync(
        BackupManagementService backups,
        BackupHistoryStore history,
        BackupCoordinator coordinator,
        IVmInventory inventory,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var names = (await inventory.ListAsync(cancellationToken)).ToDictionary(vm => vm.Id, vm => vm.Name);
        var items = BackupStatusBuilder.Build(backups.Load(), history, names, time.GetUtcNow());
        return TypedResults.Ok(new Wire.BackupStatus(items.Select(i => i.ToContract()).ToList(), coordinator.Busy, coordinator.WaitingCount));
    }

    private static Ok<List<Wire.BackupRun>> ListHistory(BackupHistoryStore history, int? limit) =>
        TypedResults.Ok(history.List(Math.Clamp(limit ?? 100, 1, BackupHistoryStore.MaxRecords)).Select(r => r.ToContract()).ToList());

    private static async Task<Accepted<VmJob>> StartRunAsync(
        Wire.StartBackupRunRequest request,
        BackupCoordinator coordinator,
        IVmInventory inventory,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (request.VmId is { } vmId && await inventory.GetAsync(vmId, cancellationToken) is null)
        {
            throw new VmNotFoundException(vmId);
        }

        var job = coordinator.StartManual(request.TargetId, request.VmId, context.User.UserId(), context.CompletionAuditor());
        context.Audit()!.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    private static async Task<Ok<Wire.BackupCatalog>> ScanAsync(Wire.ScanBackupsRequest request, BackupManagementService backups, CancellationToken cancellationToken) =>
        TypedResults.Ok((await backups.ScanAsync(request.TargetId, cancellationToken)).ToContract());

    private static async Task<Accepted<VmJob>> RestoreVmAsync(
        Wire.RestoreVmRequest request,
        BackupManagementService backups,
        RestoreCoordinator restore,
        VmJobStore jobs,
        VmStorageLocation storage,
        IHyperVHost host,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var target = backups.Find(request.TargetId);
        var destination = !string.IsNullOrWhiteSpace(request.Destination)
            ? request.Destination
            : storage.RootFolder ?? (await host.GetDefaultsAsync(cancellationToken)).ConfigurationFolder;
        if (!Path.IsPathFullyQualified(destination))
        {
            throw new LifecycleValidationException("The destination must be a full folder path.", [new ValidationIssue("destination", "The destination must be a full folder path.")]);
        }

        var work = new Engine.Restore.RestoreVmRequest(target, request.SnapshotId, destination, request.Name, request.Start, request.Administrator);
        var job = jobs.Start(
            VmJobKind.RestoreVm,
            null,
            context.User.UserId(),
            "Starting the restore",
            async run => run.ReportRestoreResult((await restore.RestoreAsync(work, run.Report, run.Stopping)).ToContract()),
            context.CompletionAuditor());
        context.Audit()!.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    private static Accepted<VmJob> RestoreHostStateAsync(
        Wire.RestoreHostStateRequest request,
        BackupManagementService backups,
        HostStateRestorer restorer,
        VmJobStore jobs,
        HttpContext context)
    {
        var target = backups.Find(request.TargetId);
        var job = jobs.Start(
            VmJobKind.RestoreHostState,
            null,
            context.User.UserId(),
            "Starting the restore",
            async run => run.ReportRestoreResult((await restorer.RestoreAsync(target, request.SnapshotId, run.Report, run.Stopping)).ToContract()),
            context.CompletionAuditor());
        context.Audit()!.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    private static Accepted<VmJob> ReplaceDiskAsync(
        Guid vmId,
        Wire.ReplaceDiskRequest request,
        BackupManagementService backups,
        DiskReplacer replacer,
        VmJobStore jobs,
        HttpContext context)
    {
        var target = backups.Find(request.TargetId);
        var work = new Engine.Restore.ReplaceDiskRequest(vmId, request.TypedName, target, request.SnapshotId);
        var job = jobs.Start(
            VmJobKind.ReplaceDisk,
            vmId,
            context.User.UserId(),
            "Starting the disk replacement",
            run => replacer.ReplaceAsync(work, run.Report, run.Stopping),
            context.CompletionAuditor());
        context.Audit()!.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }
}
