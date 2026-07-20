using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AnnasArchive.API.Services;
using AnnasArchive.Core.Exceptions;
using Serilog;

namespace AnnasArchive.API.Configuration;

/// <summary>
/// Extension methods for configuring application middleware.
/// </summary>
public static class MiddlewareExtensions
{
    /// <summary>
    /// Adds security headers to all responses.
    /// </summary>
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
            // 'unsafe-inline' on style/script is required by Angular Material's
            // CDK, which injects inline styles at runtime for overlays,
            // breakpoints, and component-scoped styles — there's no practical
            // nonce-based alternative without server-side rendering, which this
            // app doesn't use in production (it's served as a static SPA).
            // fonts.googleapis.com/fonts.gstatic.com are explicitly allowed
            // because index.html loads Roboto + Material Icons from Google Fonts.
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
                "font-src 'self' https://fonts.gstatic.com; " +
                "script-src 'self' 'unsafe-inline'; " +
                // Book covers come from many external, rotating domains
                // (OpenLibrary's CDN, Google's book thumbnails, various
                // LibGen/Anna's Archive mirrors) — an exact allowlist would
                // need constant upkeep as those domains change. Images can't
                // execute code, so allowing any HTTPS source here is a much
                // lower-risk relaxation than doing the same for scripts/styles.
                "img-src 'self' data: https:; " +
                "connect-src 'self'";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            await next();
        });

        return app;
    }

    /// <summary>
    /// Adds request body size limit middleware (10MB for JSON payloads).
    /// </summary>
    public static WebApplication UseRequestBodySizeLimit(this WebApplication app, long maxBodySize = 10 * 1024 * 1024)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.ContentLength > maxBodySize)
            {
                context.Response.StatusCode = 413; // Payload Too Large
                await context.Response.WriteAsJsonAsync(new
                {
                    error = $"Request body too large. Maximum size is {maxBodySize / (1024 * 1024)} MB."
                });
                return;
            }
            await next();
        });

        return app;
    }

    /// <summary>
    /// Adds user activity tracking middleware.
    /// </summary>
    public static WebApplication UseUserActivityTracking(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.User?.Identity?.IsAuthenticated == true)
            {
                var userName = context.User.FindFirst(ClaimTypes.Name)?.Value;
                var activityService = context.RequestServices.GetRequiredService<IUserActivityService>();
                activityService.RecordActivity(userName ?? "");
            }
            await next();
        });

        return app;
    }

    /// <summary>
    /// Configures CORS for the application.
    /// </summary>
    public static WebApplication UseAppCors(this WebApplication app)
    {
        // The Angular build is served by this same API (same-origin) in every
        // deployed environment, so CORS is only needed for local development
        // where `ng serve` runs on a different port than the API.
        app.UseCors(p => p
            .WithOrigins(
                "http://localhost:4200",
                "https://localhost:4200"
            )
            .AllowAnyHeader()
            .AllowAnyMethod());

        return app;
    }

    /// <summary>
    /// Adds global exception handling middleware.
    /// Converts exceptions to consistent JSON error responses.
    /// </summary>
    public static WebApplication UseGlobalExceptionHandler(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(context, ex);
            }
        });

        return app;
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, response) = exception switch
        {
            ValidationException validationEx => (
                HttpStatusCode.BadRequest,
                new ErrorResponse
                {
                    Error = validationEx.Message,
                    ErrorCode = "VALIDATION_ERROR",
                    Details = validationEx.Errors
                }
            ),
            NotFoundException notFoundEx => (
                HttpStatusCode.NotFound,
                new ErrorResponse
                {
                    Error = notFoundEx.Message,
                    ErrorCode = "NOT_FOUND",
                    Details = notFoundEx.ResourceType != null
                        ? new Dictionary<string, string[]> { { "resource", new[] { notFoundEx.ResourceType, notFoundEx.ResourceId ?? "" } } }
                        : null
                }
            ),
            RateLimitException rateLimitEx => (
                HttpStatusCode.TooManyRequests,
                new ErrorResponse
                {
                    Error = rateLimitEx.Message,
                    ErrorCode = "RATE_LIMIT_EXCEEDED",
                    Details = rateLimitEx.RetryAfter.HasValue
                        ? new Dictionary<string, string[]> { { "retryAfter", new[] { rateLimitEx.RetryAfter.Value.TotalSeconds.ToString("F0") } } }
                        : null
                }
            ),
            ExternalApiException externalApiEx => (
                HttpStatusCode.BadGateway,
                new ErrorResponse
                {
                    Error = $"External service error: {externalApiEx.Message}",
                    ErrorCode = "EXTERNAL_API_ERROR",
                    Details = new Dictionary<string, string[]>
                    {
                        { "service", new[] { externalApiEx.ServiceName ?? "Unknown" } },
                        { "isTransient", new[] { externalApiEx.IsTransient.ToString() } }
                    }
                }
            ),
            UnauthorizedException unauthorizedEx => (
                HttpStatusCode.Unauthorized,
                new ErrorResponse
                {
                    Error = unauthorizedEx.Message,
                    ErrorCode = "UNAUTHORIZED"
                }
            ),
            ServiceException serviceEx => (
                serviceEx.StatusCode,
                new ErrorResponse
                {
                    Error = serviceEx.Message,
                    ErrorCode = "SERVICE_ERROR",
                    Details = serviceEx.ServiceName != null
                        ? new Dictionary<string, string[]> { { "service", new[] { serviceEx.ServiceName } } }
                        : null
                }
            ),
            TaskCanceledException or OperationCanceledException => (
                HttpStatusCode.RequestTimeout,
                new ErrorResponse
                {
                    Error = "The request timed out.",
                    ErrorCode = "TIMEOUT"
                }
            ),
            // Argument validation errors (including Dropbox SDK path validation)
            ArgumentNullException argNullEx => (
                HttpStatusCode.BadRequest,
                new ErrorResponse
                {
                    Error = $"Missing required parameter: {argNullEx.ParamName}",
                    ErrorCode = "VALIDATION_ERROR",
                    Details = argNullEx.ParamName != null
                        ? new Dictionary<string, string[]> { { argNullEx.ParamName, new[] { "Value is required" } } }
                        : null
                }
            ),
            ArgumentOutOfRangeException argRangeEx => (
                HttpStatusCode.BadRequest,
                new ErrorResponse
                {
                    Error = $"Invalid parameter value: {argRangeEx.ParamName}",
                    ErrorCode = "VALIDATION_ERROR",
                    Details = argRangeEx.ParamName != null
                        ? new Dictionary<string, string[]> { { argRangeEx.ParamName, new[] { argRangeEx.Message } } }
                        : null
                }
            ),
            ArgumentException argEx => (
                HttpStatusCode.BadRequest,
                new ErrorResponse
                {
                    Error = argEx.Message,
                    ErrorCode = "VALIDATION_ERROR",
                    Details = argEx.ParamName != null
                        ? new Dictionary<string, string[]> { { argEx.ParamName, new[] { argEx.Message } } }
                        : null
                }
            ),
            // Dropbox API exceptions - check for path not found
            Exception ex when ex.GetType().FullName?.StartsWith("Dropbox.Api.ApiException") == true
                && ex.Message.Contains("path/not_found") => (
                HttpStatusCode.NotFound,
                new ErrorResponse
                {
                    Error = "File not found in Dropbox",
                    ErrorCode = "NOT_FOUND"
                }
            ),
            // Other Dropbox API exceptions
            Exception ex2 when ex2.GetType().FullName?.StartsWith("Dropbox.Api.ApiException") == true => (
                HttpStatusCode.BadGateway,
                new ErrorResponse
                {
                    Error = $"Dropbox API error: {ex2.Message}",
                    ErrorCode = "EXTERNAL_API_ERROR",
                    Details = new Dictionary<string, string[]> { { "service", new[] { "Dropbox" } } }
                }
            ),
            _ => (
                HttpStatusCode.InternalServerError,
                new ErrorResponse
                {
                    Error = "An unexpected error occurred.",
                    ErrorCode = "INTERNAL_ERROR"
                }
            )
        };

        // Log the exception with appropriate level
        if (statusCode == HttpStatusCode.InternalServerError)
        {
            Log.Error(exception, "Unhandled exception: {Message}", exception.Message);
        }
        else if (statusCode == HttpStatusCode.BadGateway || statusCode == HttpStatusCode.ServiceUnavailable)
        {
            Log.Warning(exception, "External service error: {Message}", exception.Message);
        }
        else
        {
            Log.Information("Request failed with {StatusCode}: {Message}", (int)statusCode, exception.Message);
        }

        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, jsonOptions));
    }

    /// <summary>
    /// Standard error response format for all API errors.
    /// </summary>
    private class ErrorResponse
    {
        public required string Error { get; init; }
        public string? ErrorCode { get; init; }
        public IDictionary<string, string[]>? Details { get; init; }
    }
}
