using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.Core.Helpers;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serilog;

namespace AnnasArchive.API.Endpoints;

public static class SpotifyEndpoints
{
    public static WebApplication MapSpotifyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/spotify")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        group.MapGet("/connection", HandleGetConnection);
        group.MapPost("/connection/authorize", HandleAuthorize);
        group.MapDelete("/connection", HandleDisconnect);
        group.MapGet("/search", HandleSearch);
        group.MapGet("/playlists", HandleGetPlaylists);
        group.MapPost("/command", HandleCommand);

        app.MapGet("/api/spotify/oauth/callback", HandleOAuthCallback)
            .AllowAnonymous()
            .RequireRateLimiting("login");

        return app;
    }

    private static IResult HandleGetConnection(
        ISpotifyAuthorizationService authorization,
        ISpotifyCurrentUser currentUser) =>
        Results.Ok(authorization.GetStatus(currentUser.GetRequiredOwnerKey()));

    private static IResult HandleAuthorize(
        SpotifyAuthorizeRequest? request,
        ISpotifyAuthorizationService authorization,
        ISpotifyCurrentUser currentUser)
    {
        try
        {
            var uri = authorization.CreateAuthorizationUri(
                currentUser.GetRequiredOwnerKey(),
                request?.ForceDialog ?? false);
            return Results.Ok(new SpotifyAuthorizeResponse(uri.AbsoluteUri));
        }
        catch (Exception ex)
        {
            return MapFailure(ex);
        }
    }

    private static IResult HandleDisconnect(
        ISpotifyAuthorizationService authorization,
        ISpotifyCurrentUser currentUser)
    {
        authorization.Disconnect(currentUser.GetRequiredOwnerKey());
        return Results.NoContent();
    }

    private static async Task<IResult> HandleOAuthCallback(
        string? state,
        string? code,
        string? error,
        ISpotifyAuthorizationService authorization,
        IOptions<SpotifyConfiguration> config,
        CancellationToken token)
    {
        string result;
        try
        {
            var completion = await authorization.CompleteAuthorizationAsync(state, code, error, token);
            result = completion.Succeeded ? "connected" : completion.Error ?? "authorization_failed";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Spotify] OAuth callback failed");
            result = ex is SpotifyConnectionException connectionException
                ? connectionException.State
                : "authorization_failed";
        }

        return Results.Redirect(BuildCallbackRedirect(result, config.Value.FrontendBaseUrl));
    }

    /// <summary>
    /// Where the browser lands after Spotify's callback. The <c>?spotify=</c> result
    /// must sit in a real query string, which is only true because the app serves
    /// path-based URLs. Under the previous hash routing this same value landed
    /// before the <c>#</c>, where Angular's router could not see it — a successful
    /// connection and a failed one both looked like silently landing on the wrong
    /// page. Reintroducing <c>#/</c> here would restore that bug.
    /// </summary>
    public static string BuildCallbackRedirect(string result, string? frontendBaseUrl)
    {
        var path = $"/spotifinator?spotify={Uri.EscapeDataString(result)}";
        var trimmedBase = frontendBaseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmedBase) ? path : trimmedBase + path;
    }

    private static async Task<IResult> HandleSearch(
        string q,
        int? limit,
        ISpotifyService spotifyService,
        HttpContext context,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(new { error = "Search query 'q' is required." });

        try
        {
            var results = await spotifyService.SearchTracksAsync(q, limit ?? 10, token);
            return Results.Ok(results);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Spotify] Search failed for query: {Query}", q);
            return MapFailure(ex, context);
        }
    }

    private static async Task<IResult> HandleGetPlaylists(
        ISpotifyService spotifyService,
        HttpContext context,
        CancellationToken token)
    {
        try
        {
            var playlists = await spotifyService.GetUserPlaylistsAsync(token);
            return Results.Ok(playlists);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Spotify] Failed to get playlists");
            return MapFailure(ex, context);
        }
    }

    private static async Task<IResult> HandleCreatePlaylist(
        CreatePlaylistRequest request,
        ISpotifyService spotifyService,
        HttpContext context,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return Results.BadRequest(new { error = "Playlist name is required." });

        try
        {
            var playlist = await spotifyService.CreatePlaylistAsync(
                request.Name,
                request.Description,
                request.Public,
                token);

            return Results.Ok(playlist);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Spotify] Failed to create playlist: {Name}", request?.Name);
            return MapFailure(ex, context);
        }
    }

    private static async Task<IResult> HandleAddTracks(
        string playlistId,
        [FromBody] AddTracksRequest request,
        [FromServices] ISpotifyService spotifyService,
        HttpContext context,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            return Results.BadRequest(new { error = "Playlist ID is required." });

        if (request?.TrackUris == null || request.TrackUris.Count == 0)
            return Results.BadRequest(new { error = "At least one track URI is required." });

        try
        {
            await spotifyService.AddTracksToPlaylistAsync(playlistId, request.TrackUris, token);
            return Results.Ok(new { success = true, added = request.TrackUris.Count });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Spotify] Failed to add tracks to playlist: {PlaylistId}", playlistId);
            return MapFailure(ex, context);
        }
    }

    private static async Task<IResult> HandleRemoveTracks(
        string playlistId,
        [FromBody] AddTracksRequest request,
        [FromServices] ISpotifyService spotifyService,
        HttpContext context,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            return Results.BadRequest(new { error = "Playlist ID is required." });

        if (request?.TrackUris == null || request.TrackUris.Count == 0)
            return Results.BadRequest(new { error = "At least one track URI is required." });

        try
        {
            await spotifyService.RemoveTracksFromPlaylistAsync(playlistId, request.TrackUris, token);
            return Results.Ok(new { success = true, removed = request.TrackUris.Count });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Spotify] Failed to remove tracks from playlist: {PlaylistId}", playlistId);
            return MapFailure(ex, context);
        }
    }

    // ─── AI Command Processing ───────────────────────────────────────────────────

    private static async Task<IResult> HandleCommand(
        [FromBody] SpotifyCommandRequest request,
        [FromServices] ISpotifyService spotifyService,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IConfiguration config,
        [FromServices] IModelSelectionService modelSelection,
        [FromServices] IOpenAiModelHelper modelHelper,
        HttpContext context,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request?.Message))
            return Results.BadRequest(new { error = "Message is required." });

        try
        {
            // Parse the command using OpenAI
            var parsed = await ParseCommandAsync(request.Message, request.Context, httpClientFactory, config, token);

            Log.Information("[Spotify] Parsed command: {Action} (confidence: {Confidence})",
                parsed.Action, parsed.Confidence);

            // Execute the action and build response
            var (data, naturalResponse) = await ExecuteCommandAsync(
                parsed, spotifyService, httpClientFactory, config, modelSelection, modelHelper, token);

            return Results.Ok(new SpotifyCommandResponse(parsed, naturalResponse, data));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Spotify] Command processing failed for: {Message}", request.Message);
            return MapFailure(ex, context);
        }
    }

    private static async Task<ParsedSpotifyCommand> ParseCommandAsync(
        string message,
        string? context,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        CancellationToken token)
    {
        var apiKey = config["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");

        var systemPrompt = """
            You are a Spotify assistant that parses user commands into structured actions.

            Available actions:
            - search_tracks: Search for songs/tracks. Extract the search query.
            - list_playlists: Show the user's playlists.
            - create_playlist: Create a new playlist. Extract the name and optional description.
            - add_tracks: Add songs to a playlist. Extract the search query for the song(s) AND the playlist name.
            - remove_tracks: Remove songs from a playlist. Extract the search query for the song(s) AND the playlist name.
            - generate_playlist: Generate a playlist based on a vibe/mood/theme description. This is for requests like "make me a playlist that feels like..." or "find songs that would fit..." or "create a playlist with songs for...".
            - unknown: When you can't determine the action.

            Respond with JSON only, no markdown:
            {
              "action": "action_name",
              "searchQuery": "song/artist to search for (for search_tracks, add_tracks, remove_tracks)",
              "playlistName": "playlist name (for create_playlist, add_tracks, remove_tracks, generate_playlist)",
              "description": "optional description for create_playlist",
              "confidence": 0.0-1.0,
              "clarificationNeeded": "optional question if unclear",
              "vibeDescription": "the mood/vibe/theme description (for generate_playlist)",
              "trackCount": number of tracks requested (default 20 for generate_playlist),
              "readyToGenerate": true if enough context to generate, false if clarifying questions needed,
              "clarifyingQuestions": ["question1", "question2"] if readyToGenerate is false
            }

            For generate_playlist:
            - If the request is vague, set readyToGenerate=false and provide 2-3 clarifying questions to refine the vibe
            - Questions should help narrow down: era/decade, energy level, instrumental vs vocals, specific sub-genres, or emotional tone
            - If there's enough detail OR the context includes previous answers, set readyToGenerate=true
            - Extract a suggested playlistName from the vibe (e.g., "Italian Cafe Vibes", "Rainy Sunday Morning")

            Examples:
            - "add Bohemian Rhapsody to my road trip playlist" -> action: add_tracks, searchQuery: "Bohemian Rhapsody", playlistName: "road trip"
            - "make me a playlist that feels like sitting in an Italian cafe" -> action: generate_playlist, vibeDescription: "sitting in a small cafe in the Italian countryside", readyToGenerate: false, clarifyingQuestions: ["Do you prefer classic Italian songs or more modern acoustic/indie?", "Should it be mostly instrumental or with vocals?", "How many songs would you like?"]
            - "find 30 songs perfect for a rainy Sunday morning reading session" -> action: generate_playlist, vibeDescription: "rainy Sunday morning reading session", trackCount: 30, playlistName: "Rainy Sunday Reading", readyToGenerate: true (specific enough)
            """;

        var userPrompt = context != null
            ? $"Conversation context:\n{context}\n\nNew message: {message}"
            : message;

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.3,
            max_tokens = 200
        };

        var client = httpClientFactory.CreateClient("OpenAI");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
            token);

        var content = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Spotify] OpenAI API error: {StatusCode} - {Content}", response.StatusCode, content);
            throw new InvalidOperationException($"OpenAI API error: {response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(content);
        var messageContent = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        // Clean up potential markdown code blocks
        messageContent = AiText.StripCodeFences(messageContent);

        var parsed = JsonSerializer.Deserialize<ParsedSpotifyCommand>(messageContent, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return parsed ?? new ParsedSpotifyCommand("unknown", Confidence: 0.0);
    }

    private static async Task<(object? Data, string NaturalResponse)> ExecuteCommandAsync(
        ParsedSpotifyCommand parsed,
        ISpotifyService spotifyService,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        IModelSelectionService modelSelection,
        IOpenAiModelHelper modelHelper,
        CancellationToken token)
    {
        switch (parsed.Action.ToLowerInvariant())
        {
            case "search_tracks":
                if (string.IsNullOrWhiteSpace(parsed.SearchQuery))
                    return (null, "What would you like to search for?");

                var searchResults = await spotifyService.SearchTracksAsync(parsed.SearchQuery, 10, token);
                var trackCount = searchResults.Tracks.Count;

                return trackCount > 0
                    ? (searchResults, $"Found {searchResults.Total} tracks for \"{parsed.SearchQuery}\". Here are the top {trackCount}:")
                    : (searchResults, $"No tracks found for \"{parsed.SearchQuery}\". Try a different search?");

            case "list_playlists":
                var playlists = await spotifyService.GetUserPlaylistsAsync(token);
                return playlists.Count > 0
                    ? (playlists, $"You have {playlists.Count} playlists:")
                    : (playlists, "You don't have any playlists yet.");

            case "create_playlist":
            case "add_tracks":
            case "remove_tracks":
            case "generate_playlist":
                return (null,
                    "Playlist changes are paused until Spotifinator's reviewed change-plan flow is complete. " +
                    "For now I can connect safely, search Spotify, and list playlist metadata without changing your account.");

            case "unknown":
            default:
                if (!string.IsNullOrWhiteSpace(parsed.ClarificationNeeded))
                    return (null, parsed.ClarificationNeeded);

                return (null, "I'm not sure what you want to do yet. In this phase you can:\n- Search for songs\n- Show your playlists\n- Check the Spotify connection\n\nPlaylist inspection arrives in Phase 3; reviewed changes arrive in Phase 6.");
        }
    }

    // ─── Vibe-Based Playlist Generation ──────────────────────────────────────────

    private static async Task<(object? Data, string NaturalResponse)> GenerateVibePlaylistAsync(
        string vibeDescription,
        string playlistName,
        int trackCount,
        ISpotifyService spotifyService,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        IModelSelectionService modelSelection,
        IOpenAiModelHelper modelHelper,
        CancellationToken token)
    {
        var apiKey = config["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");

        var model = modelSelection.GetModelDeep(); // Uses GPT-5.2 for deep reasoning
        Log.Information("[Spotify] Generating vibe playlist using model: {Model}", model);

        var systemPrompt = """
            You are an expert music curator with encyclopedic knowledge of songs across all genres, eras, and cultures.
            Your task is to create the PERFECT playlist that captures a specific vibe, mood, or atmosphere.

            Think deeply about:
            - The emotional qualities the user is seeking
            - Songs that evoke specific imagery, settings, or feelings
            - A mix of well-known tracks and hidden gems
            - Musical elements (tempo, instrumentation, vocals) that fit the vibe
            - How songs flow together to create a cohesive listening experience

            Return ONLY valid JSON, no markdown:
            {
              "playlistDescription": "A 2-3 sentence description of the playlist's mood and what makes it special",
              "songs": [
                {
                  "artist": "Artist Name",
                  "title": "Song Title",
                  "reason": "Brief explanation of why this song fits the vibe (1 sentence)"
                }
              ]
            }

            Rules:
            - Return EXACTLY the requested number of songs
            - Include a diverse mix: different artists, some classics, some lesser-known tracks
            - Every song MUST genuinely fit the requested vibe - no filler
            - Be specific with artist and song names (use official titles)
            - Consider the full listening experience - the playlist should flow well
            """;

        var userPrompt = $"""
            Create a playlist of exactly {trackCount} songs that perfectly captures this vibe:

            "{vibeDescription}"

            Take your time to think about what songs would truly transport someone to this feeling/place/mood.
            Consider the subtle nuances of the request and find songs that match not just the obvious interpretation,
            but the deeper emotional quality being sought.
            """;

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: 4000,
            temperature: 0.7,
            reasoningEffort: "high"
        );

        var client = httpClientFactory.CreateClient("OpenAI");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            token);

        var content = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Spotify] OpenAI vibe generation failed: {StatusCode} - {Content}", response.StatusCode, content);
            throw new InvalidOperationException($"AI generation failed: {response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(content);
        var messageContent = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        // Clean up potential markdown code blocks
        messageContent = AiText.StripCodeFences(messageContent);

        // Parse the AI response
        using var aiResponse = JsonDocument.Parse(messageContent);
        var playlistDescription = aiResponse.RootElement.TryGetProperty("playlistDescription", out var descProp)
            ? descProp.GetString() ?? ""
            : "";

        var songs = new List<GeneratedSongSuggestion>();
        if (aiResponse.RootElement.TryGetProperty("songs", out var songsArray) && songsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var song in songsArray.EnumerateArray())
            {
                var artist = song.TryGetProperty("artist", out var a) ? a.GetString() ?? "" : "";
                var title = song.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var reason = song.TryGetProperty("reason", out var r) ? r.GetString() : null;

                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                {
                    songs.Add(new GeneratedSongSuggestion(artist, title, reason));
                }
            }
        }

        Log.Information("[Spotify] AI suggested {Count} songs for vibe: {Vibe}", songs.Count, vibeDescription);

        // Search Spotify for each song and collect results
        var foundTracks = new List<SpotifyTrackDto>();
        var notFoundSongs = new List<string>();

        foreach (var song in songs)
        {
            try
            {
                var searchQuery = $"{song.Title} {song.Artist}";
                var searchResults = await spotifyService.SearchTracksAsync(searchQuery, 1, token);

                if (searchResults.Tracks.Count > 0)
                {
                    foundTracks.Add(searchResults.Tracks.First());
                }
                else
                {
                    notFoundSongs.Add($"{song.Artist} - {song.Title}");
                    Log.Information("[Spotify] Could not find: {Artist} - {Title}", song.Artist, song.Title);
                }
            }
            catch (Exception ex) when (ex is SpotifyApiException or SpotifyConnectionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Spotify] Error searching for: {Artist} - {Title}", song.Artist, song.Title);
                notFoundSongs.Add($"{song.Artist} - {song.Title}");
            }
        }

        Log.Information("[Spotify] Found {Found}/{Total} tracks on Spotify", foundTracks.Count, songs.Count);

        // Create the playlist and add tracks
        SpotifyPlaylistDto? createdPlaylist = null;
        if (foundTracks.Count > 0)
        {
            try
            {
                createdPlaylist = await spotifyService.CreatePlaylistAsync(
                    playlistName,
                    playlistDescription,
                    false,
                    token);

                var trackUris = foundTracks.Select(t => t.Uri).ToList();
                await spotifyService.AddTracksToPlaylistAsync(createdPlaylist.Id, trackUris, token);

                Log.Information("[Spotify] Created playlist '{Name}' with {Count} tracks", playlistName, foundTracks.Count);
            }
            catch (Exception ex) when (ex is SpotifyApiException or SpotifyConnectionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Spotify] Failed to create playlist: {Name}", playlistName);
            }
        }

        var result = new VibeGenerationResult(foundTracks, notFoundSongs, createdPlaylist);

        var responseText = createdPlaylist != null
            ? $"🎵 Created \"{playlistName}\" with {foundTracks.Count} tracks!\n\n{playlistDescription}"
            : $"Found {foundTracks.Count} tracks matching your vibe. Here's what I found:";

        if (notFoundSongs.Count > 0 && notFoundSongs.Count <= 5)
        {
            responseText += $"\n\n(Couldn't find {notFoundSongs.Count} songs on Spotify)";
        }

        return (result, responseText);
    }

    private static IResult MapFailure(Exception exception, HttpContext? context = null)
    {
        var (statusCode, state, reason, retryAfter, message) = exception switch
        {
            SpotifyApiException apiException => (
                (int)apiException.SpotifyStatusCode,
                apiException.IsQuotaExceeded ? "QuotaExceeded" :
                    apiException.SpotifyStatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? "RateLimited"
                        : "Connected",
                apiException.Reason,
                apiException.RetryAfter,
                apiException.SpotifyMessage ?? "Spotify rejected the request."),
            SpotifyConnectionException connectionException => (
                (int)connectionException.StatusCode,
                connectionException.State,
                (string?)null,
                connectionException.RetryAfter,
                connectionException.Message),
            UnauthorizedAccessException => (
                StatusCodes.Status401Unauthorized,
                "Disconnected",
                (string?)null,
                (TimeSpan?)null,
                "A logged-in administrator is required for Spotify."),
            _ => (
                StatusCodes.Status502BadGateway,
                "SpotifyUnavailable",
                (string?)null,
                (TimeSpan?)null,
                "The Spotify operation could not be completed.")
        };

        var retryAfterSeconds = retryAfter is { } delay
            ? Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds))
            : (int?)null;
        if (context != null && retryAfterSeconds.HasValue)
            context.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString();

        return Results.Json(
            new SpotifyConnectionErrorDto(message, state, reason, retryAfterSeconds),
            statusCode: statusCode);
    }
}
