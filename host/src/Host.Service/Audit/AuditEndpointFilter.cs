using System.Security.Claims;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Service.Api;
using HyperHarbor.Host.Service.Security;

namespace HyperHarbor.Host.Service.Audit;

/// <summary>Marks an endpoint as audited. EndpointSecurityTests require it on every route that changes state.</summary>
public sealed class AuditedMetadata;

/// <summary>
/// What the audit filter knows about the current request. Handlers add what only they know, such as
/// the device created by pairing or a summary of the parameters, through <see cref="AuditHttpContextExtensions.Audit"/>.
/// </summary>
public sealed class AuditRecord
{
    public required string Action { get; init; }

    public Guid? UserId { get; set; }

    public Guid? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    public Guid? VmId { get; set; }

    public string? VmName { get; set; }

    public bool Elevated { get; set; }

    /// <summary>A summary of the parameters. Must never contain a secret.</summary>
    public string? Detail { get; set; }

    public Guid? JobId { get; set; }
}

public static class AuditHttpContextExtensions
{
    /// <summary>The audit record of an audited request, or null on other endpoints.</summary>
    public static AuditRecord? Audit(this HttpContext context) => context.Features.Get<AuditRecord>();

    /// <summary>
    /// Writes a "requested" audit entry before the handler runs and a "succeeded" or "failed" entry
    /// after it. If the first entry cannot be written, the handler does not run. Call before
    /// RequireElevation, so requests refused for missing elevation are audited too.
    /// </summary>
    public static RouteHandlerBuilder Audited(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new AuditedMetadata()).AddEndpointFilter<AuditEndpointFilter>();

    /// <summary>
    /// Like <see cref="Audited(RouteHandlerBuilder)"/>, and records a summary of the bound
    /// <typeparamref name="TRequest"/> argument in every entry. The summary must never contain a secret.
    /// </summary>
    public static RouteHandlerBuilder Audited<TRequest>(this RouteHandlerBuilder builder, Func<TRequest, string> describe) =>
        builder.WithMetadata(new AuditedMetadata(), new AuditDescriber(arguments =>
            arguments.OfType<TRequest>().FirstOrDefault() is { } request ? describe(request) : null))
        .AddEndpointFilter<AuditEndpointFilter>();
}

/// <summary>Builds an entry's detail from the handler's bound arguments.</summary>
internal sealed record AuditDescriber(Func<IList<object?>, string?> Describe);

internal sealed class AuditEndpointFilter : IEndpointFilter
{
    private readonly IAuditLog _audit;
    private readonly IVmInventory _inventory;
    private readonly UserStore _users;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditEndpointFilter> _logger;

    public AuditEndpointFilter(IAuditLog audit, IVmInventory inventory, UserStore users, TimeProvider time, ILogger<AuditEndpointFilter> logger)
    {
        _audit = audit;
        _inventory = inventory;
        _users = users;
        _time = time;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var metadata = http.GetEndpoint()?.Metadata;
        var record = new AuditRecord
        {
            Action = metadata?.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? http.Request.Path,
            Detail = metadata?.GetMetadata<AuditDescriber>()?.Describe(context.Arguments),
        };
        Identify(record, http.User);
        if (http.Request.RouteValues.TryGetValue("vmId", out var routeVmId) && Guid.TryParse(routeVmId?.ToString(), out var vmId))
        {
            record.VmId = vmId;
            record.VmName = await FindVmNameAsync(vmId, http.RequestAborted);
        }

        http.Features.Set(record);

        // Fail closed: an operation that cannot be audited does not run.
        _audit.Write(Entry(record, AuditOutcome.Requested, status: null, detail: record.Detail));

        object? result;
        try
        {
            result = await next(context);
        }
        catch (Exception ex)
        {
            var (status, _) = ApiExceptionHandler.Classify(ex);
            WriteCompletion(Entry(record, AuditOutcome.Failed, status == 0 ? StatusCodes.Status500InternalServerError : status, ex.Message));
            throw;
        }

        var resultStatus = (result as IStatusCodeHttpResult)?.StatusCode ?? http.Response.StatusCode;
        var outcome = resultStatus < StatusCodes.Status400BadRequest ? AuditOutcome.Succeeded : AuditOutcome.Failed;
        WriteCompletion(Entry(record, outcome, resultStatus, record.Detail));
        return result;
    }

    private void Identify(AuditRecord record, ClaimsPrincipal user)
    {
        if (Guid.TryParse(user.FindFirst(PairedDeviceAuthenticationHandler.DeviceIdClaim)?.Value, out var deviceId))
        {
            record.DeviceId = deviceId;
            record.DeviceName = user.Identity?.Name;
        }

        if (Guid.TryParse(user.FindFirst(PairedDeviceAuthenticationHandler.UserIdClaim)?.Value, out var userId))
        {
            record.UserId = userId;
        }
    }

    private async Task<string?> FindVmNameAsync(Guid vmId, CancellationToken cancellationToken)
    {
        try
        {
            return await _inventory.FindNameAsync(vmId, cancellationToken);
        }
        catch (HyperVUnavailableException)
        {
            // The operation itself will fail with the same error; the entry still records the ID.
            return null;
        }
    }

    private AuditEntry Entry(AuditRecord record, AuditOutcome outcome, int? status, string? detail) => new(
        _time.GetUtcNow(),
        record.Action,
        outcome,
        record.UserId,
        record.UserId is { } userId ? _users.Find(userId)?.Name : null,
        record.DeviceId,
        record.DeviceName,
        record.VmId,
        record.VmName,
        record.Elevated,
        status,
        detail,
        record.JobId);

    /// <summary>
    /// The operation has already happened, so a failure to record its outcome is logged rather than
    /// turned into an error response.
    /// </summary>
    private void WriteCompletion(AuditEntry entry)
    {
        try
        {
            _audit.Write(entry);
        }
        catch (AuditUnavailableException ex)
        {
            _logger.LogError(ex, "Could not write the audit entry for the outcome of {Action}.", entry.Action);
        }
    }
}
