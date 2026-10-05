using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Installation;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps /host/update in the Host tag of docs/api.yaml. Only the installed service has an <see cref="UpdateCoordinator"/>.</summary>
public static class UpdateEndpoints
{
    public const string StatusPath = ContractInfo.BasePath + "/host/update";

    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(StatusPath, GetStatus).WithName("getHostUpdate");
        endpoints.MapPost(StatusPath + "/check", Check).WithName("checkHostUpdate").Audited();
        endpoints.MapPut(StatusPath + "/settings", SaveSettings).WithName("setHostUpdateSettings")
            .Audited<HostUpdateSettings>(settings => $"channel={settings.Channel} mode={settings.Mode} maintenanceTime={settings.MaintenanceTime ?? "none"}")
            .RequireElevation();
        return endpoints;
    }

    /// <summary>The status for the API and the tray.</summary>
    internal static HostUpdateStatus Status(UpdateCoordinator? coordinator, UpdateSettings settings)
    {
        var options = settings.Current();
        var channels = options.Channels.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (coordinator is null)
        {
            return new HostUpdateStatus(
                false, Map(options.Mode), options.Channel, channels, options.MaintenanceTime, HostVersion.Current.ToString(),
                null, null, HostUpdateActivity.Idle, null, "Updates apply to the installed host. This one runs without being installed.", null, []);
        }

        var status = coordinator.Status;
        return new HostUpdateStatus(
            true,
            Map(status.Mode),
            status.Channel,
            channels,
            options.MaintenanceTime,
            status.CurrentVersion,
            status.AvailableVersion,
            status.NotesUrl,
            status.Activity switch
            {
                UpdateActivity.Checking => HostUpdateActivity.Checking,
                UpdateActivity.Preparing => HostUpdateActivity.Preparing,
                UpdateActivity.Ready => HostUpdateActivity.Ready,
                UpdateActivity.Installing => HostUpdateActivity.Installing,
                _ => HostUpdateActivity.Idle,
            },
            status.LastCheck,
            status.Message,
            status.LastResult,
            status.RolledBack);
    }

    private static Ok<HostUpdateStatus> GetStatus(IServiceProvider services, UpdateSettings settings) =>
        TypedResults.Ok(Status(services.GetService<UpdateCoordinator>(), settings));

    private static Results<Accepted<HostUpdateStatus>, ProblemHttpResult> Check(IServiceProvider services, UpdateSettings settings)
    {
        if (services.GetService<UpdateCoordinator>() is not { } coordinator)
        {
            return Unsupported();
        }

        coordinator.RequestCheck();
        return TypedResults.Accepted(StatusPath, Status(coordinator, settings));
    }

    private static Results<Ok<HostUpdateStatus>, ProblemHttpResult> SaveSettings(HostUpdateSettings request, IServiceProvider services, UpdateSettings settings)
    {
        if (services.GetService<UpdateCoordinator>() is not { } coordinator)
        {
            return Unsupported();
        }

        var mode = request.Mode switch
        {
            HostUpdateMode.Notify => UpdateMode.Notify,
            HostUpdateMode.Off => UpdateMode.Off,
            _ => UpdateMode.Auto,
        };
        try
        {
            settings.Save(new UpdatePreferences(request.Channel, mode, request.MaintenanceTime));
        }
        catch (ArgumentException ex)
        {
            var field = ex.Message.Contains("maintenance", StringComparison.OrdinalIgnoreCase) ? "maintenanceTime" : "channel";
            var message = ex.Message.Split(" (Parameter", 2)[0];
            throw new LifecycleValidationException(message, [new ValidationIssue(field, message)]);
        }

        coordinator.RequestCheck();
        return TypedResults.Ok(Status(coordinator, settings));
    }

    private static ProblemHttpResult Unsupported() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Updates are not available",
        detail: "This host runs without being installed, so it does not update itself. Install it to get updates.",
        extensions: new Dictionary<string, object?> { ["code"] = ContractInfo.ProblemCodes.UpdatesUnsupported });

    internal static HostUpdateMode Map(UpdateMode mode) => mode switch
    {
        UpdateMode.Notify => HostUpdateMode.Notify,
        UpdateMode.Off => HostUpdateMode.Off,
        _ => HostUpdateMode.Auto,
    };
}
