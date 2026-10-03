using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Service.Security;

/// <summary>
/// Marks an endpoint as requiring elevation. <see cref="Conditional"/> endpoints require it only for
/// some requests, for example performVmAction with turnOff; api.yaml documents those in the operation.
/// </summary>
public sealed record ElevationRequiredMetadata(bool Conditional);

public static class ElevationEndpointExtensions
{
    /// <summary>
    /// Requires a valid elevation token in the <see cref="ContractInfo.ElevationHeader"/> header.
    /// With <paramref name="when"/>, only requests whose bound arguments match require it.
    /// Call after Audited, so refused requests are audited.
    /// </summary>
    public static RouteHandlerBuilder RequireElevation(this RouteHandlerBuilder builder, Func<IList<object?>, bool>? when = null) =>
        builder
            .WithMetadata(new ElevationRequiredMetadata(Conditional: when is not null))
            .AddEndpointFilter(new ElevationEndpointFilter(when));
}

internal sealed class ElevationEndpointFilter(Func<IList<object?>, bool>? when) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (when is not null && !when(context.Arguments))
        {
            return next(context);
        }

        var http = context.HttpContext;
        var elevation = http.RequestServices.GetRequiredService<ElevationService>();
        var token = http.Request.Headers[ContractInfo.ElevationHeader].ToString();
        if (!elevation.IsElevated(http.User.DeviceId(), http.User.UserId(), token))
        {
            throw elevation.IsConfigured ? new ElevationRequiredException() : new ElevationUnavailableException();
        }

        if (http.Audit() is { } audit)
        {
            audit.Elevated = true;
        }

        return next(context);
    }
}
