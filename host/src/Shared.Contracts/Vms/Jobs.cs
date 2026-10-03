using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Long-running VM operation. Schema: VmJobKind.</summary>
public enum VmJobKind
{
    CreateVm,
    DeleteVm,
    ApplyCompute,
}

/// <summary>Schema: VmJobState.</summary>
public enum VmJobState
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>Why a job failed. Schema: JobError.</summary>
public sealed record JobError(string Title, string Detail);

/// <summary>A long-running VM operation. Poll GET /jobs/{jobId} until it is no longer running. Schema: VmJob.</summary>
/// <param name="VmId">Null until a create job has defined its virtual machine.</param>
/// <param name="Step">What the job is doing now, for example "Deleting disks".</param>
/// <param name="Error">Set when <paramref name="State"/> is failed.</param>
public sealed record VmJob(
    Guid Id,
    VmJobKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? VmId,
    VmJobState State,
    string Step,
    int PercentComplete,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JobError? Error);
