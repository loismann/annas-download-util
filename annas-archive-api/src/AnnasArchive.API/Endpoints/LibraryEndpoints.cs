using System.Text.Json;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Library-related endpoints.
/// </summary>
public static class LibraryEndpoints
{
    /// <summary>
    /// Maps Library endpoints to the application.
    /// </summary>
    public static WebApplication MapLibraryEndpoints(this WebApplication app)
    {
        // GET /api/library/books - List library books
        app.MapGet("/api/library/books", HandleListBooks)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/cover/{*path} - Get library cover file
        app.MapGet("/api/library/cover/{*path}", HandleGetCover)
            .RequireRateLimiting("api");

        // GET /api/library/book/cover-candidates - Get cover candidates
        app.MapGet("/api/library/book/cover-candidates", HandleGetCoverCandidates)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/book/send-to-kindle - Send book to Kindle
        app.MapPost("/api/library/book/send-to-kindle", HandleSendToKindle)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // PATCH /api/library/book/{fileName}/metadata - Update book metadata
        app.MapPatch("/api/library/book/{fileName}/metadata", HandleUpdateMetadata)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // PATCH /api/library/book/{fileName}/ratings - Update book ratings
        app.MapPatch("/api/library/book/{fileName}/ratings", HandleUpdateRatings)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/book/{fileName}/reader - Toggle reader inclusion (route param)
        app.MapPost("/api/library/book/{fileName}/reader", HandleToggleReaderByRoute)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/book/reader - Toggle reader inclusion (query param)
        app.MapPost("/api/library/book/reader", HandleToggleReaderByQuery)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/books/genres/wipe - Wipe all genres
        app.MapPost("/api/library/books/genres/wipe", HandleWipeGenres)
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

        // GET /api/library/book/{fileName}/summary - Get/generate book summary
        app.MapGet("/api/library/book/{fileName}/summary", HandleGetSummary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/reader/books - List reader-enabled books
        app.MapGet("/api/library/reader/books", HandleListReaderBooks)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/reader/epub/chapters - Get EPUB chapters
        app.MapGet("/api/library/reader/epub/chapters", HandleGetReaderChapters)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/reader/epub/chapter - Get single chapter content
        app.MapGet("/api/library/reader/epub/chapter", HandleGetReaderChapter)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/reader/epub/status - Get cache status
        app.MapGet("/api/library/reader/epub/status", HandleGetReaderStatus)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/reader/epub/index - Start indexing
        app.MapPost("/api/library/reader/epub/index", HandleStartReaderIndexing)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/library/reader/epub/index - Delete cache
        app.MapDelete("/api/library/reader/epub/index", HandleDeleteReaderIndex)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/reader/epub/search - Search within EPUB
        app.MapGet("/api/library/reader/epub/search", HandleReaderSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/library/book/{fileName} - Delete book
        app.MapDelete("/api/library/book/{fileName}", HandleDeleteBook)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleListBooks(HttpContext context)
    {
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return Results.Json(Array.Empty<LibraryBookDto>());

        var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
        var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
        var books = new List<LibraryBookDto>();
        var metaLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        foreach (var metaFile in metaFiles)
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                if (meta == null)
                    continue;

                metaLookup.Add(meta.FileName);
                var coverUrl = LibraryHelpers.NormalizeLibraryCoverUrl(meta.CoverUrl, baseUrl)
                    ?? LibraryHelpers.FindLocalCoverUrl(libraryRoot, meta.FileName, baseUrl);

                var genres = meta.Genres ?? Array.Empty<string>();
                var tags = meta.Tags ?? genres;
                var primaryGenre = meta.PrimaryGenre ?? genres.FirstOrDefault() ?? tags.FirstOrDefault();

                books.Add(new LibraryBookDto(
                    meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName),
                    meta.Authors ?? Array.Empty<string>(),
                    meta.Format ?? Path.GetExtension(meta.FileName).TrimStart('.').ToUpperInvariant(),
                    meta.FileSize ?? "",
                    meta.FileName,
                    coverUrl,
                    meta.Source,
                    meta.Md5,
                    meta.SavedAt,
                    primaryGenre,
                    tags,
                    meta.Series,
                    genres,
                    meta.PublishedDate,
                    meta.Pages,
                    meta.GoodreadsRating,
                    meta.PersonalRating,
                    meta.ReaderEnabled
                ));
            }
            catch
            {
                // ignore malformed meta files
            }
        }

        var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx", ".pobi", ".fb2" };

        foreach (var filePath in Directory.GetFiles(libraryRoot))
        {
            try
            {
                var ext = Path.GetExtension(filePath);
                if (!supportedExts.Contains(ext))
                    continue;

                var fileName = Path.GetFileName(filePath);
                if (string.IsNullOrWhiteSpace(fileName) || metaLookup.Contains(fileName))
                    continue;

                var info = new FileInfo(filePath);
                books.Add(new LibraryBookDto(
                    Path.GetFileNameWithoutExtension(fileName),
                    Array.Empty<string>(),
                    ext.TrimStart('.').ToUpperInvariant(),
                    LibraryHelpers.FormatFileSize(info.Length),
                    fileName,
                    null,
                    null,
                    null,
                    info.LastWriteTimeUtc,
                    null,
                    Array.Empty<string>(),
                    null,
                    Array.Empty<string>(),
                    null,
                    null,
                    null,
                    null,
                    null
                ));
            }
            catch (Exception ex)
            {
                Log.Information("[library] Skipping file {filePath}: {ex.Message}");
            }
        }

        var ordered = books
            .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Json(ordered);
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

    private static async Task<IResult> HandleSendToKindle(
        [FromQuery] string? fileName,
        [FromQuery] string? target,
        [FromQuery] string? title,
        [FromQuery] bool toDropbox,
        IEmailService emailService,
        DropboxClient dropbox,
        IConfiguration cfg,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "fileName is required." });

        // Validate fileName length and title length
        var fileNameValidation = ValidationHelpers.ValidateStringLength(fileName, "fileName", 500);
        if (fileNameValidation != null)
            return fileNameValidation;

        var titleValidation = ValidationHelpers.ValidateStringLength(title, "title", 500);
        if (titleValidation != null)
            return titleValidation;

        if (string.IsNullOrWhiteSpace(target) || (target != "dad" && target != "mom"))
            return Results.BadRequest(new { error = "Invalid target. Must be 'dad' or 'mom'." });

        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "File not found." });

        var kindleEmail = target == "dad"
            ? cfg["Email:DadsKindleEmail"] ?? throw new InvalidOperationException("Email:DadsKindleEmail not configured")
            : cfg["Email:MomsKindleEmail"] ?? throw new InvalidOperationException("Email:MomsKindleEmail not configured");

        if (toDropbox)
        {
            var dropboxPath = $"/KindleSync/{safeFileName}";
            await using var fileStream = File.OpenRead(fullPath);
            await dropbox.Files.UploadAsync(
                dropboxPath,
                WriteMode.Overwrite.Instance,
                body: fileStream);

            // Add Kindle target tag to library book metadata
            var userTag = LibraryHelpers.ResolveUserLibraryTag(context);
            var kindleTargetTag = LibraryHelpers.GetKindleTargetTag(target);
            var tagsToAdd = new[] { userTag, kindleTargetTag }.OfType<string>().ToArray();
            await LibraryHelpers.AddTagsToLibraryBookAsync(libraryRoot, safeFileName, tagsToAdd);
        }
        else
        {
            var subject = "Book from Library";
            var body = $"Sent from Library: {title ?? safeFileName}";
            await emailService.SendEmailWithAttachmentAsync(kindleEmail, subject, body, fullPath, safeFileName);

            // Add Kindle target tag to library book metadata
            var userTag = LibraryHelpers.ResolveUserLibraryTag(context);
            var kindleTargetTag = LibraryHelpers.GetKindleTargetTag(target);
            var tagsToAdd = new[] { userTag, kindleTargetTag }.OfType<string>().ToArray();
            await LibraryHelpers.AddTagsToLibraryBookAsync(libraryRoot, safeFileName, tagsToAdd);
        }

        return Results.Ok(new { success = true, message = toDropbox ? "Sent to Dropbox." : "Sent to Kindle." });
    }

    private static async Task<IResult> HandleUpdateMetadata(
        [FromRoute] string fileName,
        [FromBody] LibraryBookMetadataUpdate update,
        HttpContext context)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            // Update fields
            meta.PrimaryGenre = update.PrimaryGenre;
            meta.Tags = update.Tags ?? Array.Empty<string>();
            meta.Series = update.Series;
            if (!string.IsNullOrWhiteSpace(update.Title))
                meta.Title = update.Title;
            if (update.Authors != null)
                meta.Authors = update.Authors;

            // Save back to file
            var updatedJson = JsonSerializer.Serialize(meta, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            Log.Information("[library] Updated metadata for {safeFileName}: Genre={meta.PrimaryGenre}, Tags={string.Join(", ", meta.Tags)}, Series={meta.Series}");

            return Results.Ok(new { success = true, message = "Metadata updated successfully." });
        }
        catch (ArgumentException ex)
        {
            Log.Information("[library] Invalid argument for metadata update: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to update metadata for {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to update metadata.");
        }
    }

    private static async Task<IResult> HandleUpdateRatings(
        [FromRoute] string fileName,
        [FromBody] LibraryBookRatingsUpdate update)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            if (update.GoodreadsRating.HasValue)
            {
                var gr = Math.Clamp(update.GoodreadsRating.Value, 0, 5);
                meta.GoodreadsRating = gr;
            }

            if (update.PersonalRating.HasValue)
            {
                var pr = Math.Clamp(update.PersonalRating.Value, 0, 5);
                meta.PersonalRating = pr;
            }

            var updatedJson = JsonSerializer.Serialize(meta, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            Log.Information("[library] Updated ratings for {safeFileName}: Goodreads={meta.GoodreadsRating}, Personal={meta.PersonalRating}");

            return Results.Ok(new { success = true, message = "Ratings updated successfully." });
        }
        catch (ArgumentException ex)
        {
            Log.Information("[library] Invalid argument for ratings update: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to update ratings for {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to update ratings.");
        }
    }

    private static async Task<IResult> HandleToggleReaderByRoute(
        [FromRoute] string fileName,
        [FromBody] LibraryBookReaderUpdate update)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            var enabled = update?.Enabled ?? true;
            var updated = meta with { ReaderEnabled = enabled };
            var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            return Results.Ok(new { success = true, enabled });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to update reader flag for {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to update reader flag.");
        }
    }

    private static async Task<IResult> HandleToggleReaderByQuery(
        [FromQuery] string? fileName,
        [FromBody] LibraryBookReaderUpdate update)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            var enabled = update?.Enabled ?? true;
            var updated = meta with { ReaderEnabled = enabled };
            var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            return Results.Ok(new { success = true, enabled });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to update reader flag for {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to update reader flag.");
        }
    }

    private static async Task<IResult> HandleWipeGenres()
    {
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return Results.Ok(new { success = true, updated = 0 });

        var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
        var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
        var updatedCount = 0;

        foreach (var metaPath in metaFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(metaPath);
                var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                if (meta == null)
                    continue;

                var updated = meta with
                {
                    PrimaryGenre = null,
                    Tags = Array.Empty<string>(),
                    Genres = Array.Empty<string>()
                };

                var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
                await File.WriteAllTextAsync(metaPath, updatedJson);
                updatedCount++;
            }
            catch
            {
                // ignore individual file failures
            }
        }

        return Results.Ok(new { success = true, updated = updatedCount });
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

    private static async Task<IResult> HandleGetSummary(
        [FromRoute] string fileName,
        IDescriptionFetcherService descriptionFetcher)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            // If we already have a description, return it
            if (!string.IsNullOrWhiteSpace(meta.Description))
            {
                return Results.Ok(new { summary = meta.Description, source = "cached" });
            }

            // Try to fetch from external sources using the centralized service
            var title = meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName);
            var author = meta.Authors?.FirstOrDefault();

            var result = await descriptionFetcher.FetchDescriptionAsync(title, author);
            var summary = result.Description;
            var source = result.Source;

            // Save to metadata if we got a summary
            if (!string.IsNullOrWhiteSpace(summary))
            {
                try
                {
                    // Re-read to avoid race conditions
                    json = await File.ReadAllTextAsync(metaPath);
                    meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                    if (meta != null)
                    {
                        var updated = meta with { Description = summary };
                        var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
                        await File.WriteAllTextAsync(metaPath, updatedJson);
                        Log.Information("[library-summary] Saved summary for {safeFileName} (source: {source})");
                    }
                }
                catch (Exception ex)
                {
                    Log.Information("[library-summary] Failed to save summary: {ex.Message}");
                }
            }

            return Results.Ok(new { summary, source });
        }
        catch (Exception ex)
        {
            Log.Information("[library-summary] Error: {ex.Message}");
            return Results.Problem("Failed to get summary.");
        }
    }

    private static IResult HandleListReaderBooks(HttpContext context)
    {
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return Results.Json(Array.Empty<ReaderBookDto>());

        var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
        var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
        var results = new List<ReaderBookDto>();
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        var existingKeys = AiContentCache.GetExistingSummaryKeys();

        foreach (var metaFile in metaFiles)
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                if (meta == null)
                    continue;

                var ext = Path.GetExtension(meta.FileName);
                if (!string.Equals(ext, ".epub", StringComparison.OrdinalIgnoreCase))
                    continue;

                var readerKey = ResolveReaderKey(meta.FileName, existingKeys);
                var hasSummaries = AiContentCache.HasAnySummaries(readerKey, existingKeys);
                var include = meta.ReaderEnabled == true || hasSummaries;
                if (!include)
                    continue;

                var coverUrl = LibraryHelpers.NormalizeLibraryCoverUrl(meta.CoverUrl, baseUrl)
                    ?? LibraryHelpers.FindLocalCoverUrl(libraryRoot, meta.FileName, baseUrl);

                results.Add(new ReaderBookDto(
                    meta.FileName,
                    readerKey,
                    meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName),
                    meta.Authors ?? Array.Empty<string>(),
                    meta.Format ?? Path.GetExtension(meta.FileName).TrimStart('.').ToUpperInvariant(),
                    coverUrl,
                    hasSummaries
                ));
            }
            catch
            {
                // ignore malformed meta files
            }
        }

        return Results.Json(results.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static async Task<IResult> HandleGetReaderChapters(
        [FromQuery] string? fileName,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        CancellationToken cancellationToken,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
        var (index, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexQuickAsync(fullPath, readerKey);
        index = await ChapterLabelingHelper.EnsureGptChapterLabelsAsync(
            index,
            cacheDir,
            httpFactory,
            cfg,
            modelHelper,
            aiResponseParser,
            cancellationToken);
        var response = new DropboxEpubChaptersResponse(
            index.Title,
            index.Chapters.Select(ch => new DropboxChapterDto(
                ch.Id,
                ch.Title,
                ch.Level,
                ch.WordCount,
                ch.DisplayLabel,
                ch.IsMainChapter)).ToList());
        return Results.Ok(response);
    }

    private static async Task<IResult> HandleGetReaderChapter(
        [FromQuery] string? fileName,
        [FromQuery] int chapterId,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
        var (index, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexQuickAsync(fullPath, readerKey);
        var chapter = index.Chapters.FirstOrDefault(ch => ch.Id == chapterId);
        if (chapter == null)
            return Results.NotFound(new { error = "Chapter not found." });

        var contentPath = Path.Combine(cacheDir, chapter.FileName);
        if (!File.Exists(contentPath))
        {
            _ = LibraryEpubCache.EnsureCacheBuildAsync(fullPath, readerKey, cacheDir);
            var fallback = await LibraryEpubCache.ReadChapterContentCachedAsync(fullPath, chapterId);
            if (fallback == null)
                return Results.NotFound(new { error = "Chapter content not ready yet." });

            try
            {
                Directory.CreateDirectory(cacheDir);
                await File.WriteAllTextAsync(contentPath, fallback);
            }
            catch (Exception ex)
            {
                Log.Information("[library] Failed to persist chapter cache for {safeFileName} chapter {chapterId}: {ex.Message}");
            }

            var fallbackResponse = new DropboxChapterContentDto(
                chapter.Id,
                chapter.Title,
                fallback,
                chapter.CharacterCount,
                chapter.WordCount);
            return Results.Ok(fallbackResponse);
        }

        var content = await File.ReadAllTextAsync(contentPath);
        var response = new DropboxChapterContentDto(chapter.Id, chapter.Title, content, chapter.CharacterCount, chapter.WordCount);
        return Results.Ok(response);
    }

    private static async Task<IResult> HandleGetReaderStatus([FromQuery] string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
        var status = await LibraryEpubCache.GetCacheStatusAsync(fullPath, readerKey);
        return Results.Ok(status);
    }

    private static async Task<IResult> HandleStartReaderIndexing([FromBody] LibraryReaderIndexRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var fileName = Path.GetFileName(request.FileName);
        if (!string.Equals(Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var fullPath = Path.Combine(libraryRoot, fileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var readerKey = ResolveReaderKey(fileName, AiContentCache.GetExistingSummaryKeys());
        var (_, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexAsync(fullPath, readerKey);
        await LibraryEpubCache.EnsureCacheBuildAsync(fullPath, readerKey, cacheDir);
        return Results.Ok(new { started = true });
    }

    private static IResult HandleDeleteReaderIndex([FromBody] LibraryReaderIndexRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var fileName = Path.GetFileName(request.FileName);
        if (!string.Equals(Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var readerKey = ResolveReaderKey(fileName, AiContentCache.GetExistingSummaryKeys());

        // Delete EPUB chapter cache
        var epubCacheRemoved = LibraryEpubCache.DeleteCache(readerKey);
        Log.Information("[library] EPUB cache deleted for {ReaderKey}: {Removed}", readerKey, epubCacheRemoved);

        // Delete ALL AI-generated content (summaries, character graphs, section summaries, etc.)
        var aiCacheRemoved = AiContentCache.DeleteAllAiCacheForBook(readerKey);
        Log.Information("[library] AI cache deleted for {ReaderKey}: {Removed}", readerKey, aiCacheRemoved);

        return Results.Ok(new { success = epubCacheRemoved || aiCacheRemoved });
    }

    private static async Task<IResult> HandleReaderSearch(
        [FromQuery] string? fileName,
        [FromQuery] string? query,
        [FromQuery] string? q)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var normalizedQuery = (query ?? q)?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery) || normalizedQuery.Length < 10)
            return Results.BadRequest(new { error = "Search query must be at least 10 characters." });

        // Validate query max length
        var queryValidation = ValidationHelpers.ValidateStringLength(normalizedQuery, "query", 500);
        if (queryValidation != null)
            return queryValidation;

        var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
        var results = await LibraryEpubCache.SearchAsync(fullPath, readerKey, normalizedQuery);
        return Results.Ok(results);
    }

    private static IResult HandleDeleteBook([FromRoute] string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var bookPath = Path.Combine(libraryRoot, safeFileName);
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");
        var coverDir = Path.Combine(libraryRoot, "_covers");
        var coverMatches = Directory.Exists(coverDir)
            ? Directory.GetFiles(coverDir, $"{safeFileName}.cover.*")
            : Array.Empty<string>();

        if (!File.Exists(bookPath) && !File.Exists(metaPath) && coverMatches.Length == 0)
            return Results.NotFound(new { error = "Book not found." });

        try
        {
            if (File.Exists(bookPath))
                File.Delete(bookPath);

            if (File.Exists(metaPath))
                File.Delete(metaPath);

            foreach (var cover in coverMatches)
            {
                try { File.Delete(cover); } catch { /* ignore */ }
            }

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to delete book {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to delete book.");
        }
    }

    // Helper function for resolving reader keys
    private static string ResolveReaderKey(string fileName, ISet<string> existingKeys)
    {
        if (existingKeys == null || existingKeys.Count == 0)
            return fileName;

        var sanitized = AiContentCache.SanitizeKey(fileName);
        if (existingKeys.Contains(sanitized))
            return sanitized;

        var match = existingKeys.FirstOrDefault(key =>
            key.EndsWith(sanitized, StringComparison.OrdinalIgnoreCase));
        return match ?? fileName;
    }
}
