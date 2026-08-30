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
                return ApiResponse.BadRequest("Provide ?code=yourcode in the query string");

            var hash = BCrypt.Net.BCrypt.HashPassword(code, workFactor: 12);

            return Results.Ok(new
            {
                original = code,
                hashed = hash,
                instructions = "Copy the 'hashed' value to appsettings.json Auth:AccessCodes:Code field"
            });
        })
        // Anonymous by necessity — it exists to mint the hash you need before you
        // have a credential to authenticate with. So it gets the strict "login"
        // limiter rather than "api": BCrypt at work factor 12 is deliberately
        // expensive, which makes an unauthenticated, unlimited caller a way to
        // burn the CPU. DEBUG-only, but the limiter costs nothing to add.
        .RequireRateLimiting("login");
#endif


        return app;
    }

    private static IResult ClearAndRespond(string name, Action clearAction)
    {
        clearAction();
        return Results.Ok(new { message = $"Cache '{name}' cleared" });
    }
}
