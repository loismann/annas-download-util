using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping book search-related endpoints.
/// </summary>
public static class BookSearchEndpoints
{
    /// <summary>
    /// Maps book search endpoints to the application.
    /// </summary>
    public static WebApplication MapBookSearchEndpoints(this WebApplication app)
    {
        // Main book search endpoint
        app.MapGet("/api/anna/book", HandleBookSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Download status endpoint
        app.MapGet("/api/anna/download-status", HandleGetDownloadStatus)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Cover lookup endpoint (title/author search via OpenLibrary/Google Books)
        app.MapGet("/api/anna/book/cover", HandleCoverLookup)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Cover lookup by MD5 — extracts ISBN from Anna's Archive's detail
        // page, resolves via OpenLibrary's cover CDN. Independent of both the
        // OpenLibrary search API and Google Books' quota, so it works even
        // when those are down/exhausted. Meant to be called lazily per-book
        // after search results render, not during search itself.
        app.MapGet("/api/anna/book/{md5}/cover-by-isbn", HandleCoverByIsbn)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Description endpoints
        app.MapGet("/api/anna/book/description/google-books", HandleGoogleBooksDescription)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/anna/book/description/openlibrary", HandleOpenLibraryDescription)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Real, non-AI description source that's actually still working —
        // Google Books' quota has been exhausted and OpenLibrary's search
        // API has been down for a while, so in practice both of the above
        // almost always fall straight through to GPT today. This gives the
        // same real-data fallback already used by the AI Related Books flow
        // to the normal search results' "Retrieve Summary" button too.
        app.MapGet("/api/anna/book/description/wikipedia", HandleWikipediaDescription)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Health check endpoints (no auth required)
        app.MapGet("/api/anna/slum-health", HandleSlumHealth);
        app.MapGet("/api/anna/mirror-health", HandleMirrorHealth);

        return app;
    }

    private static async Task<IResult> HandleBookSearch(
        [FromQuery] string? name,
        AnnaArchiveService svc,
        IValidationService validation,
        IConfiguration cfg,
        [FromQuery] bool exact = false,
        [FromQuery] int page = 1)
    {
        if (!validation.IsValidSearchQuery(name))
            return Results.BadRequest(new {
                error = "Query parameter 'name' is required and must be between 1 and 500 characters."
            });

        try
        {
            // Deliberately smaller than the old single-shot Anna:SearchLimit
            // (50) — the frontend fetches page 1 first for a fast initial
            // render, then fetches page 2 in the background and appends,
            // rather than blocking one response on the full ~50-result
            // budget. Same total results delivered, just progressively.
            var pageBatchSize = cfg.GetValue<int>("Anna:SearchPageBatchSize", 25);
            var books = (await svc.SearchAsync(name, pageBatchSize, exact, startPage: page)).ToList();

            if (exact)
                books = books
                    .Where(b => string.Equals(b.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();

            return books.Any()
                ? books.Count == 1 ? Results.Ok(books[0]) : Results.Ok(books)
                : ApiResponse.NotFound("No books found matching that name.");
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("Book search failed due to external service error: {Message}", ex.Message);
            return Results.Json(
                new { error = "External search service unavailable", details = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Log.Warning("Book search timed out: {Message}", ex.Message);
            return Results.Json(
                new { error = "Search request timed out" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleGoogleBooksDescription(
        [FromQuery] string? title,
        [FromQuery] string? author,
        IGoogleBooksService googleBooks)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new { error = "title is required." });

        Log.Information("Google Books description lookup: title='{title}', author='{author}'");
        var description = await googleBooks.GetBookDescriptionAsync(title, author ?? "");

        Log.Information(description is null
            ? $"Google Books description not found for '{title}'"
            : $"Google Books description found for '{title}'");

        return Results.Ok(new { description });
    }

    private static async Task<IResult> HandleOpenLibraryDescription(
        [FromQuery] string? title,
        [FromQuery] string? author,
        IOpenLibraryService openLibrary)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new { error = "title is required." });

        Log.Information("OpenLibrary description lookup: title='{title}', author='{author}'");
        var description = await openLibrary.GetBookDescriptionAsync(title, author ?? "");

        Log.Information(description is null
            ? $"OpenLibrary description not found for '{title}'"
            : $"OpenLibrary description found for '{title}'");

        return Results.Ok(new { description });
    }

    private static async Task<IResult> HandleWikipediaDescription(
        [FromQuery] string? title,
        [FromQuery] string? author,
        IWikipediaService wikipedia)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new { error = "title is required." });

        Log.Information("Wikipedia description lookup: title='{title}', author='{author}'");
        var description = await wikipedia.GetBookDescriptionAsync(title, author);

        Log.Information(description is null
            ? $"Wikipedia description not found for '{title}'"
            : $"Wikipedia description found for '{title}'");

        return Results.Ok(new { description });
    }

    private static async Task<IResult> HandleCoverLookup(
        [FromQuery] string? title,
        [FromQuery] string? author,
        ICoverLookupService coverLookupService)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new { error = "title is required." });

        Log.Information("Cover lookup: title='{Title}', author='{Author}'", title, author);
        var result = await coverLookupService.GetCoverAsync(title, author);

        if (result.CoverUrl is null)
            Log.Information("Cover lookup failed for '{Title}'", title);
        else
            Log.Information("Cover lookup found for '{Title}' from {Source}: {CoverUrl}", title, result.Source, result.CoverUrl);

        return Results.Ok(new { coverUrl = result.CoverUrl });
    }

    private static async Task<IResult> HandleCoverByIsbn(
        [FromRoute] string md5,
        AnnaArchiveService annaService,
        IValidationService validation)
    {
        if (!validation.IsValidMd5(md5))
            return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

        var coverUrl = await annaService.GetCoverByMd5Async(md5);
        return Results.Ok(new { coverUrl });
    }

    private static IResult HandleGetDownloadStatus(IDownloadTrackingService downloadTracking)
    {
        try
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var acctInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);

            return Results.Ok(new { accountFastInfo = acctInfo });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            return Results.Ok(new { accountFastInfo = (AccountFastDownloadInfoDto?)null, error = ex.Message });
        }
    }

    private static async Task<IResult> HandleSlumHealth(IHttpClientFactory httpFactory)
    {
        try
        {
            using var http = httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            Log.Information("[slum-health] Fetching status page data...");
            var statusResponse = await http.GetAsync("https://open-slum.org/api/status-page/slum");
            if (!statusResponse.IsSuccessStatusCode)
            {
                Log.Information("[slum-health] Failed to fetch status page: {statusResponse.StatusCode}");
                return Results.Json(new { success = false, error = "Failed to fetch status page data" });
            }

            Log.Information("[slum-health] Fetching heartbeat data...");
            var heartbeatResponse = await http.GetAsync("https://open-slum.org/api/status-page/heartbeat/slum");
            if (!heartbeatResponse.IsSuccessStatusCode)
            {
                Log.Information("[slum-health] Failed to fetch heartbeat: {heartbeatResponse.StatusCode}");
                return Results.Json(new { success = false, error = "Failed to fetch heartbeat data" });
            }

            var statusJson = await statusResponse.Content.ReadAsStringAsync();
            var heartbeatJson = await heartbeatResponse.Content.ReadAsStringAsync();

            using var statusDoc = JsonDocument.Parse(statusJson);
            using var heartbeatDoc = JsonDocument.Parse(heartbeatJson);

            // Build result array with Anna's Archive monitors
            var result = new List<object>();
            var heartbeats = heartbeatDoc.RootElement.GetProperty("heartbeatList");

            // Find Anna's Archive group
            if (statusDoc.RootElement.TryGetProperty("publicGroupList", out var groups))
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.TryGetProperty("name", out var groupName) &&
                        groupName.GetString()?.Contains("Anna's Archive") == true)
                    {
                        foreach (var monitor in group.GetProperty("monitorList").EnumerateArray())
                        {
                            var name = monitor.GetProperty("name").GetString() ?? "";
                            var id = monitor.GetProperty("id").GetInt32().ToString();

                            // Calculate health percentage from heartbeats
                            double health = 0;
                            if (heartbeats.TryGetProperty(id, out var monitorHeartbeats))
                            {
                                int upCount = 0, totalCount = 0;
                                foreach (var heartbeat in monitorHeartbeats.EnumerateArray())
                                {
                                    totalCount++;
                                    if (heartbeat.GetProperty("status").GetInt32() == 1) upCount++;
                                }
                                health = totalCount > 0 ? Math.Round((double)upCount / totalCount * 100, 2) : 0;
                            }

                            // Get cert expiry
                            int? certExpDays = null;
                            if (monitor.TryGetProperty("certExpiryDaysRemaining", out var cert))
                            {
                                certExpDays = cert.GetInt32();
                            }

                            result.Add(new
                            {
                                name = name,
                                health = $"{health}%",
                                cert_exp = certExpDays.HasValue ? $"{certExpDays} days" : null
                            });
                        }
                        break;
                    }
                }
            }

            Log.Information("[slum-health] Returning {MonitorCount} monitors", result.Count);
            return Results.Json(result);
        }
        catch (ArgumentException ex)
        {
            Log.Information("[slum-health] Invalid argument: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("[slum-health] Error: {ErrorMessage}", ex.Message);
            return Results.Json(new { success = false, error = ex.Message });
        }
    }

    private const string MirrorHealthCacheKey = "mirror-health";
    private static readonly TimeSpan MirrorHealthCacheDuration = TimeSpan.FromSeconds(60);

    private static async Task<IResult> HandleMirrorHealth(IHttpClientFactory httpFactory, IMemoryCache cache)
    {
        // Mirror reachability doesn't change on a per-request basis — this
        // endpoint has no auth/rate-limit gate (it's polled by the search
        // page's status badges), so without a cache it was doing 3 live,
        // sequential, up-to-2-attempt HTTP round trips on every single call.
        if (cache.TryGetValue(MirrorHealthCacheKey, out List<object>? cachedResults))
            return Results.Json(cachedResults);

        var client = httpFactory.CreateClient();
        client.Timeout = HttpTimeouts.ShortScraperTimeout;

        // Sourced from AnnaArchiveService.BaseDomains so this health check always
        // reflects the exact domains actually used for search/download, rather
        // than a second hardcoded list that can silently drift out of sync.
        var mirrors = AnnaArchiveService.BaseDomains
            .Select(domain =>
            {
                var host = new Uri(domain).Host;
                var extension = host[(host.LastIndexOf('.') + 1)..];
                return (extension, domain);
            })
            .ToArray();

        var results = new List<object>();

        foreach (var (extension, url) in mirrors)
        {
            // Up to 2 attempts — a single transient timeout/connection blip on an
            // otherwise-healthy domain shouldn't leave its dashboard badge stuck
            // at "unknown" for an entire page load with no chance to recover.
            const int maxAttempts = 2;
            var sw = Stopwatch.StartNew();
            object? result = null;

            for (var attempt = 1; attempt <= maxAttempts && result == null; attempt++)
            {
                try
                {
                    using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    var statusCode = (int)resp.StatusCode;
                    var ok = statusCode >= 200 && statusCode < 500;
                    result = new
                    {
                        name = $"Anna's Archive {extension.ToUpperInvariant()}",
                        extension,
                        health = ok ? 100 : 0,
                        statusCode,
                        responseMs = sw.ElapsedMilliseconds
                    };
                }
                catch (ArgumentException ex) when (attempt == maxAttempts)
                {
                    result = new
                    {
                        name = $"Anna's Archive {extension.ToUpperInvariant()}",
                        extension,
                        health = (int?)null,
                        statusCode = (int?)null,
                        responseMs = sw.ElapsedMilliseconds,
                        error = $"ArgumentException: {ex.ParamName ?? "unknown"}"
                    };
                }
                catch (Exception ex) when (attempt == maxAttempts)
                {
                    result = new
                    {
                        name = $"Anna's Archive {extension.ToUpperInvariant()}",
                        extension,
                        health = (int?)null,
                        statusCode = (int?)null,
                        responseMs = sw.ElapsedMilliseconds,
                        error = ex.GetType().Name
                    };
                }
                catch
                {
                    // Not the final attempt — swallow and retry.
                }
            }

            sw.Stop();
            results.Add(result!);
        }

        cache.Set(MirrorHealthCacheKey, results, MirrorHealthCacheDuration);
        return Results.Json(results);
    }
}
