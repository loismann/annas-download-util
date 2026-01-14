using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping media-related endpoints.
/// </summary>
public static class MediaEndpoints
{
    /// <summary>
    /// Maps media endpoints to the application.
    /// </summary>
    public static WebApplication MapMediaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/media/wiki-images", HandleWikiImages)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleWikiImages([FromQuery] string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Results.BadRequest(new { error = "Query parameter 'term' is required." });

        try
        {
            var title = term.Replace(' ', '_');
            var summaryUrl = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title)}";

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AnnasArchive/1.0 (+https://fs01pfbooks.synology.me)");

            var images = new List<string>();
            var summaryJson = await http.GetStringAsync(summaryUrl);
            using (var doc = JsonDocument.Parse(summaryJson))
            {
                void TryAdd(string? url)
                {
                    if (string.IsNullOrWhiteSpace(url)) return;
                    var normalized = url.Replace(" ", "_");
                    if (!normalized.StartsWith("https://upload.wikimedia.org", StringComparison.OrdinalIgnoreCase)) return;
                    if (!(normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                          normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                          normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                        return;
                    if (!images.Contains(normalized)) images.Add(normalized);
                }

                if (doc.RootElement.TryGetProperty("originalimage", out var original) &&
                    original.TryGetProperty("source", out var source))
                {
                    TryAdd(source.GetString());
                }

                if (doc.RootElement.TryGetProperty("thumbnail", out var thumb) &&
                    thumb.TryGetProperty("source", out var tsource))
                {
                    TryAdd(tsource.GetString());
                }
            }

            // Fallback: use pageimages API for more options if needed
            if (images.Count < 3)
            {
                var queryUrl = $"https://en.wikipedia.org/w/api.php?action=query&format=json&prop=pageimages&piprop=original|thumbnail&pithumbsize=640&titles={Uri.EscapeDataString(title)}";
                var queryJson = await http.GetStringAsync(queryUrl);
                using var doc2 = JsonDocument.Parse(queryJson);
                if (doc2.RootElement.TryGetProperty("query", out var q) &&
                    q.TryGetProperty("pages", out var pages) &&
                    pages.EnumerateObject().Any())
                {
                    foreach (var page in pages.EnumerateObject())
                    {
                        var val = page.Value;
                        if (val.TryGetProperty("original", out var orig) && orig.TryGetProperty("source", out var osrc))
                        {
                            var url = osrc.GetString();
                            if (!string.IsNullOrWhiteSpace(url) && images.Count < 3)
                            {
                                var normalized = url!.Replace(" ", "_");
                                if (normalized.StartsWith("https://upload.wikimedia.org", StringComparison.OrdinalIgnoreCase) &&
                                    (normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                     normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                     normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                                {
                                    if (!images.Contains(normalized))
                                        images.Add(normalized);
                                }
                            }
                        }

                        if (val.TryGetProperty("thumbnail", out var thumb) && thumb.TryGetProperty("source", out var tsrc))
                        {
                            var url = tsrc.GetString();
                            if (!string.IsNullOrWhiteSpace(url) && images.Count < 3)
                            {
                                var normalized = url!.Replace(" ", "_");
                                if (normalized.StartsWith("https://upload.wikimedia.org", StringComparison.OrdinalIgnoreCase) &&
                                    (normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                     normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                     normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                                {
                                    if (!images.Contains(normalized))
                                        images.Add(normalized);
                                }
                            }
                        }
                    }
                }
            }

            return Results.Ok(new WikiImagesResponse(images));
        }
        catch
        {
            Log.Information("⚠️ Wiki images lookup failed for term '{term}'");
            return Results.Ok(new WikiImagesResponse(new List<string>()));
        }
    }
}

/// <summary>
/// Response containing Wikipedia image URLs.
/// </summary>
public record WikiImagesResponse(List<string> Images);
