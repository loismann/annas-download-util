using System.Text.Json;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Library cover management endpoints.
/// </summary>
public static class LibraryCoverEndpoints
{
    /// <summary>
    /// Maps Library cover endpoints to the application.
    /// </summary>
    public static WebApplication MapLibraryCoverEndpoints(this WebApplication app)
    {
        // GET /api/library/cover/{*path} - Get library cover file
        app.MapGet("/api/library/cover/{*path}", HandleGetCover)
            .RequireRateLimiting("api");

        // GET /api/library/book/cover-candidates - Get cover candidates
        app.MapGet("/api/library/book/cover-candidates", HandleGetCoverCandidates)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/book/{fileName}/cover - Update book cover from URL
        app.MapPost("/api/library/book/{fileName}/cover", HandleUpdateCover)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/book/{fileName}/cover-bytes - Update book cover from image bytes
        app.MapPost("/api/library/book/{fileName}/cover-bytes", HandleUpdateCoverBytes)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleGetCover(HttpContext context, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.NotFound();

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var fullPath = Path.GetFullPath(Path.Combine(libraryRoot, path));
        if (!fullPath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Invalid path." });

        if (!File.Exists(fullPath))
            return Results.NotFound();

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        // Prevent browser from caching covers so updates appear immediately
        // no-cache means browser must revalidate; it can still use conditional requests (304)
        context.Response.Headers.CacheControl = "no-cache, must-revalidate";

        return Results.File(fullPath, contentType);
    }

    private static async Task<IResult> HandleGetCoverCandidates(
        [FromQuery] string? title,
        [FromQuery] string? author,
        ICoverLookupService coverLookupService)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new { error = "title is required." });

        // Validate title length
        var titleValidation = ValidationHelpers.ValidateStringLength(title, "title", 500);
        if (titleValidation != null)
            return titleValidation;

        var covers = await coverLookupService.GetCoverCandidatesAsync(title, author);
        return Results.Ok(new { covers });
    }

    private static async Task<IResult> HandleUpdateCover(
        [FromRoute] string fileName,
        [FromBody] LibraryBookCoverUpdate update,
        HttpContext context,
        IHttpClientFactory httpFactory)
    {
        if (update == null || string.IsNullOrWhiteSpace(update.CoverUrl))
            return Results.BadRequest(new { error = "coverUrl is required." });

        if (!Uri.TryCreate(update.CoverUrl, UriKind.Absolute, out var coverUri) ||
            (coverUri.Scheme != Uri.UriSchemeHttp && coverUri.Scheme != Uri.UriSchemeHttps))
            return Results.BadRequest(new { error = "coverUrl must be an http(s) URL." });

        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        byte[] coverBytes;
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = HttpTimeouts.LibraryHttpOperation;
            using var request = new HttpRequestMessage(HttpMethod.Get, coverUri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            request.Headers.Referrer = new Uri(coverUri.GetLeftPart(UriPartial.Authority));

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            coverBytes = await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to download cover: {ex.Message}");
            return Results.Problem("Failed to download cover image.");
        }

        if (!CoverLookupHelpers.TryGetImageSize(coverBytes, out var width, out var height))
            return Results.BadRequest(new { error = "Unsupported cover image format." });

        if (!CoverLookupHelpers.IsCoverSizeValid(width, height))
        {
            return Results.BadRequest(new
            {
                error = "Cover image must be at least 100x100 pixels."
            });
        }

        var coverExt = CoverLookupHelpers.DetermineImageExtension(coverUri.ToString(), coverBytes);
        var coverDir = Path.Combine(libraryRoot, "_covers");
        Directory.CreateDirectory(coverDir);

        foreach (var existing in Directory.GetFiles(coverDir, $"{safeFileName}.cover.*"))
        {
            try { File.Delete(existing); } catch { /* ignore */ }
        }

        var coverFileName = $"{safeFileName}.cover{coverExt}";
        var coverDiskPath = Path.Combine(coverDir, coverFileName);
        await File.WriteAllBytesAsync(coverDiskPath, coverBytes);

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            Log.Information("[library-cover] READ metadata for {safeFileName}:");
            Log.Information("[library-cover]   Existing JSON: {json.Substring(0, Math.Min(200, json.Length))}...");

            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            Log.Information("[library-cover]   Deserialized CoverUrl: {meta.CoverUrl}");

            var updated = meta with { CoverUrl = $"_covers/{coverFileName}" };
            Log.Information("[library-cover]   NEW CoverUrl: {updated.CoverUrl}");

            var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
            Log.Information("[library-cover]   Serialized JSON: {updatedJson.Substring(0, Math.Min(200, updatedJson.Length))}...");

            await File.WriteAllTextAsync(metaPath, updatedJson);
            Log.Information("[library-cover] WROTE metadata to {metaPath}");

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var normalized = LibraryHelpers.NormalizeLibraryCoverUrl(updated.CoverUrl, baseUrl);

            return Results.Ok(new { success = true, coverUrl = normalized });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to update cover metadata for {safeFileName}: {ex.Message}");
            Log.Information("[library] Stack trace: {ex.StackTrace}");
            return Results.Problem("Failed to update cover metadata.");
        }
    }

    private static async Task<IResult> HandleUpdateCoverBytes(
        [FromRoute] string fileName,
        [FromBody] LibraryBookCoverBytesUpdate update,
        HttpContext context)
    {
        Log.Information("[library-cover-bytes] === START === Received request for {fileName}", fileName);

        if (update == null || string.IsNullOrWhiteSpace(update.ImageBase64))
        {
            Log.Information("[library-cover-bytes] ❌ Missing imageBase64 data");
            return Results.BadRequest(new { error = "imageBase64 is required." });
        }

        Log.Information("[library-cover-bytes] Received base64 data: {Length} chars, MimeType: {MimeType}",
            update.ImageBase64?.Length ?? 0, update.MimeType ?? "null");

        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        byte[] coverBytes;
        try
        {
            // Remove data URL prefix if present (e.g., "data:image/png;base64,")
            var base64Data = update.ImageBase64;
            var commaIndex = base64Data.IndexOf(',');
            if (commaIndex >= 0)
            {
                base64Data = base64Data.Substring(commaIndex + 1);
            }

            coverBytes = Convert.FromBase64String(base64Data);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new { error = "Invalid base64 data." });
        }

        if (coverBytes.Length == 0)
        {
            Log.Information("[library-cover-bytes] ❌ Empty image data after base64 decode");
            return Results.BadRequest(new { error = "Empty image data." });
        }

        Log.Information("[library-cover-bytes] ✓ Decoded {ByteCount} bytes from base64", coverBytes.Length);

        if (coverBytes.Length > 10 * 1024 * 1024) // 10MB limit
        {
            Log.Information("[library-cover-bytes] ❌ Image too large: {ByteCount} bytes", coverBytes.Length);
            return Results.BadRequest(new { error = "Image too large. Maximum size is 10MB." });
        }

        if (!CoverLookupHelpers.TryGetImageSize(coverBytes, out var width, out var height))
        {
            Log.Information("[library-cover-bytes] ❌ Could not determine image size (unsupported format). First 16 bytes: {Bytes}",
                BitConverter.ToString(coverBytes.Take(16).ToArray()));
            return Results.BadRequest(new { error = "Unsupported cover image format." });
        }

        Log.Information("[library-cover-bytes] ✓ Image dimensions: {Width}x{Height}", width, height);

        if (!CoverLookupHelpers.IsCoverSizeValid(width, height))
        {
            Log.Information("[library-cover-bytes] ❌ Image too small: {Width}x{Height}", width, height);
            return Results.BadRequest(new
            {
                error = "Cover image must be at least 100x100 pixels."
            });
        }

        // Determine extension from MIME type or image magic bytes
        var coverExt = CoverLookupHelpers.DetermineImageExtensionFromBytes(coverBytes, update.MimeType);
        Log.Information("[library-cover-bytes] ✓ Determined extension: {Ext}", coverExt);

        var coverDir = Path.Combine(libraryRoot, "_covers");
        Directory.CreateDirectory(coverDir);
        Log.Information("[library-cover-bytes] ✓ Cover directory: {CoverDir}", coverDir);

        // Delete existing covers for this book
        foreach (var existing in Directory.GetFiles(coverDir, $"{safeFileName}.cover.*"))
        {
            try { File.Delete(existing); } catch { /* ignore */ }
        }

        var coverFileName = $"{safeFileName}.cover{coverExt}";
        var coverDiskPath = Path.Combine(coverDir, coverFileName);

        Log.Information("[library-cover-bytes] Writing {ByteCount} bytes to: {Path}", coverBytes.Length, coverDiskPath);
        await File.WriteAllBytesAsync(coverDiskPath, coverBytes);
        Log.Information("[library-cover-bytes] ✓ File written successfully");

        // Verify file was written
        if (!File.Exists(coverDiskPath))
        {
            Log.Information("[library-cover-bytes] ❌ CRITICAL: File does not exist after write!");
            return Results.Problem("Failed to save cover image file.");
        }
        var fileInfo = new FileInfo(coverDiskPath);
        Log.Information("[library-cover-bytes] ✓ File verified: {Size} bytes on disk", fileInfo.Length);

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
            {
                Log.Information("[library-cover-bytes] ❌ Failed to deserialize metadata");
                return Results.BadRequest(new { error = "Invalid metadata file." });
            }

            Log.Information("[library-cover-bytes] ✓ Metadata loaded. Old CoverUrl: {OldUrl}", meta.CoverUrl);

            var updated = meta with { CoverUrl = $"_covers/{coverFileName}" };
            var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);

            Log.Information("[library-cover-bytes] Writing metadata to: {Path}", metaPath);
            await File.WriteAllTextAsync(metaPath, updatedJson);
            Log.Information("[library-cover-bytes] ✓ Metadata written. New CoverUrl: {NewUrl}", updated.CoverUrl);

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var normalized = LibraryHelpers.NormalizeLibraryCoverUrl(updated.CoverUrl, baseUrl);

            Log.Information("[library-cover-bytes] === SUCCESS === Returning coverUrl: {Url}", normalized);
            return Results.Ok(new { success = true, coverUrl = normalized });
        }
        catch (Exception ex)
        {
            Log.Information("[library-cover-bytes] ❌ Exception updating metadata: {Message}", ex.Message);
            Log.Information("[library-cover-bytes] Stack: {Stack}", ex.StackTrace);
            return Results.Problem("Failed to update cover metadata.");
        }
    }
}
