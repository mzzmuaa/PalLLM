using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PalLLM.Sidecar;

/// <summary>
/// <see cref="IExceptionHandler"/> that maps malformed-input exceptions
/// from the ASP.NET Core model binder to a 400 ProblemDetails response.
/// Without this handler, a malformed JSON body or an empty body on a
/// minimal-API endpoint that expects a typed model parameter throws
/// <see cref="BadHttpRequestException"/> (or a wrapped
/// <see cref="JsonException"/>) and the default exception handler maps
/// it to a confusing 500 — which violates the "never leak 5xx for bad
/// input" hard rule and surprises operators who think the server is
/// broken.
///
/// <para>
/// This handler is registered first in the DI chain so it sees the
/// exception before any other handler. It intentionally only handles
/// exception shapes that are unambiguously caller-error so it never
/// masks a real server-side bug.
/// </para>
/// </summary>
internal sealed class MalformedRequestExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<MalformedRequestExceptionHandler> _logger;

    public MalformedRequestExceptionHandler(
        IProblemDetailsService problemDetails,
        ILogger<MalformedRequestExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!IsMalformedInputException(exception))
        {
            return false;
        }

        // Don't log full stack — the exception is caller error, not a
        // server bug. A single warning with the request path + reason
        // is enough for operators to spot a misbehaving client.
        _logger.LogDebug(
            "Rejecting malformed request to {Path}: {Reason}",
            httpContext.Request.Path,
            exception.Message);

        httpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;

        ProblemDetails problem = new()
        {
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            Title = "Bad Request",
            Status = (int)HttpStatusCode.BadRequest,
            Detail = "The request body could not be parsed. Verify it is valid JSON matching the documented endpoint contract.",
            Instance = httpContext.Request.Path,
        };

        ProblemDetailsContext context = new()
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        };

        return await _problemDetails.TryWriteAsync(context);
    }

    /// <summary>
    /// Returns <c>true</c> only for exception shapes that unambiguously
    /// indicate caller-side malformed input. The list is deliberately
    /// narrow so genuine server bugs continue to surface as 500.
    /// </summary>
    private static bool IsMalformedInputException(Exception exception)
    {
        // ASP.NET Core 8/9/10 minimal API parameter-binding pipeline
        // wraps JsonException, EndOfStreamException, and similar in
        // BadHttpRequestException with StatusCode == 400.
        if (exception is BadHttpRequestException badRequest &&
            badRequest.StatusCode == (int)HttpStatusCode.BadRequest)
        {
            return true;
        }

        // A JsonException can leak through directly when streaming
        // deserialization is used outside the minimal-API binder.
        if (exception is JsonException)
        {
            return true;
        }

        // Some payloads cause EndOfStreamException to bubble up before
        // the binder can wrap it.
        if (exception is EndOfStreamException)
        {
            return true;
        }

        return false;
    }
}

/// <summary>
/// DI-extension helper so Program.cs can register the handler with one
/// call and not have to spell the type name out twice.
/// </summary>
internal static class MalformedRequestExceptionHandlerExtensions
{
    public static IServiceCollection AddMalformedRequestExceptionHandler(this IServiceCollection services)
    {
        services.AddExceptionHandler<MalformedRequestExceptionHandler>();
        return services;
    }
}
