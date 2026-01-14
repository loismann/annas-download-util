using System.Security.Claims;
using AnnasArchive.API.Services;

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
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'";
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
        app.UseCors(p => p
            .WithOrigins(
                "https://fs01pfbooks.synology.me",      // Production HTTPS
                "http://fs01pfbooks.synology.me",       // Production HTTP (fallback)
                "http://localhost:4200",                // Local dev
                "https://localhost:4200"                // Local dev HTTPS
            )
            .AllowAnyHeader()
            .AllowAnyMethod());

        return app;
    }
}
