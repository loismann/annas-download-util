using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

        // Cover lookup endpoint
        app.MapGet("/api/anna/book/cover", HandleCoverLookup)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Description endpoints
        app.MapGet("/api/anna/book/description/google-books", HandleGoogleBooksDescription)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/anna/book/description/openlibrary", HandleOpenLibraryDescription)
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
        [FromQuery] bool exact = false)
    {
        if (!validation.IsValidSearchQuery(name))
            return Results.BadRequest(new {
                error = "Query parameter 'name' is required and must be between 1 and 500 characters."
            });

        try
        {
            var searchLimit = cfg.GetValue<int>("Anna:SearchLimit", 25);
            var books = (await svc.SearchAsync(name, searchLimit, exact)).ToList();

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

    private static async Task<IResult> HandleCoverLookup(
        [FromQuery] string? title,
        [FromQuery] string? author,
        IOpenLibraryService openLibrary,
        IGoogleBooksService googleBooks)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new { error = "title is required." });

        Log.Information("Cover lookup: title='{title}', author='{author}'");
        var cover = await openLibrary.GetCoverUrlAsync(title, author)
                    ?? await googleBooks.GetCoverUrlAsync(title, author);

        Log.Information(cover is null
            ? $"Cover lookup failed for '{title}'"
            : $"Cover lookup found for '{title}': {cover}");

        return Results.Ok(new { coverUrl = cover });
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

            Log.Information("[slum-health] Returning {result.Count} monitors");
            return Results.Json(result);
        }
        catch (ArgumentException ex)
        {
            Log.Information("[slum-health] Invalid argument: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("[slum-health] Error: {ex.Message}");
            return Results.Json(new { success = false, error = ex.Message });
        }
    }

    private static async Task<IResult> HandleMirrorHealth(IHttpClientFactory httpFactory)
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(4);

        var mirrors = new[]
        {
            ("org", "https://annas-archive.org"),
            ("se",  "https://annas-archive.se"),
            ("li",  "https://annas-archive.li"),
            ("pm",  "https://annas-archive.pm"),
            ("in",  "https://annas-archive.in")
        };

        var results = new List<object>();

        foreach (var (extension, url) in mirrors)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                sw.Stop();

                var statusCode = (int)resp.StatusCode;
                var ok = statusCode >= 200 && statusCode < 500;
                results.Add(new
                {
                    name = $"Anna's Archive {extension.ToUpperInvariant()}",
                    extension,
                    health = ok ? 100 : 0,
                    statusCode,
                    responseMs = sw.ElapsedMilliseconds
                });
            }
            catch (ArgumentException ex)
            {
                sw.Stop();
                results.Add(new
                {
                    name = $"Anna's Archive {extension.ToUpperInvariant()}",
                    extension,
                    health = (int?)null,
                    statusCode = (int?)null,
                    responseMs = sw.ElapsedMilliseconds,
                    error = $"ArgumentException: {ex.ParamName ?? "unknown"}"
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new
                {
                    name = $"Anna's Archive {extension.ToUpperInvariant()}",
                    extension,
                    health = (int?)null,
                    statusCode = (int?)null,
                    responseMs = sw.ElapsedMilliseconds,
                    error = ex.GetType().Name
                });
            }
        }

        return Results.Json(results);
    }
}
