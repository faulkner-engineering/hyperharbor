using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Pairing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// Maps the Pairing tag of docs/api.yaml. Request and confirm are anonymous; they are
/// protected by the SPAKE2 exchange instead of a client certificate.
/// </summary>
public static class PairingEndpoints
{
    public const string BasePath = ContractInfo.BasePath + "/pairing";

    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var pairing = endpoints.MapGroup(BasePath);

        pairing.MapPost("/requests", CreateRequest).AllowAnonymous().WithName("createPairingRequest");
        pairing.MapDelete("/requests/{pairingId:guid}", CancelRequest).AllowAnonymous().WithName("cancelPairingRequest");
        pairing.MapPost("/requests/{pairingId:guid}/confirm", Confirm).AllowAnonymous().WithName("confirmPairing");
        pairing.MapDelete("/devices/self", UnpairSelf).WithName("unpairSelf");

        return endpoints;
    }

    private static Created<PairingRequestCreated> CreateRequest(PairingRequest request, PairingService pairing)
    {
        var created = pairing.CreateRequest(request);
        return TypedResults.Created((string?)null, created);
    }

    private static NoContent CancelRequest(Guid pairingId, PairingService pairing)
    {
        pairing.Cancel(pairingId);
        return TypedResults.NoContent();
    }

    private static Ok<PairingResult> Confirm(Guid pairingId, PairingConfirmation confirmation, PairingService pairing)
    {
        return TypedResults.Ok(pairing.Confirm(pairingId, confirmation));
    }

    private static NoContent UnpairSelf(HttpContext context, PairedDeviceStore devices, ILogger<PairingService> logger)
    {
        var deviceId = Guid.Parse(context.User.FindFirst(PairedDeviceAuthenticationHandler.DeviceIdClaim)!.Value);
        devices.Remove(deviceId);
        logger.LogInformation("Device {DeviceId} unpaired itself.", deviceId);
        return TypedResults.NoContent();
    }
}
