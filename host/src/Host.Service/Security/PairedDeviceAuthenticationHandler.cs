using System.Security.Claims;
using System.Text.Encodings.Web;
using HyperHarbor.Host.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Service.Security;

/// <summary>
/// Authenticates a request when its TLS client certificate belongs to a paired device.
/// Kestrel accepts any client certificate during the handshake; the pinned fingerprint check
/// happens here so unpaired devices can still reach the pairing endpoints.
/// </summary>
public sealed class PairedDeviceAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "PairedDevice";
    public const string DeviceIdClaim = "hyperharbor:device_id";
    public const string UserIdClaim = "hyperharbor:user_id";

    private readonly PairedDeviceStore _devices;

    public PairedDeviceAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PairedDeviceStore devices)
        : base(options, logger, encoder)
    {
        _devices = devices;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var certificate = Context.Connection.ClientCertificate;
        if (certificate is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var device = _devices.FindByFingerprint(CertificateFingerprint.Of(certificate));
        if (device is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The client certificate is not paired with this host."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(DeviceIdClaim, device.DeviceId.ToString("D")),
                new Claim(UserIdClaim, device.UserId.ToString("D")),
                new Claim(ClaimTypes.Name, device.Name),
            ],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails =
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Pairing required",
                Detail = "Pair this device with the host before calling this endpoint.",
            },
        });
    }
}
