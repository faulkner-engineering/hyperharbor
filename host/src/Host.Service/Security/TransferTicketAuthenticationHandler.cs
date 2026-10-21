using System.Security.Claims;
using System.Text.Encodings.Web;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Shared.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Service.Security;

/// <summary>
/// Authenticates the data requests of an incoming transfer (the transferTicket security scheme): the ticket this host
/// issued for the transfer named in the route, sent as "Authorization: HyperHarbor-Transfer &lt;ticket&gt;", from a
/// connection whose TLS client certificate is the source host's (the fingerprint the authorizing client gave). Only the
/// ticket routes use this scheme, and they use nothing else; a ticket opens no other route.
/// </summary>
public sealed class TransferTicketAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TransferTicket";
    public const string PolicyName = "TransferTicket";
    public const string TransferIdClaim = "hyperharbor:transfer_id";

    /// <summary>How the audit trail names the caller of a ticket route.</summary>
    public const string PeerName = "peer host (transfer ticket)";

    private readonly TransferReceiver _receiver;

    public TransferTicketAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TransferReceiver receiver)
        : base(options, logger, encoder)
    {
        _receiver = receiver;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var prefix = ContractInfo.TransferTicketScheme + " ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (Context.Connection.ClientCertificate is not { } certificate
            || !Guid.TryParse(Request.RouteValues["transferId"]?.ToString(), out var transferId)
            || _receiver.Authenticate(transferId, header[prefix.Length..].Trim(), CertificateFingerprint.Of(certificate)) is not { } principal)
        {
            return Task.FromResult(AuthenticateResult.Fail("The transfer ticket or the source host's certificate is not valid for this transfer."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(TransferIdClaim, principal.TransferId.ToString("D")),
                new Claim(PairedDeviceAuthenticationHandler.UserIdClaim, principal.UserId.ToString("D")),
                new Claim(ClaimTypes.Name, PeerName),
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
                Title = "Transfer ticket required",
                Detail = "This route takes only a transfer's ticket from the host that sends it. The ticket may have ended; start the transfer again.",
            },
        });
    }
}
