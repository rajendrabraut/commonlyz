using Commonlyz.Files.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Commonlyz.Files.Api;

/// <summary>
/// Turns exceptions into RFC 7807 problem responses. User errors keep their message; engine failures and bugs
/// get a generic message so internal paths and stack traces never reach the client.
/// </summary>
public sealed class ErrorHandler(IProblemDetailsService problems, ILogger<ErrorHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            InvalidInputException e => (StatusCodes.Status400BadRequest, "Invalid input", e.Message),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                (StatusCodes.Status413PayloadTooLarge, "Too large", "The upload is too large."),
            InvalidDataException or BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request", "The upload could not be read."),
            FeatureUnavailableException e => (StatusCodes.Status503ServiceUnavailable, "Unavailable", e.Message),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "Cancelled", "The request was cancelled."),
            _ => (StatusCodes.Status500InternalServerError, "Processing failed", "Something went wrong while processing the file. Please try again."),
        };

        if (status >= 500)
            logger.LogError(exception, "Request failed: {Path}", context.Request.Path);

        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }
}
