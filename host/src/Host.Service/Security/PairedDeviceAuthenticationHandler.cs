using System.Security.Claims;
using System.Text.Encodings.Web;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Tailscale;
using HyperHarbor.Shared.Contracts;
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

    private const string NodeProblemKey = "hyperharbor:tailscale_node_problem";

    private readonly PairedDeviceStore _devices;
    private readonly TailscaleNodeVerifier _nodes;

    public PairedDeviceAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PairedDeviceStore devices,
        TailscaleNodeVerifier nodes)
        : base(options, logger, encoder)
    {
        _devices = devices;
        _nodes = nodes;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var certificate = Context.Connection.ClientCertificate;
        if (certificate is null)
        {
            return AuthenticateResult.NoResult();
        }

        var device = _devices.FindByFingerprint(CertificateFingerprint.Of(certificate));
        if (device is null)
        {
            return AuthenticateResult.Fail("The client certificate is not paired with this host.");
        }

        // A request over the tailnet must come from the node the device is tied to. The certificate stays the credential.
        if (Context.Connection.RemoteIpAddress is { } remote)
        {
            var check = await _nodes.CheckAsync(remote, device.TailscaleNodeId, Context.RequestAborted);
            if (!check.Allowed)
            {
                Logger.LogWarning(
                    "Refused {DeviceName} over the tailnet: {Decision} (recorded {Recorded}, request from {Actual}).",
                    device.Name,
                    check.Decision,
                    device.TailscaleNodeId,
                    check.Node?.NodeId ?? "unknown");
                Context.Items[NodeProblemKey] = check.Decision;
                return AuthenticateResult.Fail("The request came from a Tailscale node this device is not tied to.");
            }

            if (check.Decision == NodeDecision.Bound && check.Node is { } node)
            {
                _devices.SetTailscaleNode(device.DeviceId, node.NodeId, node.Name);
            }
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(DeviceIdClaim, device.DeviceId.ToString("D")),
                new Claim(UserIdClaim, device.UserId.ToString("D")),
                new Claim(ClaimTypes.Name, device.Name),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.TryGetValue(NodeProblemKey, out var problem) && problem is NodeDecision decision)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            await Context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = Context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Tailscale node mismatch",
                    Detail = decision == NodeDecision.Unverifiable
                        ? "This device is tied to a Tailscale node, and this host cannot tell which node sent the request. Check that Tailscale runs on the host, or connect over the local network."
                        : "This device is paired from a different Tailscale node. Connect from the original device, or pair this device again.",
                    Extensions =
                    {
                        ["code"] = decision == NodeDecision.Unverifiable
                            ? ContractInfo.ProblemCodes.TailscaleNodeUnverified
                            : ContractInfo.ProblemCodes.TailscaleNodeMismatch,
                    },
                },
            });
            return;
        }

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
