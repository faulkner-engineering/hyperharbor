using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
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
        var (status, title) = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, "Invalid request"),
            VmNotFoundException => (StatusCodes.Status404NotFound, "Virtual machine not found"),
            VmActionNotAllowedException => (StatusCodes.Status409Conflict, "Action not allowed"),
            HyperVUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Hyper-V unavailable"),
            HyperVOperationException => (StatusCodes.Status502BadGateway, "Hyper-V operation failed"),
            _ => (0, string.Empty),
        };

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
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Detail = exception.Message,
            },
        });
    }
}
