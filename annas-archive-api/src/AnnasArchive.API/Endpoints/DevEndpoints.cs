namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping development-only endpoints.
/// </summary>
public static class DevEndpoints
{
    /// <summary>
    /// Maps development helper endpoints (only available in DEBUG builds).
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

        return app;
    }
}
