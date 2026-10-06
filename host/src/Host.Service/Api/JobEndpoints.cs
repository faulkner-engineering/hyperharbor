using System.Security.Claims;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps the Jobs tag of docs/api.yaml.</summary>
public static class JobEndpoints
{
    public const string BasePath = ContractInfo.BasePath + "/jobs";

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(BasePath + "/{jobId:guid}", GetJob).WithName("getJob");
        return endpoints;
    }

    public static string Location(Guid jobId) => $"{BasePath}/{jobId}";

    public static VmJob ToContract(this VmJobSnapshot job) => new(
        job.Id,
        job.Kind,
        job.VmId,
        job.State,
        job.Step,
        job.PercentComplete,
        job.CreatedAt,
        job.UpdatedAt,
        job.ErrorTitle is { } title ? new JobError(title, job.ErrorDetail ?? string.Empty) : null,
        job.SetupResult);

    /// <summary>Another User's job is reported as not found.</summary>
    private static Results<Ok<VmJob>, ProblemHttpResult> GetJob(Guid jobId, VmJobStore jobs, ClaimsPrincipal user) =>
        jobs.Get(jobId, user.UserId()) is { } job
            ? TypedResults.Ok(job.ToContract())
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found", detail: $"Job {jobId} was not found. Finished jobs are kept for an hour.");
}
