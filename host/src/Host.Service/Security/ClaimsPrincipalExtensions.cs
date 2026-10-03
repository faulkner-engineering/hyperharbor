using System.Security.Claims;

namespace HyperHarbor.Host.Service.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The User the calling paired device belongs to.</summary>
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirst(PairedDeviceAuthenticationHandler.UserIdClaim)?.Value
            ?? throw new InvalidOperationException("The request is not from a paired device."));

    /// <summary>The calling paired device.</summary>
    public static Guid DeviceId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirst(PairedDeviceAuthenticationHandler.DeviceIdClaim)?.Value
            ?? throw new InvalidOperationException("The request is not from a paired device."));
}
