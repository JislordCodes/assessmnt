using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ExpenseClassifier.Services;

/// <summary>
/// Central exception -> RFC 7807 <see cref="ProblemDetails"/> mapping.
/// Client mistakes are 4xx with actionable detail; upstream AI problems are 429/502/504 with a safe,
/// generic message (no provider internals, keys or stack traces leak to callers).
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Caller hung up: nothing to send, and it is not a server error. 499 = "client closed request".
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var (status, title) = exception switch
        {
            ClassificationFailedException e when e.StatusCode == StatusCodes.Status429TooManyRequests =>
                (StatusCodes.Status429TooManyRequests, "AI service is rate limited"),
            ClassificationFailedException e when e.StatusCode == StatusCodes.Status504GatewayTimeout =>
                (StatusCodes.Status504GatewayTimeout, "AI service timed out"),
            ClassificationFailedException => (StatusCodes.Status502BadGateway, "AI service failure"),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Malformed request"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error")
        };

        if (status >= 500 && exception is not ClassificationFailedException)
        {
            logger.LogError(exception, "Unhandled exception processing {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
        }

        if (exception is ClassificationFailedException { RetryAfterSeconds: { } retryAfter })
        {
            httpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status == StatusCodes.Status500InternalServerError
                    ? "An unexpected error occurred."
                    : exception is ArgumentException or BadHttpRequestException or ClassificationFailedException
                        ? exception.Message
                        : null
            }
        });
    }
}
