using System.Globalization;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service.Wake;
using HyperHarbor.Shared.Contracts;
using Microsoft.AspNetCore.Diagnostics;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// Converts known exceptions into RFC 7807 problem responses.
/// </summary>
internal sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<ApiExceptionHandler> _logger;

    public ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = Classify(exception);

        if (status == 0)
        {
            // The middleware's own logging is disabled in appsettings.json, so unexpected failures are logged here.
            _logger.LogError(exception, "Unhandled exception for {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
            return false;
        }

        if (status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogWarning(exception, "Request failed: {Title}.", title);
        }

        httpContext.Response.StatusCode = status;
        if (exception is ElevationRateLimitedException limited)
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(limited.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Detail = exception.Message,
            },
        };
        if (ProblemCode(exception) is { } code)
        {
            context.ProblemDetails.Extensions["code"] = code;
        }

        if (exception is LifecycleValidationException { Errors.Count: > 0 } invalid)
        {
            context.ProblemDetails.Extensions["errors"] = invalid.Errors;
        }

        if (exception is ResourceWarningsException warned)
        {
            context.ProblemDetails.Extensions["warnings"] = warned.Warnings;
        }

        return await _problemDetails.TryWriteAsync(context);
    }

    /// <summary>The problem details "code" for errors a client handles differently from others with the same status.</summary>
    internal static string? ProblemCode(Exception exception) => exception switch
    {
        ElevationRequiredException => ContractInfo.ProblemCodes.ElevationRequired,
        ElevationUnavailableException => ContractInfo.ProblemCodes.ElevationUnavailable,
        IncorrectPassphraseException => ContractInfo.ProblemCodes.IncorrectPassphrase,
        ElevationRateLimitedException => ContractInfo.ProblemCodes.TooManyAttempts,
        ResourceWarningsException => ContractInfo.ProblemCodes.ResourceWarnings,
        LifecycleConflictException { Code: { } code } => code,
        ConsoleConflictException conflict => conflict.Code,
        _ => null,
    };

    /// <summary>The response status and title for a known exception, or status 0 for an unexpected one.</summary>
    internal static (int Status, string Title) Classify(Exception exception)
    {
        return exception switch
        {
            AuditUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Audit log unavailable"),
            ElevationRequiredException => (StatusCodes.Status403Forbidden, "Elevation required"),
            ElevationUnavailableException => (StatusCodes.Status403Forbidden, "Elevation unavailable"),
            IncorrectPassphraseException => (StatusCodes.Status403Forbidden, "Incorrect passphrase"),
            IsoNotFoundException => (StatusCodes.Status404NotFound, "Image not found"),
            UnattendProfileNotFoundException => (StatusCodes.Status404NotFound, "Profile not found"),
            InsufficientStorageException => (StatusCodes.Status507InsufficientStorage, "Not enough space"),
            ElevationRateLimitedException => (StatusCodes.Status429TooManyRequests, "Too many attempts"),
            BadHttpRequestException bad => (bad.StatusCode, "Invalid request"),
            VmNotFoundException => (StatusCodes.Status404NotFound, "Virtual machine not found"),
            VmActionNotAllowedException => (StatusCodes.Status409Conflict, "Action not allowed"),
            VmBusyException => (StatusCodes.Status409Conflict, "Virtual machine busy"),
            LifecycleValidationException => (StatusCodes.Status400BadRequest, "Invalid request"),
            LifecycleConflictException => (StatusCodes.Status409Conflict, "Cannot complete the request"),
            ResourceWarningsException => (StatusCodes.Status409Conflict, "Check host resources"),
            HyperVJobFailedException => (StatusCodes.Status502BadGateway, "Hyper-V operation failed"),
            HyperVCallException => (StatusCodes.Status502BadGateway, "Hyper-V operation failed"),
            HyperVUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Hyper-V unavailable"),
            HyperVOperationException => (StatusCodes.Status502BadGateway, "Hyper-V operation failed"),
            WakeTestAlreadyScheduledException => (StatusCodes.Status409Conflict, "Wake test already scheduled"),
            WakeFixUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Approval unavailable"),
            InvalidWakeRequestException => (StatusCodes.Status400BadRequest, "Invalid request"),
            GuestCredentialRejectedException => (StatusCodes.Status422UnprocessableEntity, "Administrator credential rejected"),
            GuestAccountConflictException => (StatusCodes.Status409Conflict, "Cannot provision"),
            ConsoleConflictException => (StatusCodes.Status409Conflict, "Cannot open the console"),
            ConsoleTicketRejectedException => (StatusCodes.Status403Forbidden, "Console ticket rejected"),
            ConsoleUnavailableException => (StatusCodes.Status502BadGateway, "Console unavailable"),
            GuestUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Guest unavailable"),
            GuestOperationException => (StatusCodes.Status502BadGateway, "Guest operation failed"),
            PairingException pairing => pairing.Error switch
            {
                PairingError.InvalidRequest => (StatusCodes.Status400BadRequest, "Invalid pairing request"),
                PairingError.NoDisplay => (StatusCodes.Status503ServiceUnavailable, "Tray app not running"),
                PairingError.RequestPending => (StatusCodes.Status429TooManyRequests, "Pairing in progress"),
                PairingError.NotFound => (StatusCodes.Status404NotFound, "Pairing request not found"),
                PairingError.Gone => (StatusCodes.Status410Gone, "Pairing request ended"),
                PairingError.ConfirmationMismatch => (StatusCodes.Status401Unauthorized, "Incorrect PIN"),
                _ => (StatusCodes.Status500InternalServerError, "Pairing failed"),
            },
            _ => (0, string.Empty),
        };
    }
}
