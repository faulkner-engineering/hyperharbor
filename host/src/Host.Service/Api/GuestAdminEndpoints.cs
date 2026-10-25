using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps the administrator window routes of docs/api.yaml (openGuestAdmin and its siblings).</summary>
public static class GuestAdminEndpoints
{
    public static IEndpointRouteBuilder MapGuestAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var vms = endpoints.MapGroup(VmEndpoints.BasePath);
        vms.MapGet("/{vmId:guid}/guest-admin", GetAsync).WithName("getGuestAdmin");

        // The handlers add the account and expiry to the audit summary; the passphrase never goes into it.
        vms.MapPost("/{vmId:guid}/guest-admin", OpenAsync).WithName(GuestAdminAudit.OpenAction)
            .Audited()
            .RequireElevation();
        vms.MapDelete("/{vmId:guid}/guest-admin", CloseAsync).WithName(GuestAdminAudit.CloseAction)
            .Audited();

        endpoints.MapPut(ContractInfo.BasePath + "/host/guest-admin/settings", SetSettings).WithName("setGuestAdminSettings")
            .Audited<GuestAdminSettings>(settings => $"windowMinutes={settings.WindowMinutes}")
            .RequireElevation();
        return endpoints;
    }

    private static async Task<Ok<GuestAdminStatus>> GetAsync(Guid vmId, GuestAdminService guestAdmin, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await guestAdmin.GetStatusAsync(vmId, context.User.UserId(), cancellationToken));

    private static async Task<Ok<GuestAdminWindow>> OpenAsync(Guid vmId, GuestAdminService guestAdmin, HttpContext context, CancellationToken cancellationToken)
    {
        var requester = new GuestAdminRequester(context.User.DeviceId(), context.User.Identity?.Name);
        var window = await guestAdmin.OpenAsync(vmId, context.User.UserId(), requester, cancellationToken);
        context.Audit()!.Detail = GuestAdminAudit.Detail(window.AccountName, window.ExpiresAt);

        // The body carries the passphrase; no cache may keep it.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return TypedResults.Ok(window);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> CloseAsync(Guid vmId, GuestAdminService guestAdmin, HttpContext context, CancellationToken cancellationToken)
    {
        var userId = context.User.UserId();
        var status = await guestAdmin.GetStatusAsync(vmId, userId, cancellationToken);
        if (status is { Open: true, ExpiresAt: { } expiresAt })
        {
            context.Audit()!.Detail = GuestAdminAudit.Detail(status.AccountName, expiresAt, GuestAdminCloseReason.User);
        }

        return status.Open && await guestAdmin.CloseAsync(vmId, userId, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "No administrator window",
                detail: "No administrator window is open in this VM for you.");
    }

    private static Results<Ok<GuestAdminSettings>, ProblemHttpResult> SetSettings(GuestAdminSettings settings, GuestAdminService guestAdmin)
    {
        try
        {
            return TypedResults.Ok(guestAdmin.SetSettings(settings));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: exception.Message.Split(" (Parameter")[0]);
        }
    }
}
