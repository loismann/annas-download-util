using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping AI Usage tracking endpoints.
/// </summary>
public static class AiUsageEndpoints
{
    /// <summary>
    /// Maps AI Usage endpoints to the application.
    /// </summary>
    public static WebApplication MapAiUsageEndpoints(this WebApplication app)
    {
        // GET /api/ai/usage - Get current user's token usage
        app.MapGet("/api/ai/usage", HandleGetUsage)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/ai/usage/all-users - Get all users' usage
        app.MapGet("/api/ai/usage/all-users", HandleGetAllUsersUsage)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/usage/reset - Reset usage counter
        app.MapPost("/api/ai/usage/reset", HandleResetUsage)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleGetUsage(HttpContext context, IConfiguration cfg, ITokenUsageService tokenUsage)
    {
        var userId = UserHelpers.GetUserIdFromContext(context);
        if (userId == null)
            return Results.Unauthorized();

        var (promptTokens, completionTokens, totalTokens) = tokenUsage.GetTotals(userId);
        var allowanceUsd = cfg.GetValue<double?>("OpenAI:PerUserMonthlyCostAllowanceUsd") ?? 20.0;
        var costUsd = tokenUsage.CalculateCostUsd(promptTokens, completionTokens);
        var allowanceUsedPercent = (costUsd / allowanceUsd) * 100.0;
        var remaining = Math.Max(0, allowanceUsd - costUsd);

        var now = DateTime.UtcNow;
        var nextReset = new DateTime(now.Year, now.Month, 1).AddMonths(1);

        var resp = new TokenUsageResponse(
            promptTokens,
            completionTokens,
            totalTokens,
            null, // No longer using token-based allowance
            allowanceUsedPercent,
            null, // No longer using token-based remaining
            nextReset,
            costUsd
        );

        return Results.Ok(resp);
    }

    private static IResult HandleGetAllUsersUsage(IConfiguration cfg, ITokenUsageService tokenUsage)
    {
        var allowanceUsd = cfg.GetValue<double?>("OpenAI:PerUserMonthlyCostAllowanceUsd") ?? 20.0;
        var userDisplayNames = UserHelpers.GetUserDisplayNames(cfg);

        var now = DateTime.UtcNow;
        var nextReset = new DateTime(now.Year, now.Month, 1).AddMonths(1);

        // Return usage for ALL configured users (from appsettings.json), even if they have $0.00 usage
        var result = userDisplayNames.Select(kvp =>
        {
            var userId = kvp.Key;
            var displayName = kvp.Value;

            // Get totals for this user (will return 0,0,0 if no usage file exists yet)
            var (promptTokens, completionTokens, totalTokens) = tokenUsage.GetTotals(userId);
            var costUsd = tokenUsage.CalculateCostUsd(promptTokens, completionTokens);
            var allowanceUsedPercent = (costUsd / allowanceUsd) * 100.0;

            return new UserTokenUsage(
                userId,
                displayName,
                promptTokens,
                completionTokens,
                totalTokens,
                costUsd,
                allowanceUsd,
                allowanceUsedPercent,
                nextReset,
                costUsd >= allowanceUsd
            );
        }).ToList();

        return Results.Ok(result);
    }

    private static IResult HandleResetUsage(ITokenUsageService tokenUsage)
    {
        tokenUsage.Reset();
        Log.Information("Token usage counter has been reset");
        return Results.Ok(new { success = true, message = "Token usage counter has been reset" });
    }
}
