using AnnasArchive.API.Helpers;
using AnnasArchive.API.Infrastructure;
using Microsoft.AspNetCore.Authorization;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping development and admin endpoints.
/// </summary>
public static class DevEndpoints
{
    /// <summary>
    /// Maps development helper endpoints and cache management endpoints.
    /// </summary>
    public static WebApplication MapDevEndpoints(this WebApplication app)
    {
#if DEBUG
        // Development helper: Generate BCrypt hashes for access codes
        app.MapGet("/api/dev/hash", (string? code) =>
        {
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest(new { error = "Provide ?code=yourcode in the query string" });

            var hash = BCrypt.Net.BCrypt.HashPassword(code, workFactor: 12);

            return Results.Ok(new
            {
                original = code,
                hashed = hash,
                instructions = "Copy the 'hashed' value to appsettings.json Auth:AccessCodes:Code field"
            });
        });
#endif

        // ─── Cache Management Endpoints ────────────────────────────────────────────
        // These are available in all builds for production monitoring

        app.MapGet("/api/dev/cache/stats", [Authorize(Roles = "Admin")] () =>
        {
            var stats = new Dictionary<string, object>
            {
                ["libraryChapterContent"] = LibraryEpubCache.GetCacheStatistics()
            };

            return Results.Ok(stats);
        })
        .WithName("GetCacheStats")
        .WithTags("Dev");

        app.MapDelete("/api/dev/cache", [Authorize(Roles = "Admin")] (string? name) =>
        {
            if (string.IsNullOrEmpty(name) || name == "all")
            {
                // Clear all caches
                LibraryEpubCache.ClearCache();
                return Results.Ok(new { message = "All caches cleared" });
            }

            // Clear specific cache
            return name.ToLowerInvariant() switch
            {
                "librarychaptercontent" or "library" => ClearAndRespond("libraryChapterContent", LibraryEpubCache.ClearCache),
                _ => Results.NotFound(new { error = $"Unknown cache: {name}" })
            };
        })
        .WithName("ClearCache")
        .WithTags("Dev");

        return app;
    }

    private static IResult ClearAndRespond(string name, Action clearAction)
    {
        clearAction();
        return Results.Ok(new { message = $"Cache '{name}' cleared" });
    }
}
