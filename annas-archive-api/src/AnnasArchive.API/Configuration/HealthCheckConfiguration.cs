using System.Text.Json;
using AnnasArchive.API.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnnasArchive.API.Configuration;

/// <summary>
/// Configuration for health checks and health endpoints.
/// </summary>
public static class HealthCheckConfiguration
{
    /// <summary>
    /// Registers all health checks with the DI container.
    /// </summary>
    public static IServiceCollection AddHealthCheckServices(this IServiceCollection services)
    {
        services.AddHealthChecks()
            // Critical checks - required for app to function
            .AddCheck<FileSystemHealthCheck>(
                "filesystem",
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "ready", "live" })
            .AddCheck<DropboxHealthCheck>(
                "dropbox",
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "ready" })

            // External API checks - degraded if unavailable
            .AddCheck<OpenAiHealthCheck>(
                "openai",
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "external" })
            .AddCheck<GoogleBooksHealthCheck>(
                "googlebooks",
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "external" })
            .AddCheck<OpenLibraryHealthCheck>(
                "openlibrary",
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "external" });

        return services;
    }

    /// <summary>
    /// Maps the health check endpoints.
    /// </summary>
    public static WebApplication MapHealthCheckEndpoints(this WebApplication app)
    {
        // Main health endpoint - all checks
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthResponse,
            AllowCachingResponses = false
        });

        // Readiness probe - checks if dependencies are ready
        // Used by container orchestrators for routing traffic
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthResponse,
            AllowCachingResponses = false
        });

        // Liveness probe - checks if the app is running
        // Used by container orchestrators to restart unhealthy containers
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live"),
            ResponseWriter = WriteHealthResponse,
            AllowCachingResponses = false
        });

        // External services only - for monitoring dashboard
        app.MapHealthChecks("/health/external", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("external"),
            ResponseWriter = WriteHealthResponse,
            AllowCachingResponses = false
        });

        return app;
    }

    /// <summary>
    /// Custom JSON response writer for health check results.
    /// </summary>
    private static async Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                duration = entry.Value.Duration.TotalMilliseconds,
                data = entry.Value.Data.Count > 0 ? entry.Value.Data : null,
                exception = entry.Value.Exception?.Message
            })
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, options));
    }
}
