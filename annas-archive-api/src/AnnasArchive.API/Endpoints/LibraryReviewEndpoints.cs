using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping the daily library-review modal's endpoints.
/// Admin-only — this is Paul's personal cull/genre triage flow over "Paul's Books".
/// </summary>
public static class LibraryReviewEndpoints
{
    public static WebApplication MapLibraryReviewEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/library/review")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        // GET /api/library/review/status - Current phase, whether to auto-show, remaining count
        group.MapGet("/status", HandleGetStatus);

        // POST /api/library/review/session/start - Start today's batch, or resume it
        group.MapPost("/session/start", HandleStartSession);

        // POST /api/library/review/decision - Record a keep/delete/genreSet decision for one book
        group.MapPost("/decision", HandleDecision);

        return app;
    }

    private static IResult HandleGetStatus(HttpContext context, ILibraryReviewService reviewService)
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Ok(reviewService.GetStatus(baseUrl));
    }

    private static IResult HandleStartSession(HttpContext context, ILibraryReviewService reviewService)
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Ok(reviewService.StartOrResumeSession(baseUrl));
    }

    private static async Task<IResult> HandleDecision(
        [FromBody] LibraryReviewDecisionRequest request,
        ILibraryReviewService reviewService)
    {
        if (string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.Decision))
            return Results.BadRequest(new { error = "fileName and decision are required." });

        var result = await reviewService.RecordDecisionAsync(request.FileName, request.Decision);
        if (!result.Success)
            return Results.BadRequest(new { error = result.Error ?? "Failed to record decision." });

        return Results.Ok(new { success = true });
    }
}
