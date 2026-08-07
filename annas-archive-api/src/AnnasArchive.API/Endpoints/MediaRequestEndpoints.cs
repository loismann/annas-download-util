using System.Text.Json.Nodes;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>Body for POST /api/media/tv/add — wraps the raw Sonarr lookup object
/// alongside an optional season-picker selection (null/empty = monitor everything).</summary>
public record AddSeriesRequest(JsonObject Series, List<int>? SelectedSeasons);

/// <summary>Body for POST /api/media/tv/update-seasons — for a series that's already
/// added, adds more monitored seasons instead of re-adding it from scratch.</summary>
public record UpdateSeasonsRequest(int SeriesId, List<int> SelectedSeasons);

/// <summary>One row of a bulk-import list — Title+Year are used to find the movie in
/// Radarr (TMDB-backed lookup), Genres/Owner classify it in our own metadata once
/// matched. Owner must be a household member name (see HouseholdOwners.Names) — same
/// constraint as the single-item metadata editor, not free text.</summary>
public record BulkImportMovieRow(string Title, int? Year, List<string>? Genres, string? Owner);

/// <summary>When <paramref name="DateNightPool"/> is set, movies are registered as
/// catalog records only — unmonitored, no search, tagged <c>date-night-pool</c> — so a
/// 300-title list can be added without any of it downloading. See
/// DOCS/features/DATE_NIGHT.md.</summary>
public record BulkImportMoviesRequest(List<BulkImportMovieRow> Rows, bool DateNightPool = false);

/// <summary>Status is one of: added, already-existed, not-found, ambiguous, invalid, error.</summary>
public record BulkImportMovieResult(string Title, int? Year, string Status, string? Message, int? MovieId);

/// <summary>
/// TV/movie search-and-acquire endpoints — a thin proxy in front of Sonarr
/// and Radarr's own REST APIs, so the frontend gets one consistent app
/// (matching the book-search flow) instead of linking out to their separate
/// dashboards.
/// </summary>
public static class MediaRequestEndpoints
{
    public static WebApplication MapMediaRequestEndpoints(this WebApplication app)
    {
        app.MapGet("/api/media/tv/search", HandleTvSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/media/tv/add", HandleTvAdd)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/tv/library", HandleGetTvLibrary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/media/tv/update-seasons", HandleUpdateTvSeasons)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/movies/search", HandleMovieSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/media/movies/add", HandleMovieAdd)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // AdminOnly: this is a library-administration tool that can add hundreds of
        // movies in one call, and its DateNightPool flag would reveal a feature that
        // isn't meant to be visible yet.
        app.MapPost("/api/media/movies/bulk-import", HandleMovieBulkImport)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapGet("/api/media/queue", HandleGetQueue)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleTvSearch([FromQuery] string? term, ISonarrService sonarr)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Results.BadRequest(new { error = "term is required." });

        try
        {
            var results = await sonarr.LookupSeriesAsync(term);
            return Results.Ok(results);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Sonarr search failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleTvAdd(
        [FromBody] AddSeriesRequest request, ISonarrService sonarr, IMediaMetadataService metadata, HttpContext context)
    {
        try
        {
            var added = await sonarr.AddSeriesAsync(request.Series, request.SelectedSeasons);
            if (added["id"] is not null)
            {
                MediaOwnership.AssignToCaller(
                    metadata, "tv", added["id"]!.GetValue<int>().ToString(), context, "TV add");
            }
            return Results.Ok(added);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Sonarr add failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr rejected the request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleGetTvLibrary(ISonarrService sonarr)
    {
        try
        {
            var series = await sonarr.GetAllSeriesAsync();
            return Results.Ok(series);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Sonarr library fetch failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleUpdateTvSeasons([FromBody] UpdateSeasonsRequest request, ISonarrService sonarr)
    {
        try
        {
            var updated = await sonarr.UpdateSeriesSeasonsAsync(request.SeriesId, request.SelectedSeasons);
            return Results.Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Sonarr update-seasons failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr rejected the request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleMovieSearch([FromQuery] string? term, IRadarrService radarr)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Results.BadRequest(new { error = "term is required." });

        try
        {
            var results = await radarr.LookupMoviesAsync(term);
            return Results.Ok(results);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Radarr search failed: {Message}", ex.Message);
            return Results.Json(new { error = "Radarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleMovieAdd(
        [FromBody] JsonObject movie, IRadarrService radarr, IMediaMetadataService metadata, HttpContext context)
    {
        try
        {
            var added = await radarr.AddMovieAsync(movie);
            if (added["id"] is not null)
            {
                MediaOwnership.AssignToCaller(
                    metadata, "movie", added["id"]!.GetValue<int>().ToString(), context, "movie add");
            }
            return Results.Ok(added);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Radarr add failed: {Message}", ex.Message);
            return Results.Json(new { error = "Radarr rejected the request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Processes a structured list (title/year/genres/owner per row) in one
    /// pass: looks each title+year up against Radarr's TMDB-backed lookup, adds it if
    /// not already present, then tags owner/genres — merged into whatever metadata the
    /// movie already had, never overwriting it (an "already-existed" movie may already
    /// carry owners/genres from before; a full replace here would silently wipe them).
    /// Ambiguous (multiple matches) or unmatched rows are skipped, not guessed at —
    /// reported back in the per-row result for manual follow-up instead.
    ///
    /// In Date Night pool mode every movie is additionally tagged and registered
    /// catalog-only (see MovieAddMode.CatalogOnly). Movies that were *already* in
    /// Radarr get the pool tag but keep their existing monitoring state — an existing
    /// movie may be one the household is actively acquiring for ordinary reasons, and
    /// silently unmonitoring it because it also appears on a B-movie list would be a
    /// surprising side effect of importing a list.</summary>
    private static async Task<IResult> HandleMovieBulkImport(
        [FromBody] BulkImportMoviesRequest request, IRadarrService radarr, IMediaMetadataService metadata,
        HttpContext context)
    {
        var results = new List<BulkImportMovieResult>();

        // A row without an Owner column used to import untagged, which is how eleven
        // B-movies and five animated films ended up in the library owned by nobody.
        // The person running the import is the obvious default, and is exactly what
        // the single-title add already records.
        var importer = MediaOwnership.ResolveMember(context);
        if (importer is null)
            Log.Warning("[MediaRequest] Bulk import has no resolvable household member — " +
                "rows without an Owner column will import unowned");

        var addMode = request.DateNightPool ? MovieAddMode.CatalogOnly : MovieAddMode.MonitorAndSearch;
        int[]? poolTag = null;
        if (request.DateNightPool)
        {
            try
            {
                poolTag = [await radarr.EnsureTagAsync(DateNight.PoolTag)];
            }
            catch (Exception ex)
            {
                Log.Warning("[MediaRequest] Could not resolve the '{Tag}' tag: {Message}", DateNight.PoolTag, ex.Message);
                return Results.Json(
                    new { error = $"Could not create the '{DateNight.PoolTag}' tag in Radarr — nothing was imported." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }

        foreach (var row in request.Rows)
        {
            var title = row.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                results.Add(new BulkImportMovieResult(row.Title ?? "", row.Year, "invalid", "Title is required", null));
                continue;
            }

            List<string> owners = new();
            if (!string.IsNullOrWhiteSpace(row.Owner))
            {
                var owner = row.Owner.Trim();
                if (!HouseholdOwners.Names.Contains(owner, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(new BulkImportMovieResult(title, row.Year, "invalid",
                        $"Owner '{row.Owner}' must be one of {string.Join(", ", HouseholdOwners.Names)}", null));
                    continue;
                }
                owners.Add(owner);
            }
            else if (importer is not null)
            {
                owners.Add(importer);
            }

            var genres = (row.Genres ?? new List<string>())
                .Select(g => g.Trim())
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            try
            {
                var candidates = await radarr.LookupMoviesAsync(title);
                var matches = candidates.OfType<JsonObject>().ToList();
                if (row.Year is int year)
                    matches = matches.Where(c => (int?)c["year"] == year).ToList();

                if (matches.Count == 0)
                {
                    results.Add(new BulkImportMovieResult(title, row.Year, "not-found", "No matching movie found", null));
                    continue;
                }
                if (matches.Count > 1)
                {
                    results.Add(new BulkImportMovieResult(title, row.Year, "ambiguous",
                        $"{matches.Count} matches found — add manually", null));
                    continue;
                }

                var match = matches[0];
                var existingId = (int?)match["id"];
                int movieId;
                string status;

                if (existingId is int id && id > 0)
                {
                    movieId = id;
                    status = "already-existed";
                    if (poolTag is not null)
                        await radarr.EditMoviesAsync([movieId], addTagIds: poolTag);
                }
                else
                {
                    var added = await radarr.AddMovieAsync(match, addMode, poolTag);
                    movieId = added["id"]!.GetValue<int>();
                    status = "added";
                }

                if (owners.Count > 0 || genres.Count > 0)
                {
                    var existing = metadata.Get("movie", movieId.ToString());
                    var mergedOwners = (existing?.Owners ?? new List<string>())
                        .Concat(owners).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var mergedGenres = (existing?.Genres ?? new List<string>())
                        .Concat(genres).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    metadata.Set("movie", movieId.ToString(), new MediaItemMetadata(mergedOwners, mergedGenres));
                }

                results.Add(new BulkImportMovieResult(title, row.Year, status, null, movieId));
            }
            catch (Exception ex)
            {
                Log.Warning("[MediaRequest] Bulk import row '{Title}' ({Year}) failed: {Message}", title, row.Year, ex.Message);
                results.Add(new BulkImportMovieResult(title, row.Year, "error", ex.Message, null));
            }
        }

        Log.Information("[MediaRequest] Bulk import: {Total} rows, {Added} added, {Existed} already existed, {Failed} failed/skipped",
            results.Count,
            results.Count(r => r.Status == "added"),
            results.Count(r => r.Status == "already-existed"),
            results.Count(r => r.Status is "not-found" or "ambiguous" or "invalid" or "error"));

        return Results.Ok(results);
    }

    private static async Task<IResult> HandleGetQueue(ISonarrService sonarr, IRadarrService radarr)
    {
        try
        {
            var tvQueue = await sonarr.GetQueueAsync();
            var movieQueue = await radarr.GetQueueAsync();
            return Results.Ok(new { tv = tvQueue, movies = movieQueue });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Queue fetch failed: {Message}", ex.Message);
            return Results.Json(new { error = "Queue temporarily unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
