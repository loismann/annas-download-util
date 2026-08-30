using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Spotify;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Spotifinator's chat route — the one Spotify route that spends money.
///
/// <para>Split out of <see cref="SpotifyEndpoints"/> when the allowance check was
/// added. Everything else under <c>/api/spotify</c> talks to Spotify's own API,
/// which this household pays a flat nothing for; this route sends the message to a
/// model and is billed per token against the caller's monthly cap. A route with a
/// different cost model and a different guard belongs in its own file.</para>
/// </summary>
public static class SpotifyConversationEndpoints
{
    public static WebApplication MapSpotifyConversationEndpoints(this WebApplication app)
    {
        // Any signed-in person. The conversation resolves its own data through
        // GetRequiredOwnerKey(), so a request only ever reaches the caller's own
        // Spotify connection, drafts and plans.
        app.MapPost("/api/spotify/command", HandleCommand)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleCommand(
        [FromBody] SpotifyConversationRequest request,
        [FromServices] ISpotifyConversationService conversation,
        [FromServices] IConfiguration cfg,
        [FromServices] ITokenUsageService tokenUsage,
        HttpContext context,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request?.Message))
            return ApiResponse.BadRequest("Message is required.");

        // This conversation bills the caller's monthly AI allowance, so it enforces it.
        // It did not, which meant somebody past their cap was refused by every other AI
        // feature and could keep spending here — see AiSpendGateTests.
        if (TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context) is { } overLimit)
            return overLimit;

        try
        {
            return Results.Ok(await conversation.HandleAsync(request, token));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Spotify] Command failed");
            return SpotifyEndpoints.MapFailure(ex, context);
        }
    }
}
