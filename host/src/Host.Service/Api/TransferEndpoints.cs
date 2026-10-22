using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Wire = HyperHarbor.Shared.Contracts.Transfers;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// Maps the Transfers tag of docs/api.yaml. A paired client authorizes a transfer on both hosts (offer on the receiving
/// host, start on the sending host); the sending host then calls the receiving host's ticket routes itself.
/// </summary>
public static partial class TransferEndpoints
{
    public const string TransfersPath = ContractInfo.BasePath + "/transfers";

    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var vms = endpoints.MapGroup(ContractInfo.BasePath + "/vms");
        vms.MapGet("/{vmId:guid}/transfer-preview", PreviewAsync).WithName("getTransferPreview");
        vms.MapPost("/{vmId:guid}/transfer", StartAsync).WithName("startTransfer")
            .Audited<Wire.StartTransferRequest>(request =>
                $"mode={request.Mode}, tpm={request.TpmMode}, target={request.Target.Name} ({request.Target.HostId}), transfer={request.TransferId}")
            .RequireElevation();
        vms.MapPost("/{vmId:guid}/transfer/cancel", Cancel).WithName("cancelTransfer").Audited();

        var transfers = endpoints.MapGroup(TransfersPath);
        transfers.MapPost("/incoming", OfferAsync).WithName("offerIncomingTransfer")
            .Audited<Wire.IncomingTransferRequest>(request =>
                $"mode={request.Mode}, tpm={request.TpmMode}, vm={request.Preview.Name} ({request.Preview.VmId}), disks={request.Preview.Disks.Count}, bytes={request.Preview.TotalBytes}")
            .RequireElevation();

        // The sending host's data requests: the transfer's ticket and the sending host's certificate, nothing else.
        var ticket = transfers.MapGroup("/{transferId:guid}").RequireAuthorization(TransferTicketAuthenticationHandler.PolicyName);
        ticket.MapGet(string.Empty, Status).WithName("getIncomingTransfer");
        ticket.MapPut("/files/{fileKey}", ReceiveBlockAsync).WithName("receiveTransferBlock").Audited();
        ticket.MapPost("/records", ReceiveRecordsAsync).WithName("sendTransferRecords")
            .Audited<Wire.TransferRecords>(records =>
                $"accounts={records.Accounts.Count}, adminCredential={(records.AdminCredential is null ? "no" : "yes")}, gamePolicy={records.GamePolicy?.Action.ToString() ?? "none"}");
        ticket.MapPost("/import", ImportAsync).WithName("importTransfer").Audited();
        ticket.MapPost("/complete", CompleteAsync).WithName("completeTransfer").Audited();
        ticket.MapDelete(string.Empty, CancelIncomingAsync).WithName("cancelIncomingTransfer").Audited();
        return endpoints;
    }

    private static async Task<Ok<Wire.TransferPreview>> PreviewAsync(Guid vmId, TransferPreviewService previews, CancellationToken cancellationToken) =>
        TypedResults.Ok((await previews.PreviewAsync(vmId, cancellationToken)).ToContract());

    private static async Task<Accepted<VmJob>> StartAsync(
        Guid vmId,
        Wire.StartTransferRequest request,
        TransferJobs jobs,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var job = await jobs.StartAsync(vmId, context.User.UserId(), request.ToCore(vmId), request.ConfirmName, context.CompletionAuditor(), cancellationToken);
        context.Audit()!.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    private static Results<NoContent, ProblemHttpResult> Cancel(Guid vmId, TransferJobs jobs, ClaimsPrincipal user) =>
        jobs.Cancel(vmId, user.UserId())
            ? TypedResults.NoContent()
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No transfer running", detail: $"No transfer of VM {vmId} is running for this User.");

    private static async Task<Created<Wire.TransferOffer>> OfferAsync(
        Wire.IncomingTransferRequest request,
        TransferReceiver receiver,
        INetworkAdapterReader adapters,
        ClaimsPrincipal user,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var offer = await receiver.OfferAsync(request.ToCore(), user.UserId(), cancellationToken);
        IReadOnlyList<string> addresses;
        try
        {
            addresses = TransferAddresses.Rank(await adapters.ReadAdaptersAsync(cancellationToken)).Take(16).ToList();
        }
        catch (Exception ex) when (ex is Microsoft.Management.Infrastructure.CimException or UnauthorizedAccessException)
        {
            // The client's own addresses for this host still work; they are only not ordered by link.
            loggers.CreateLogger(typeof(TransferEndpoints)).LogWarning(ex, "Could not read this host's network adapters for transfer {TransferId}.", offer.TransferId);
            addresses = [];
        }

        return TypedResults.Created($"{TransfersPath}/{offer.TransferId:D}", offer.ToContract(addresses));
    }

    private static Ok<Wire.IncomingTransferStatus> Status(Guid transferId, TransferReceiver receiver) =>
        TypedResults.Ok(receiver.Status(transferId).ToContract());

    /// <summary>One block: Content-Range "bytes a-b/size" and Content-Digest "sha-256=:&lt;base64&gt;:".</summary>
    private static async Task<Results<Ok<Wire.IncomingTransferFile>, ProblemHttpResult>> ReceiveBlockAsync(
        Guid transferId, string fileKey, HttpContext context, TransferReceiver receiver, CancellationToken cancellationToken)
    {
        // A block is up to 256 MiB, beyond Kestrel's default request size limit.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = TransferReceiver.BlockSize + 1;
        }

        if (!ContentRangeHeaderValue.TryParse(context.Request.Headers.ContentRange.ToString(), out var range)
            || range is not { Unit: "bytes", From: { } from, To: { } to, Length: { } size }
            || to < from)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status416RangeNotSatisfiable, title: "Invalid range",
                detail: "Send Content-Range \"bytes <first>-<last>/<file size>\" with each block.");
        }

        var length = to - from + 1;
        if (context.Request.ContentLength is { } sent && sent != length)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid block",
                detail: $"Content-Length is {sent}, but the range holds {length} bytes.");
        }

        if (Sha256Digest().Match(context.Request.Headers["Content-Digest"].ToString()) is not { Success: true } digest
            || !TryDecode(digest.Groups[1].Value, out var sha256))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid block",
                detail: "Send Content-Digest \"sha-256=:<base64>:\" with each block.");
        }

        context.Audit()!.Detail = $"file={fileKey}, bytes={from}-{to}/{size}";
        var file = await receiver.ReceiveBlockAsync(transferId, fileKey, from, length, size, sha256, context.Request.Body, cancellationToken);
        return TypedResults.Ok(file.ToContract());
    }

    private static async Task<NoContent> ReceiveRecordsAsync(
        Guid transferId, Wire.TransferRecords records, TransferReceiver receiver, CancellationToken cancellationToken)
    {
        await receiver.ReceiveRecordsAsync(transferId, records.ToCore(), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<Wire.TransferImported>> ImportAsync(Guid transferId, TransferReceiver receiver, CancellationToken cancellationToken) =>
        TypedResults.Ok((await receiver.ImportAsync(transferId, cancellationToken)).ToContract());

    private static async Task<NoContent> CompleteAsync(Guid transferId, TransferReceiver receiver, CancellationToken cancellationToken)
    {
        await receiver.CompleteAsync(transferId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<Wire.IncomingTransferCanceled>> CancelIncomingAsync(Guid transferId, TransferReceiver receiver, CancellationToken cancellationToken) =>
        TypedResults.Ok(new Wire.IncomingTransferCanceled(await receiver.CancelAsync(transferId, cancellationToken)));

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        bytes = new byte[32];
        return Convert.TryFromBase64String(base64, bytes, out var written) && written == bytes.Length;
    }

    [GeneratedRegex(@"(?:^|,)\s*sha-256=:([A-Za-z0-9+/=]{1,64}):", RegexOptions.IgnoreCase)]
    private static partial Regex Sha256Digest();
}
