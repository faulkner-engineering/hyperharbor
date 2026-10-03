using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Service.Audit;

public static class JobAudit
{
    /// <summary>
    /// Returns a callback that writes the outcome of a job started by this request, with the same
    /// user, device, VM, and elevation as the request's own entries. Call it from an audited endpoint.
    /// </summary>
    public static Action<VmJobSnapshot> CompletionAuditor(this HttpContext context)
    {
        var record = context.Audit() ?? throw new InvalidOperationException("Job auditing needs an audited endpoint.");
        var audit = context.RequestServices.GetRequiredService<IAuditLog>();
        var users = context.RequestServices.GetRequiredService<UserStore>();
        var time = context.RequestServices.GetRequiredService<TimeProvider>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AuditRecord>>();

        // Copy now: the request, and its record, are gone by the time the job ends.
        var action = record.Action;
        var (userId, deviceId, deviceName, vmName, elevated, detail) =
            (record.UserId, record.DeviceId, record.DeviceName, record.VmName, record.Elevated, record.Detail);
        var vmId = record.VmId;

        return job =>
        {
            var failed = job.State == VmJobState.Failed;
            try
            {
                audit.Write(new AuditEntry(
                    time.GetUtcNow(),
                    action,
                    failed ? AuditOutcome.Failed : AuditOutcome.Succeeded,
                    userId,
                    userId is { } id ? users.Find(id)?.Name : null,
                    deviceId,
                    deviceName,
                    vmId ?? job.VmId,
                    vmName,
                    elevated,
                    Detail: failed ? $"Job failed: {job.ErrorDetail}" : $"Job completed. {detail}".Trim(),
                    JobId: job.Id));
            }
            catch (AuditUnavailableException ex)
            {
                logger.LogError(ex, "Could not write the audit entry for the outcome of job {JobId}.", job.Id);
            }
        };
    }
}
