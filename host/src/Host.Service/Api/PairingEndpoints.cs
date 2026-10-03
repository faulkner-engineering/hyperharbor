using HyperHarbor.Host.Service.Audit;
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

        pairing.MapPost("/requests", CreateRequest).AllowAnonymous().WithName("createPairingRequest")
            .Audited<PairingRequest>(request => $"deviceName={request.DeviceName}");
        pairing.MapDelete("/requests/{pairingId:guid}", CancelRequest).AllowAnonymous().WithName("cancelPairingRequest")
            .Audited<Guid>(pairingId => $"pairingId={pairingId}");
        pairing.MapPost("/requests/{pairingId:guid}/confirm", Confirm).AllowAnonymous().WithName("confirmPairing")
            .Audited<Guid>(pairingId => $"pairingId={pairingId}");
        pairing.MapDelete("/devices/self", UnpairSelf).WithName("unpairSelf").Audited();

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

    private static Ok<PairingResult> Confirm(
        Guid pairingId,
        PairingConfirmation confirmation,
        PairingService pairing,
        PairedDeviceStore devices,
        HttpContext context)
    {
        var audit = context.Audit()!;
        var result = pairing.Confirm(pairingId, confirmation);

        // The caller is anonymous until this succeeds; record the device it became.
        audit.DeviceId = result.DeviceId;
        audit.UserId = result.UserId;
        audit.DeviceName = devices.List().FirstOrDefault(device => device.DeviceId == result.DeviceId)?.Name;
        return TypedResults.Ok(result);
    }

    private static NoContent UnpairSelf(HttpContext context, PairedDeviceStore devices, ILogger<PairingService> logger)
    {
        var deviceId = Guid.Parse(context.User.FindFirst(PairedDeviceAuthenticationHandler.DeviceIdClaim)!.Value);
        devices.Remove(deviceId);
        logger.LogInformation("Device {DeviceId} unpaired itself.", deviceId);
        return TypedResults.NoContent();
    }
}
