using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using AnnasArchive.API.Configuration;
using AnnasArchive.API.Endpoints;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading;
using BCrypt.Net;
using System.Diagnostics;
using VersOne.Epub;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Xml.Linq;
using ICSharpCode.SharpZipLib.Zip;
using ICSharpCode.SharpZipLib.Checksum;
using System.Net.Http.Headers;
using HtmlAgilityPack;
using NReadability;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

// ─── configuration ───────────────────────────────────────────────────────
builder.Configuration
       .SetBasePath(Directory.GetCurrentDirectory())
       .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
       .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
       .AddEnvironmentVariables();

var searchLimit = builder.Configuration.GetValue<int>("Anna:SearchLimit", 25);  // Reduced from 50 to 25 for better performance

// ─── Register all application services ───────────────────────────────────
builder.Services.AddApplicationServices(builder.Configuration);

var app = builder.Build();

// ─── AI job locks ───────────────────────────────────────────────────────────
var aiJobLocks = new ConcurrentDictionary<string, byte>();

bool TryStartAiJob(string key) => aiJobLocks.TryAdd(key, 0);
void EndAiJob(string key) => aiJobLocks.TryRemove(key, out _);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Anna's Archive v1"));
}

app.UseCors(p => p
    .WithOrigins(
        "https://fs01pfbooks.synology.me",      // Production HTTPS
        "http://fs01pfbooks.synology.me",       // Production HTTP (fallback)
        "http://localhost:4200",                // Local dev
        "https://localhost:4200"                // Local dev HTTPS
    )
    .AllowAnyHeader()
    .AllowAnyMethod());

// ─── security headers ────────────────────────────────────────────────
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

// ─── request body size limit (10MB for JSON payloads) ────────────────
app.Use(async (context, next) =>
{
    const long maxBodySize = 10 * 1024 * 1024; // 10 MB
    if (context.Request.ContentLength > maxBodySize)
    {
        context.Response.StatusCode = 413; // Payload Too Large
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Request body too large. Maximum size is 10 MB."
        });
        return;
    }
    await next();
});

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// ─── User activity tracking middleware ──────────────────────────────────────
app.Use(async (context, next) =>
{
    if (context.User?.Identity?.IsAuthenticated == true)
    {
        var userName = context.User.FindFirst(ClaimTypes.Name)?.Value;
        var activityService = context.RequestServices.GetRequiredService<IUserActivityService>();
        activityService.RecordActivity(userName ?? "");
    }
    await next();
});

// Helper functions moved to Helpers/ folder:
// - GetUserIdFromContext, GetUserDisplayNames -> UserHelpers.cs
// - CheckTokenLimit, IsTokenLimitExceeded -> TokenLimitHelpers.cs
// - GenerateNoSpoilerDescriptionAsync -> AiDescriptionHelpers.cs

var openLibraryAuthorCacheTtl = TimeSpan.FromHours(6);
var openLibraryAuthorCache = new Dictionary<string, (DateTime fetchedAt, List<AuthorSuggestion> authors)>();
var openLibraryAuthorCacheLock = new object();

bool TryGetOpenLibraryAuthorCache(string title, out List<AuthorSuggestion> authors)
{
    var key = title.Trim().ToLowerInvariant();
    lock (openLibraryAuthorCacheLock)
    {
        if (openLibraryAuthorCache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow - entry.fetchedAt <= openLibraryAuthorCacheTtl)
            {
                authors = entry.authors;
                return true;
            }
            openLibraryAuthorCache.Remove(key);
        }
    }

    authors = new List<AuthorSuggestion>();
    return false;
}

void SetOpenLibraryAuthorCache(string title, List<AuthorSuggestion> authors)
{
    var key = title.Trim().ToLowerInvariant();
    lock (openLibraryAuthorCacheLock)
    {
        openLibraryAuthorCache[key] = (DateTime.UtcNow, authors);
    }
}

async Task<List<AuthorSuggestion>> FetchAuthorsFromOpenLibraryAsync(string title, IHttpClientFactory httpFactory)
{
    if (string.IsNullOrWhiteSpace(title)) return new List<AuthorSuggestion>();

    if (TryGetOpenLibraryAuthorCache(title, out var cached))
        return cached;

    try
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(3);

        var query = Uri.EscapeDataString(title.Trim());
        var url = $"https://openlibrary.org/search.json?title={query}&limit=10";
        using var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return new List<AuthorSuggestion>();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
            return new List<AuthorSuggestion>();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in docs.EnumerateArray())
        {
            if (!item.TryGetProperty("author_name", out var authorNames) || authorNames.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var author in authorNames.EnumerateArray())
            {
                var name = author.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var key = name.Trim();
                counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
            }
        }

        if (counts.Count == 0) return new List<AuthorSuggestion>();

        var max = counts.Values.Max();
        string ConfidenceFromScore(int score)
        {
            var ratio = score / (double)max;
            if (ratio >= 0.66) return "high";
            if (ratio >= 0.34) return "medium";
            return "low";
        }

        var results = counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(5)
            .Select(kv => new AuthorSuggestion(kv.Key, ConfidenceFromScore(kv.Value)))
            .ToList();
        SetOpenLibraryAuthorCache(title, results);
        return results;
    }
    catch
    {
        return new List<AuthorSuggestion>();
    }
}


// ─── Anna Download endpoints ── (moved to AnnaDownloadEndpoints.cs)

// ═══════════════════════════════════════════════════════════════════════════
// ═══ LIBGEN ENDPOINTS ══════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════════

// ─── LibGen search ── (moved to LibGenEndpoints.cs)

// ─── LibGen download ─────────────────────────────────────────────────────
app.MapPost("/api/libgen/book/{md5}/download/member", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    [FromQuery] string? coverUrl,
    [FromQuery] string? authors,
    [FromQuery] string? format,
    [FromQuery] string? fileSize,
    [FromQuery] string? source,
    LibGenService libgen,
    IValidationService validation,
    IEbookCoverService coverService,
    IDownloadTrackingService downloadTracking,
    HttpContext context) =>
{
    if (!validation.IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!validation.IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    // Get user name from auth context
    var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
        ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
        ?? "unknown";

    Console.WriteLine($"📚 [LibGen] Downloading book {md5} for user {userName}...");

    // Download the book from LibGen
    var resp = await libgen.GetDownloadResponseAsync(md5, HttpCompletionOption.ResponseHeadersRead);

    if (resp == null || !resp.IsSuccessStatusCode)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        Console.WriteLine($"❌ [LibGen] Failed to download book {md5}");
        return Results.Ok(new { success = false, message = "Failed to download book from LibGen.", accountFastInfo = trackingInfo });
    }

    // Sanitize title and determine file extension
    var rawTitle = !string.IsNullOrWhiteSpace(title) ? title : md5;
    var safeTitle = Regex.Replace(rawTitle, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");

    var downloadUrl = await libgen.GetDownloadUrlAsync(md5);
    var ext = !string.IsNullOrEmpty(downloadUrl) ? Path.GetExtension(new Uri(downloadUrl).AbsolutePath) : "";

    if (string.IsNullOrEmpty(ext))
        ext = resp.Content.Headers.ContentType?.MediaType switch
        {
            "application/pdf"                 => ".pdf",
            "application/epub+zip"            => ".epub",
            "application/x-mobipocket-ebook"  => ".mobi",
            _                                 => ".bin"
        };

    var fileName = $"{safeTitle}{ext}";

    Console.WriteLine($"✅ [LibGen] Downloaded: {fileName}");

    // Record successful download in our tracking system
    downloadTracking.RecordDownload(md5, userName);
    Console.WriteLine($"[download-libgen] Recorded download for user {userName}, MD5: {md5}");

    // Get updated download status
    var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();

    using (resp)
    {
        Stream ebookStream = await resp.Content.ReadAsStreamAsync();

        // Attempt cover replacement if coverUrl is provided and format is supported
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            var extNoDot = ext.TrimStart('.');
            if (coverService.IsFormatSupported(extNoDot))
            {
                Console.WriteLine($"[download-libgen] Attempting cover replacement for {fileName}");
                ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, extNoDot);
            }
            else
            {
                Console.WriteLine($"[download-libgen] Format {extNoDot} not supported for cover replacement, skipping");
            }
        }

        // Set content type based on file extension
        var contentType = ext.ToLowerInvariant() switch
        {
            ".epub" => "application/epub+zip",
            ".pdf" => "application/pdf",
            ".mobi" => "application/x-mobipocket-ebook",
            ".azw3" => "application/vnd.amazon.ebook",
            ".fb2" => "text/xml",
            _ => "application/octet-stream"
        };

        // Stream the file back to the client
        return Results.Stream(ebookStream, contentType, fileName);
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── LibGen send-to-library ──────────────────────────────────────────────
app.MapPost("/api/libgen/book/{md5}/send-to-library", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    [FromQuery] string? coverUrl,
    [FromQuery] string? authors,
    [FromQuery] string? format,
    [FromQuery] string? fileSize,
    [FromQuery] string? source,
    [FromQuery] string? description,
    LibGenService libgen,
    IValidationService validation,
    IEbookCoverService coverService,
    IDownloadTrackingService downloadTracking,
    HttpContext context) =>
{
    if (!validation.IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!validation.IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    // Get user name from auth context
    var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
        ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
        ?? "unknown";
    var userTag = LibraryHelpers.ResolveUserLibraryTag(context);

    Console.WriteLine($"📚 [LibGen] Saving book {md5} to library for user {userName}...");

    // Download the book from LibGen
    var resp = await libgen.GetDownloadResponseAsync(md5, HttpCompletionOption.ResponseHeadersRead);

    if (resp == null || !resp.IsSuccessStatusCode)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        Console.WriteLine($"❌ [LibGen] Failed to download book {md5}");
        return Results.Ok(new { success = false, message = "Failed to download book from LibGen.", accountFastInfo = trackingInfo });
    }

    // Sanitize title and determine file extension
    var rawTitle = !string.IsNullOrWhiteSpace(title) ? title : md5;
    var safeTitle = Regex.Replace(rawTitle, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");

    var downloadUrl = await libgen.GetDownloadUrlAsync(md5);
    var ext = !string.IsNullOrEmpty(downloadUrl) ? Path.GetExtension(new Uri(downloadUrl).AbsolutePath) : "";

    if (string.IsNullOrEmpty(ext))
        ext = resp.Content.Headers.ContentType?.MediaType switch
        {
            "application/pdf"                 => ".pdf",
            "application/epub+zip"            => ".epub",
            "application/x-mobipocket-ebook"  => ".mobi",
            _                                 => ".bin"
        };

    var fileName = $"{safeTitle}{ext}";

    // Record successful download in our tracking system
    downloadTracking.RecordDownload(md5, userName);
    Console.WriteLine($"[library-libgen] Recorded download for user {userName}, MD5: {md5}");

    // Get updated download status
    var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();
    var currentTrackingInfo = new AccountFastDownloadInfoDto(currentDownloadsLeft, currentDownloadsPerDay);

    var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
    Directory.CreateDirectory(libraryRoot);

    using (resp)
    {
        Stream ebookStream = await resp.Content.ReadAsStreamAsync();

        // Attempt cover replacement if coverUrl is provided and format is supported
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            var extNoDot = ext.TrimStart('.');
            if (coverService.IsFormatSupported(extNoDot))
            {
                Console.WriteLine($"[library-libgen] Attempting cover replacement for {fileName}");
                ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, extNoDot);
            }
            else
            {
                Console.WriteLine($"[library-libgen] Format {extNoDot} not supported for cover replacement, skipping");
            }
        }

        var destinationPath = Path.Combine(libraryRoot, fileName);
        if (File.Exists(destinationPath))
        {
            return Results.Ok(new
            {
                success = true,
                message = "File already exists in library.",
                fileName,
                path = destinationPath,
                accountFastInfo = currentTrackingInfo
            });
        }

        await using var outStream = File.Create(destinationPath);
        await ebookStream.CopyToAsync(outStream);

        await LibraryHelpers.WriteLibraryMetadataAsync(libraryRoot, fileName, md5, title, authors, format, fileSize, coverUrl, source, userTag, description);

        return Results.Ok(new
        {
            success = true,
            message = "Saved to library.",
            fileName,
            path = destinationPath,
            accountFastInfo = currentTrackingInfo
        });
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ═══════════════════════════════════════════════════════════════════════════
// ═══ END LIBGEN ENDPOINTS ══════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════════

// ─── Health endpoints ── (moved to BookSearchEndpoints.cs)

// ─── 3c) Library listing ─────────────────────────────────────────────────
app.MapGet("/api/library/books", (HttpContext context) =>
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
            Console.WriteLine($"[library] Skipping file {filePath}: {ex.Message}");
        }
    }

    var ordered = books
        .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();

    return Results.Json(ordered);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3d) Library cover file ──────────────────────────────────────────────
app.MapGet("/api/library/cover/{*path}", (HttpContext context, string path) =>
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
        _ => "application/octet-stream"
    };

    return Results.File(fullPath, contentType);
})
.RequireRateLimiting("api");

// ─── Library cover candidates ────────────────────────────────────────────
app.MapGet("/api/library/book/cover-candidates", async (
    [FromQuery] string title,
    [FromQuery] string? author,
    IOpenLibraryService openLibrarySvc,
    IGoogleBooksService googleBooksSvc) =>
{
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "title is required." });

    var openLibrary = await openLibrarySvc.GetCoverCandidatesAsync(title, author);
    var google = await googleBooksSvc.GetCoverCandidatesAsync(title, author);
    var covers = openLibrary.Concat(google)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    return Results.Ok(new { covers });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3e) Library send-to-kindle ──────────────────────────────────────────
app.MapPost("/api/library/book/send-to-kindle", async (
    [FromQuery] string fileName,
    [FromQuery] string target,
    [FromQuery] string? title,
    [FromQuery] bool toDropbox,
    IEmailService emailService,
    DropboxClient dropbox,
    IConfiguration cfg,
    HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(fileName))
        return Results.BadRequest(new { error = "fileName is required." });

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
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f) Update library book metadata ────────────────────────────────────
app.MapPatch("/api/library/book/{fileName}/metadata", async (
    [FromRoute] string fileName,
    [FromBody] LibraryBookMetadataUpdate update,
    HttpContext context) =>
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

        Console.WriteLine($"[library] Updated metadata for {safeFileName}: Genre={meta.PrimaryGenre}, Tags={string.Join(", ", meta.Tags)}, Series={meta.Series}");

        return Results.Ok(new { success = true, message = "Metadata updated successfully." });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to update metadata for {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to update metadata.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f-1) Update library book ratings ───────────────────────────────────
app.MapPatch("/api/library/book/{fileName}/ratings", async (
    [FromRoute] string fileName,
    [FromBody] LibraryBookRatingsUpdate update) =>
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

        Console.WriteLine($"[library] Updated ratings for {safeFileName}: Goodreads={meta.GoodreadsRating}, Personal={meta.PersonalRating}");

        return Results.Ok(new { success = true, message = "Ratings updated successfully." });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to update ratings for {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to update ratings.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f-1) Toggle library reader inclusion ──────────────────────────────
app.MapPost("/api/library/book/{fileName}/reader", async (
    [FromRoute] string fileName,
    [FromBody] LibraryBookReaderUpdate update) =>
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
        Console.WriteLine($"[library] Failed to update reader flag for {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to update reader flag.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/library/book/reader", async (
    [FromQuery] string fileName,
    [FromBody] LibraryBookReaderUpdate update) =>
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
        Console.WriteLine($"[library] Failed to update reader flag for {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to update reader flag.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f-2) Wipe all library genres ───────────────────────────────────────
app.MapPost("/api/library/books/genres/wipe", async () =>
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
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3g) Update library book cover ───────────────────────────────────────
app.MapPost("/api/library/book/{fileName}/cover", async (
    [FromRoute] string fileName,
    [FromBody] LibraryBookCoverUpdate update,
    HttpContext context,
    IHttpClientFactory httpFactory) =>
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
        http.Timeout = TimeSpan.FromSeconds(6);
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
        Console.WriteLine($"[library] Failed to download cover: {ex.Message}");
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
        Console.WriteLine($"[library-cover] READ metadata for {safeFileName}:");
        Console.WriteLine($"[library-cover]   Existing JSON: {json.Substring(0, Math.Min(200, json.Length))}...");

        var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

        if (meta == null)
            return Results.BadRequest(new { error = "Invalid metadata file." });

        Console.WriteLine($"[library-cover]   Deserialized CoverUrl: {meta.CoverUrl}");

        var updated = meta with { CoverUrl = $"_covers/{coverFileName}" };
        Console.WriteLine($"[library-cover]   NEW CoverUrl: {updated.CoverUrl}");

        var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
        Console.WriteLine($"[library-cover]   Serialized JSON: {updatedJson.Substring(0, Math.Min(200, updatedJson.Length))}...");

        await File.WriteAllTextAsync(metaPath, updatedJson);
        Console.WriteLine($"[library-cover] WROTE metadata to {metaPath}");

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        var normalized = LibraryHelpers.NormalizeLibraryCoverUrl(updated.CoverUrl, baseUrl);

        return Results.Ok(new { success = true, coverUrl = normalized });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to update cover metadata for {safeFileName}: {ex.Message}");
        Console.WriteLine($"[library] Stack trace: {ex.StackTrace}");
        return Results.Problem("Failed to update cover metadata.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3g-1) Get/generate library book summary ─────────────────────────────
app.MapGet("/api/library/book/{fileName}/summary", async (
    [FromRoute] string fileName,
    IHttpClientFactory httpFactory,
    IModelSelectionService modelSelection,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser) =>
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

        // Try to fetch from external sources
        var title = meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName);
        var author = meta.Authors?.FirstOrDefault();
        string? summary = null;
        string? source = null;

        // 1. Try Google Books API
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            var query = Uri.EscapeDataString($"{title} {author ?? ""}".Trim());
            var googleUrl = $"https://www.googleapis.com/books/v1/volumes?q={query}&maxResults=1";
            var googleResp = await http.GetStringAsync(googleUrl);
            var googleJson = JsonDocument.Parse(googleResp);

            if (googleJson.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var volumeInfo = items[0].GetProperty("volumeInfo");
                if (volumeInfo.TryGetProperty("description", out var desc))
                {
                    summary = desc.GetString();
                    source = "Google Books";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[library-summary] Google Books lookup failed: {ex.Message}");
        }

        // 2. Try Open Library if Google failed
        if (string.IsNullOrWhiteSpace(summary))
        {
            try
            {
                using var http = httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var query = Uri.EscapeDataString($"{title} {author ?? ""}".Trim());
                var olSearchUrl = $"https://openlibrary.org/search.json?q={query}&limit=1";
                var olSearchResp = await http.GetStringAsync(olSearchUrl);
                var olSearchJson = JsonDocument.Parse(olSearchResp);

                if (olSearchJson.RootElement.TryGetProperty("docs", out var docs) && docs.GetArrayLength() > 0)
                {
                    var firstDoc = docs[0];

                    // Try first_sentence
                    if (firstDoc.TryGetProperty("first_sentence", out var firstSentence) && firstSentence.GetArrayLength() > 0)
                    {
                        summary = firstSentence[0].GetString();
                        source = "Open Library";
                    }

                    // Try to get work details for description
                    if (string.IsNullOrWhiteSpace(summary) && firstDoc.TryGetProperty("key", out var workKey))
                    {
                        var workUrl = $"https://openlibrary.org{workKey.GetString()}.json";
                        var workResp = await http.GetStringAsync(workUrl);
                        var workJson = JsonDocument.Parse(workResp);

                        if (workJson.RootElement.TryGetProperty("description", out var workDesc))
                        {
                            if (workDesc.ValueKind == JsonValueKind.String)
                            {
                                summary = workDesc.GetString();
                            }
                            else if (workDesc.ValueKind == JsonValueKind.Object && workDesc.TryGetProperty("value", out var descValue))
                            {
                                summary = descValue.GetString();
                            }
                            source = "Open Library";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[library-summary] Open Library lookup failed: {ex.Message}");
            }
        }

        // 3. Fall back to GPT-4 if external sources failed
        if (string.IsNullOrWhiteSpace(summary))
        {
            try
            {
                using var openAiHttp = httpFactory.CreateClient("OpenAI");
                var model = modelSelection.GetModelFast();
                summary = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                    title,
                    author ?? "Unknown",
                    openAiHttp,
                    model,
                    modelHelper,
                    aiResponseParser);
                source = "GPT-4";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[library-summary] GPT-4 generation failed: {ex.Message}");
            }
        }

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
                    Console.WriteLine($"[library-summary] Saved summary for {safeFileName} (source: {source})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[library-summary] Failed to save summary: {ex.Message}");
            }
        }

        return Results.Ok(new { summary, source });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library-summary] Error: {ex.Message}");
        return Results.Problem("Failed to get summary.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f-4) Library reader list ──────────────────────────────────────────
app.MapGet("/api/library/reader/books", (HttpContext context) =>
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
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5b-alt) Library EPUB reader endpoints ───────────────────────────────
app.MapGet("/api/library/reader/epub/chapters", async (
    [FromQuery] string fileName,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    CancellationToken cancellationToken,
    HttpContext context) =>
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
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/library/reader/epub/chapter", async (
    [FromQuery] string fileName,
    [FromQuery] int chapterId,
    HttpContext context) =>
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
            Console.WriteLine($"[library] Failed to persist chapter cache for {safeFileName} chapter {chapterId}: {ex.Message}");
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
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/library/reader/epub/status", async (
    [FromQuery] string fileName) =>
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
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/library/reader/epub/index", async (
    [FromBody] LibraryReaderIndexRequest request) =>
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
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapDelete("/api/library/reader/epub/index", ([FromBody] LibraryReaderIndexRequest request) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.FileName))
        return Results.BadRequest(new { error = "fileName is required." });

    var fileName = Path.GetFileName(request.FileName);
    if (!string.Equals(Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Reader supports EPUB files only." });
    var readerKey = ResolveReaderKey(fileName, AiContentCache.GetExistingSummaryKeys());
    var removed = LibraryEpubCache.DeleteCache(readerKey);
    return Results.Ok(new { success = removed });
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/library/reader/epub/search", async (
    [FromQuery] string fileName,
    [FromQuery] string? query,
    [FromQuery] string? q) =>
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

    var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
    var results = await LibraryEpubCache.SearchAsync(fullPath, readerKey, normalizedQuery);
    return Results.Ok(results);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3h) Delete library book ─────────────────────────────────────────────
app.MapDelete("/api/library/book/{fileName}", (
    [FromRoute] string fileName) =>
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
        Console.WriteLine($"[library] Failed to delete book {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to delete book.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5b) Dropbox EPUB reader endpoints ─────────────────────────────────
app.MapGet("/api/anna/dropbox/epubs", async (
    DropboxClient dropbox,
    IConfiguration cfg) =>
{
    try
    {
        var folderPath = cfg["Dropbox:UploadFolderPath"] ?? string.Empty;
        var epubs = await DropboxEpubCache.ListDropboxEpubsAsync(dropbox, folderPath);
        return Results.Ok(epubs);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to list Dropbox EPUBs: {ex.Message}");
        return ApiResponse.InternalError("Unable to list Dropbox files right now.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/anna/dropbox/epub/chapters", async (
    [FromQuery] string path,
    IValidationService validation,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    CancellationToken cancellationToken) =>
{
    if (!validation.IsValidDropboxPath(path))
        return Results.BadRequest(new {
            error = "Invalid Dropbox path. Must start with '/', end with '.epub', and be less than 500 characters."
        });

    try
    {
        var (index, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
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
            index.Chapters
                .Where(ch => ch.WordCount >= 50)
                .Select(ch => new DropboxChapterDto(
                    ch.Id,
                    ch.Title,
                    ch.Level,
                    ch.WordCount,
                    ch.DisplayLabel,
                    ch.IsMainChapter))
                .ToList()
        );

        return Results.Ok(response);
    }
    catch (ApiException<DownloadError> ex)
    {
        Console.WriteLine($"❌ Dropbox download failed: {ex.ErrorResponse}");
        return ApiResponse.InternalError("Unable to download EPUB from Dropbox.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to load EPUB: {ex.Message}");
        return ApiResponse.InternalError("Unable to read the EPUB file.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/anna/dropbox/epub/chapter", async (
    [FromQuery] string path,
    [FromQuery] int chapterId,
    IValidationService validation,
    DropboxClient dropbox) =>
{
    if (!validation.IsValidDropboxPath(path))
        return Results.BadRequest(new {
            error = "Invalid Dropbox path. Must start with '/', end with '.epub', and be less than 500 characters."
        });

    if (!validation.IsValidChapterId(chapterId))
        return Results.BadRequest(new { error = "Chapter ID must be between 0 and 9999." });

    try
    {
        var (index, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
        var chapter = index.Chapters.FirstOrDefault(c => c.Id == chapterId);

        if (chapter is null || string.IsNullOrWhiteSpace(chapter.FileName))
            return ApiResponse.NotFound("Chapter not found.");

        var chapterPath = Path.Combine(cacheDir, chapter.FileName);
        if (!File.Exists(chapterPath))
        {
            await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, path, cacheDir);
        }

        var contentText = await File.ReadAllTextAsync(chapterPath);

        var content = new DropboxChapterContentDto(
            chapter.Id,
            chapter.Title,
            contentText,
            contentText.Length,
            chapter.WordCount);

        return Results.Ok(content);
    }
    catch (ApiException<DownloadError> ex)
    {
        Console.WriteLine($"❌ Dropbox download failed: {ex.ErrorResponse}");
        return ApiResponse.InternalError("Unable to download EPUB from Dropbox.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to load EPUB: {ex.Message}");
        return ApiResponse.InternalError("Unable to read the EPUB file.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/anna/dropbox/epub/status", async (
    [FromQuery] string path,
    DropboxClient dropbox) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    try
    {
        var status = await DropboxEpubCache.GetCacheStatusAsync(dropbox, path);
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to read cache status: {ex.Message}");
        return ApiResponse.InternalError("Unable to fetch cache status.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/anna/dropbox/epub/index", async (
    [FromQuery] string path,
    DropboxClient dropbox) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    try
    {
        var (_, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
        // fire and forget rebuild to ensure freshness
        await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, path, cacheDir);
        return Results.Ok(new { started = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to start indexing: {ex.Message}");
        return ApiResponse.InternalError("Unable to start indexing for this book.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapDelete("/api/anna/dropbox/epub/index", (
    [FromQuery] string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    try
    {
        // Delete EPUB cache (chapter content)
        var epubRemoved = DropboxEpubCache.DeleteCache(path);

        // Delete AI cache (summaries, vocab, chunk boundaries, character graph)
        var aiRemoved = AiContentCache.DeleteAllAiCacheForBook(path);

        Console.WriteLine($"🗑️ Cache deletion: EPUB={epubRemoved}, AI={aiRemoved}");
        return Results.Ok(new { epubCacheRemoved = epubRemoved, aiCacheRemoved = aiRemoved });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to delete cache: {ex.Message}");
        return ApiResponse.InternalError("Unable to delete cache for this book.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5e) Summarize via OpenAI ────────────────────────────────────────────
app.MapPost("/api/ai/summarize", async (
    HttpContext context,
    [FromBody] SummarizeRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection,
    IValidationService validation,
    ITextProcessingService textProcessing) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest(new { error = "Text is required." });

    if (!validation.IsValidTextLength(request.Text))
        return Results.BadRequest(new { error = "Text too long. Maximum 1,000,000 characters." });

    var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelFast();

        string? previousAnalyses = null;
        string? cacheDirForSummary = null;

        if (!string.IsNullOrWhiteSpace(request.DropboxPath))
        {
            cacheDirForSummary = Path.Combine(DropboxEpubCache.GetCacheRoot(), DropboxEpubCache.ComputeHashPublic(request.DropboxPath));
            Directory.CreateDirectory(cacheDirForSummary);

            if (request.ChapterId.HasValue)
            {
                // Load ALL previous analyses for this chapter (sorted chronologically by word offset)
                var existingFiles = Directory.EnumerateFiles(cacheDirForSummary, $"summary-{request.ChapterId.Value}-*.txt")
                    .Select(f => new
                    {
                        Path = f,
                        Offset = textProcessing.ExtractWordOffset(Path.GetFileNameWithoutExtension(f))
                    })
                    .Where(x => x.Offset < (request.WordOffset ?? int.MaxValue)) // Only include analyses from earlier in the chapter
                    .OrderBy(x => x.Offset)
                    .ToList();

                if (existingFiles.Any())
                {
                    var analyses = new List<string>();
                    foreach (var file in existingFiles)
                    {
                        var content = await File.ReadAllTextAsync(file.Path);
                        if (!string.IsNullOrWhiteSpace(content))
                            analyses.Add(content);
                    }

                    if (analyses.Count > 0)
                        previousAnalyses = string.Join("\n\n---\n\n", analyses);
                }
            }
        }

        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.BookTitle))
            contextParts.Add($"Title: {request.BookTitle}");
        if (!string.IsNullOrWhiteSpace(request.Author))
            contextParts.Add($"Author: {request.Author}");
        if (request.Year.HasValue)
            contextParts.Add($"Year: {request.Year.Value}");
        if (!string.IsNullOrWhiteSpace(request.Premise))
            contextParts.Add($"Premise: {request.Premise}");

        var contextBlock = contextParts.Count > 0
            ? $"Book context -> {string.Join(" | ", contextParts)}"
            : "Book context -> (not provided)";

        // Build the system prompt with known words exclusion
        var systemPromptBase = @"You are an advanced literary analysis assistant with deep knowledge of philosophy, critical theory, and cultural studies. Provide a rich, thoughtful analysis (max 200 words) that goes beyond surface-level reading:

**Analysis should include:**
- What's happening narratively and conceptually
- Philosophical undertones and implicit arguments the author is making
- Literary techniques and their rhetorical effect
- How this passage connects to broader themes in the work
- Academic interpretations and critical perspectives (if applicable)
- Cultural, historical, or political context that enriches understanding
- Connections to other philosophical or literary traditions

Then add a 'Definitions:' section. BE EXTREMELY THOROUGH with definitions - include ALL words/phrases a typical high school student might not know: archaic terms, foreign words/phrases, technical jargon, sophisticated vocabulary, philosophical concepts, brand names, historical items, British/European terms, proper nouns needing context, academic terminology. Err on the side of over-defining.";

        string systemPrompt;
        if (request.KnownWords != null && request.KnownWords.Count > 0)
        {
            var knownWordsList = string.Join(", ", request.KnownWords);
            systemPrompt = $"{systemPromptBase}\n\nIMPORTANT: The user already knows these words, so DO NOT define them: {knownWordsList}. Total response can be up to 600 words.";
        }
        else
        {
            systemPrompt = $"{systemPromptBase}\n\nTotal response can be up to 600 words.";
        }

        var userPrompt = textProcessing.BuildAnalysisPrompt(contextBlock, previousAnalyses, request.Text);
        var fullInput = $"{systemPrompt}\n\n{userPrompt}";

        var payload = new
        {
            model = model,
            input = fullInput,
            reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:Vocabulary") },
            max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:Vocabulary"),
            temperature = cfg.GetValue<double>("AI:Temperature:Vocabulary")
        };

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI summarize failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        var summary = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("input_tokens").GetInt32();
            var completionTokens = usage.GetProperty("output_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        if (cacheDirForSummary != null && request.ChapterId.HasValue)
        {
            var offsetLabel = request.WordOffset?.ToString() ?? DateTime.UtcNow.Ticks.ToString();
            var fileName = $"summary-{request.ChapterId.Value}-{offsetLabel}.txt";
            var savePath = Path.Combine(cacheDirForSummary, fileName);
            try
            {
                await File.WriteAllTextAsync(savePath, summary ?? string.Empty);
            }
            catch { /* ignore */ }
        }

        return Results.Ok(new SummarizeResponse(summary ?? "No summary returned."));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI summarize failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to summarize text.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5f) Learn more about a vocab term ──────────────────────────
app.MapPost("/api/ai/vocab/learn-more", async (
    HttpContext context,
    [FromBody] LearnMoreRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Term))
        return Results.BadRequest(new { error = "Term is required." });

    var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelDeep();

        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.BookTitle))
            contextParts.Add($"Book: {request.BookTitle}");
        if (!string.IsNullOrWhiteSpace(request.DropboxPath))
            contextParts.Add($"Source path: {request.DropboxPath}");

        var prompt = $@"Provide a rich, scholarly 300-400 word deep dive on the term/phrase ""{request.Term}"" that goes beyond dictionary definitions.

Respond as concise HTML with paragraphs, <ul>, <strong>, and include up to 2-3 reliable image URLs and 1-2 reference links (e.g., Wikipedia) that help explain the term.

**Your analysis should explore:**
- Core meaning and etymology
- Historical development and evolution of the concept
- How this term/concept is understood in different academic disciplines (philosophy, literature, sociology, etc.)
- Key thinkers, works, or movements associated with it
- How it appears in popular culture vs. academic discourse
- Common misconceptions or debates surrounding the term
- Relevance to contemporary discussions or current events (if applicable)
- Interesting facts or notable usage examples

IMAGE RULES (strict):
- Prefer upload.wikimedia.org or commons.wikimedia.org images; use fully-qualified HTTPS URLs with underscores instead of spaces.
- Do NOT include images unless you are confident the URL exists and is directly fetchable (ending in .jpg/.png/.jpeg).
- If unsure about an image URL, skip images entirely.

Structure:
- Rich overview paragraph (2-3 sentences)
- Bullet list covering the points above
- A ""Resources"" section with authoritative hyperlinks (plain <a href=""..."">text</a>)
- After the text, include a line ""Images:"" followed by <img src=""..."" alt=""..."" loading=""lazy"" /> for each image (absolute URLs only). Use images that are likely to be stable (e.g., Wikimedia, Wikipedia, major news/edu sites). No base64.

Context: {string.Join(" | ", contextParts)}
Definition (if given): {request.Definition ?? "(none)"}
Relevant passage/context: {request.Context ?? "(none)"}";

        var systemInstructions = "You are a scholarly explainer with expertise in philosophy, critical theory, literature, history, and cultural studies. Provide nuanced, intellectually rich analysis that bridges academic and accessible discourse.";
        var fullInput = $"{systemInstructions}\n\n{prompt}";

        var payload = new
        {
            model,
            input = fullInput,
            reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:WikiImages") },
            max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:WikiImages"),
            temperature = cfg.GetValue<double>("AI:Temperature:WikiImages")
        };

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI learn-more failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var detail = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("input_tokens").GetInt32();
            var completionTokens = usage.GetProperty("output_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        return Results.Ok(new LearnMoreResponse(detail ?? "No details returned."));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI learn-more failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to fetch details.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5g) Flashcards CRUD ─────────────────────────────────────────
app.MapGet("/api/ai/flashcards", ([FromQuery] string path, IFlashcardService flashcardService) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    var flashcards = flashcardService.LoadFlashcards(path);
    return Results.Ok(flashcards);
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/ai/flashcards", async (
    HttpContext context,
    [FromBody] FlashcardRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IFlashcardService flashcardService) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Term))
        return Results.BadRequest(new { error = "Term is required." });

    var shouldSave = request.SaveToLibrary ?? true;
    if (shouldSave && string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required when saving flashcards." });

    var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");

        // Truncate very long passages to avoid overwhelming the model
        var maxInputLength = cfg.GetValue<int>("AI:MaxInputLength");
        var inputText = request.Term.Length > maxInputLength
            ? request.Term.Substring(0, maxInputLength) + "..."
            : request.Term;

        var systemPrompt = @"You are a vocabulary flashcard generator. Your job is to extract INDIVIDUAL WORDS or SHORT PHRASES from text and create a separate flashcard for EACH ONE.

CRITICAL: Extract MULTIPLE individual terms from the passage. DO NOT create a single flashcard with the entire passage. Each flashcard should be for ONE specific word or short phrase.

Return ONLY valid JSON, no markdown or explanation.

JSON Structure (ARRAY of flashcards):
[
  { ""term"": ""audacity"", ""definition"": ""bold or rude behavior"", ""etymology"": ""Latin audax (bold)"", ""usageExamples"": [""She had the audacity to criticize."", ""His audacity was shocking.""], ""notes"": """" },
  { ""term"": ""rhizome"", ""definition"": ""(philosophy) a non-hierarchical network structure, as opposed to a tree-like hierarchy"", ""etymology"": ""Greek rhizoma (mass of roots)"", ""usageExamples"": [""Deleuze uses rhizome as a metaphor."", ""A rhizomatic structure has no center.""], ""notes"": ""Specific philosophical meaning by Deleuze & Guattari"" },
  ...
]

What to extract (BE VERY SELECTIVE):
- College-level or graduate-level vocabulary (words beyond typical high school reading)
- Foreign words/phrases used in the text
- Specialized academic, philosophical, or technical terms
- Subject-specific jargon that requires domain knowledge
- Neologisms or terms with specialized meaning in this work (e.g., philosophy terms that are also common English words but have specific meaning here)
- Archaic or literary words rarely used in modern English
- Historical/cultural references requiring background knowledge

DO NOT extract:
- Common words that high school students would know (e.g., ""said"", ""walked"", ""important"", ""although"", ""necessary"")
- Basic academic words taught in high school (e.g., ""analyze"", ""demonstrate"", ""significant"")
- Simple vocabulary regardless of context

BE STRICT: Only select words that would genuinely challenge someone with a high school education or require specific domain knowledge.

Rules:
- Extract 3-10 individual terms from the passage (fewer is better than including common words)
- Each term should be a SINGLE WORD or SHORT PHRASE (2-4 words max)
- Definitions: 1-2 sentences, clear and concise (include subject-specific meaning if applicable)
- Usage examples: 2 brief sentences showing the word in context
- Etymology: Short phrase (""Unknown"" if unclear)
- Notes: Include context if the word has a specific meaning in this discipline/work";

        var knownWordsContext = request.KnownWords != null && request.KnownWords.Count > 0
            ? $"\n\nEXCLUDE these words (user already knows them): {string.Join(", ", request.KnownWords)}"
            : "";

        // Add custom context instructions if provided (for intelligent selection handling)
        var customInstructions = !string.IsNullOrWhiteSpace(request.Context)
            ? $"\n\nSPECIAL INSTRUCTIONS:\n{request.Context}\n"
            : "";

        var userPrompt = $@"Extract vocabulary terms from this passage:

""{inputText}""

Context: {request.BookTitle ?? "Unknown book"}{knownWordsContext}{customInstructions}

Return JSON array of flashcards for individual terms found in the passage.";

        var model = "gpt-4o"; // Use GPT-4o for cost-effective vocab extraction
        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:LearnMore"),
            temperature: cfg.GetValue<double>("AI:Temperature:LearnMore")
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI flashcard failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var content = aiResponseParser.ExtractText(doc.RootElement) ?? "{}";

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        List<FlashcardItem> cardsParsed;
        try
        {
            // Try to clean the content first - remove markdown code blocks if present
            var cleanedContent = content.Trim();
            if (cleanedContent.StartsWith("```"))
            {
                var lines = cleanedContent.Split('\n');
                cleanedContent = string.Join('\n', lines.Skip(1).SkipLast(1));
            }

            // Try to extract JSON array from the content
            var jsonMatch = Regex.Match(cleanedContent, @"\[[\s\S]*\]");
            if (jsonMatch.Success)
            {
                cleanedContent = jsonMatch.Value;
            }

            cardsParsed = JsonSerializer.Deserialize<List<FlashcardItem>>(cleanedContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new Exception("Invalid flashcard JSON array");

            Console.WriteLine($"✅ Successfully parsed {cardsParsed.Count} flashcards from AI response");
        }
        catch (Exception parseEx)
        {
            Console.WriteLine($"⚠️ Failed to parse flashcards as array: {parseEx.Message}");
            Console.WriteLine($"   AI response: {content.Substring(0, Math.Min(200, content.Length))}...");

            try
            {
                // Try parsing as a single object
                var single = JsonSerializer.Deserialize<FlashcardItem>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (single != null)
                {
                    cardsParsed = new List<FlashcardItem> { single };
                    Console.WriteLine($"✅ Parsed single flashcard");
                }
                else
                    throw new Exception("Invalid flashcard JSON");
            }
            catch (Exception singleEx)
            {
                Console.WriteLine($"❌ Failed to parse flashcards: {singleEx.Message}");
                // Don't create a fallback card - return empty list
                // This prevents creating giant vocab cards with entire text
                cardsParsed = new List<FlashcardItem>();
                Console.WriteLine($"⚠️ Returning empty flashcard list due to parsing failure");
            }
        }

        if (shouldSave && !string.IsNullOrWhiteSpace(request.DropboxPath))
        {
            var list = flashcardService.LoadFlashcards(request.DropboxPath);
            foreach (var card in cardsParsed)
            {
                var existing = list.FindIndex(x => string.Equals(x.Term, card.Term, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                    list[existing] = card;
                else
                    list.Add(card);
            }

            flashcardService.SaveFlashcards(request.DropboxPath, list);
        }
        return Results.Ok(new FlashcardResult(cardsParsed));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Flashcard create failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to create flashcard.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapDelete("/api/ai/flashcards", ([FromQuery] string path, IFlashcardService flashcardService) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    try
    {
        var (_, filePath) = flashcardService.GetFlashcardPath(path);
        if (System.IO.File.Exists(filePath))
            System.IO.File.Delete(filePath);
        return Results.Ok(new { cleared = true });
    }
    catch
    {
        return ApiResponse.InternalError("Failed to clear flashcards.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapDelete("/api/ai/flashcard", ([FromQuery] string path, [FromQuery] string term, IFlashcardService flashcardService) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });
    if (string.IsNullOrWhiteSpace(term))
        return Results.BadRequest(new { error = "Query parameter 'term' is required." });

    try
    {
        var deleted = flashcardService.DeleteFlashcard(path, term);
        if (deleted)
            return Results.Ok(new { deleted = true });
        return Results.NotFound(new { error = "Flashcard not found." });
    }
    catch
    {
        return ApiResponse.InternalError("Failed to delete flashcard.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5h) Wikipedia images helper ── (moved to MediaEndpoints.cs)

app.MapGet("/api/anna/dropbox/epub/search", async (
    [FromQuery] string path,
    [FromQuery] string query,
    DropboxClient dropbox) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    if (string.IsNullOrWhiteSpace(query) || query.Length < 10)
        return Results.BadRequest(new { error = "Search query must be at least 10 characters." });

    try
    {
        var matches = await DropboxEpubCache.SearchAsync(dropbox, path, query);
        return Results.Ok(matches);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to search EPUB cache: {ex.Message}");
        return ApiResponse.InternalError("Unable to search this book right now.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5i) Full-chapter summary with SSE progress ────────────────────────────────
app.MapPost("/api/ai/summarize/chapter/stream", async (
    HttpContext context,
    [FromBody] FullChapterSummaryRequest request,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection,
    ITextProcessingService textProcessing) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "dropboxPath is required." });
        return;
    }
    if (request.ChapterId < 0)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "chapterId must be zero or positive." });
        return;
    }

    if (request.ForceRegenerate)
    {
        AiContentCache.DeleteChapterSummary(request.DropboxPath, request.ChapterId);
    }

    // Check if cached summary exists
    var cached = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
    if (cached != null)
    {
        Console.WriteLine($"📦 Returning cached chapter summary for {request.DropboxPath} chapter {request.ChapterId}");
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.Append("Cache-Control", "no-cache");
        context.Response.Headers.Append("Connection", "keep-alive");

        static long ToLong(object? value)
        {
            if (value == null) return 0L;
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is double d) return (long)d;
            if (value is string s && long.TryParse(s, out var parsed)) return parsed;
            if (value is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var num)) return num;
                if (je.ValueKind == JsonValueKind.String && long.TryParse(je.GetString(), out var numFromString)) return numFromString;
            }
            return 0L;
        }

        static DateTime ToDateTime(object? value)
        {
            if (value == null) return DateTime.UtcNow;
            if (value is DateTime dt) return dt;
            if (value is string s && DateTime.TryParse(s, out var parsed)) return parsed;
            if (value is JsonElement je && je.ValueKind == JsonValueKind.String && DateTime.TryParse(je.GetString(), out var parsedJe)) return parsedJe;
            return DateTime.UtcNow;
        }

        var completeEvent = new
        {
            summary = cached.GetValueOrDefault("summary", ""),
            promptTokens = cached.TryGetValue("promptTokens", out var pt) ? ToLong(pt) : 0L,
            completionTokens = cached.TryGetValue("completionTokens", out var ct) ? ToLong(ct) : 0L,
            totalTokens = cached.TryGetValue("totalTokens", out var tt) ? ToLong(tt) : 0L,
            cachedAt = cached.TryGetValue("cachedAt", out var cachedAt) ? ToDateTime(cachedAt) : DateTime.UtcNow
        };

        await ServerSentEventsHelper.SendEventAsync(context.Response, completeEvent, "complete");
        return;
    }

    var chapterSummaryLockKey = $"chapter-summary:{request.DropboxPath}:{request.ChapterId}";
    if (!TryStartAiJob(chapterSummaryLockKey))
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "Chapter summary already in progress." });
        return;
    }

    try
    {
        // Check token limit
        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null)
        {
            await tokenLimitResult.ExecuteAsync(context);
            return;
        }

        var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "OpenAI API key not configured." });
            return;
        }

        // Set up SSE headers
        context.Response.Headers.Append("Content-Type", "text/event-stream");
        context.Response.Headers.Append("Cache-Control", "no-cache");
        context.Response.Headers.Append("Connection", "keep-alive");

        // Load chapter content using helper
        var content = await LoadChapterContentAsync(dropbox, request.DropboxPath, request.ChapterId);
        if (content is null)
        {
            await ServerSentEventsHelper.SendEventAsync(context.Response, new { message = "Chapter not found or empty." }, "error");
            return;
        }

        // Prepare context for AI
        var index = await LoadChapterIndexAsync(dropbox, request.DropboxPath);
        var chapter = index?.Chapters.FirstOrDefault(c => c.Id == request.ChapterId);

        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.BookTitle))
            contextParts.Add($"Book: {request.BookTitle}");

        // Use DisplayChapterNumber if provided (filtered chapters), otherwise fall back to ChapterId + 1
        var chapterNum = request.DisplayChapterNumber ?? (request.ChapterId + 1);
        var chapterTitle = !string.IsNullOrWhiteSpace(chapter?.Title)
            ? $"Chapter {chapterNum}: {chapter.Title}"
            : $"Chapter {chapterNum}";
        contextParts.Add(chapterTitle);
        var contextLine = string.Join(" | ", contextParts);

        // Split into chunks
        var chunkSize = cfg.GetValue<int>("AI:ChunkSize");
        var chunks = textProcessing.SplitIntoChunks(content, chunkSize);

        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelDeep();

        // TIER 1: Summarize chunks using helper
        var (chunkSummaries, tier1PromptTokens, tier1CompletionTokens) =
            await SummarizeChunksAsync(http, model, chunks, contextLine, context.Response, cfg, aiResponseParser, tokenUsage);

        // TIER 2: Synthesize sections using helper
        var (sectionSummaries, tier2PromptTokens, tier2CompletionTokens) =
            await SynthesizeSectionsAsync(http, model, chunkSummaries, contextLine, context.Response, cfg, aiResponseParser, tokenUsage);

        // TIER 3: Create final summary using helper
        var (finalSummary, tier3PromptTokens, tier3CompletionTokens) =
            await CreateFinalSummaryAsync(http, model, sectionSummaries, contextParts, context.Response, cfg, aiResponseParser);

        // Calculate total tokens
        var promptTokensTotal = tier1PromptTokens + tier2PromptTokens + tier3PromptTokens;
        var completionTokensTotal = tier1CompletionTokens + tier2CompletionTokens + tier3CompletionTokens;

        var userId = UserHelpers.GetUserIdFromContext(context);
        if (userId != null)
            tokenUsage.AddUsage(userId, (int)promptTokensTotal, (int)completionTokensTotal);
        var totals = tokenUsage.GetTotals(userId ?? "");
        var monthlyAllowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
        double? percent = null;
        long? remaining = null;
        if (monthlyAllowance.HasValue && monthlyAllowance.Value > 0)
        {
            percent = Math.Round((double)totals.TotalTokens / monthlyAllowance.Value * 100, 2);
            remaining = monthlyAllowance.Value - totals.TotalTokens;
        }

        // Save summary to cache
        var summaryData = new
        {
            summary = finalSummary,
            promptTokens = promptTokensTotal,
            completionTokens = completionTokensTotal,
            totalTokens = promptTokensTotal + completionTokensTotal,
            allowanceUsedPercent = percent,
            tokensRemaining = remaining,
            cachedAt = DateTime.UtcNow
        };

        AiContentCache.SaveChapterSummary(request.DropboxPath, request.ChapterId, summaryData);

        // Send completion event with full summary
        await ServerSentEventsHelper.SendEventAsync(context.Response, summaryData, "complete");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Full-chapter summary failed: {ex.Message}");
        await ServerSentEventsHelper.SendEventAsync(context.Response, new { message = "Failed to summarize chapter.", error = ex.Message }, "error");
    }
    finally
    {
        EndAiJob(chapterSummaryLockKey);
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Retrieve cached full-chapter summary (if any)
app.MapGet("/api/ai/summarize/chapter", (
    [FromQuery] string dropboxPath,
    [FromQuery] int chapterId) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
        return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

    var cached = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(dropboxPath, chapterId);
    if (cached == null)
        return ApiResponse.NotFound("No summary cached for this chapter.");

    return Results.Ok(cached);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Delete cached chapter summary
app.MapDelete("/api/ai/summarize/chapter", (
    [FromQuery] string dropboxPath,
    [FromQuery] int chapterId) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
        return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

    AiContentCache.DeleteChapterSummary(dropboxPath, chapterId);
    return Results.Ok(new { message = "Cached summary deleted." });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Retrieve or generate an ultra "I'm a Dummy" chapter summary
app.MapPost("/api/ai/summarize/chapter/dummy", async (
    HttpContext context,
    [FromBody] UltraChapterSummaryRequest request,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath) || request.ChapterId < 0)
        return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

    if (request.ForceRegenerate)
    {
        AiContentCache.DeleteUltraChapterSummary(request.DropboxPath, request.ChapterId);
    }

    var cached = AiContentCache.LoadUltraChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
    if (cached != null)
        return Results.Ok(cached);

    var baseSummaryData = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
    var baseSummaryText = baseSummaryData != null && baseSummaryData.TryGetValue("summary", out var summaryObj)
        ? summaryObj?.ToString()
        : null;

    if (string.IsNullOrWhiteSpace(baseSummaryText))
        return ApiResponse.NotFound("Full chapter summary is required before generating the dummy explanation.");

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    var index = await LoadChapterIndexAsync(dropbox, request.DropboxPath);
    var chapter = index?.Chapters.FirstOrDefault(c => c.Id == request.ChapterId);
    var chapterTitle = chapter?.Title;

    var contextParts = new List<string>();
    if (!string.IsNullOrWhiteSpace(request.BookTitle))
        contextParts.Add($"Book: {request.BookTitle}");

    // Use DisplayChapterNumber if provided (filtered chapters), otherwise fall back to ChapterId + 1
    var chapterNum = request.DisplayChapterNumber ?? (request.ChapterId + 1);
    if (!string.IsNullOrWhiteSpace(chapterTitle))
        contextParts.Add($"Chapter {chapterNum}: {chapterTitle}");
    else
        contextParts.Add($"Chapter {chapterNum}");

    var contextLine = contextParts.Count > 0 ? string.Join(" | ", contextParts) : "Chapter context";

    var systemPrompt = @"You are a friendly teacher who makes hard ideas feel obvious.
Write in a warm, conversational tone for a smart reader with zero background knowledge.
Use 3–5 short paragraphs. No headings, no bullet points, no numbered lists.";

    var userPrompt = $@"Explain this chapter in the clearest, most human way possible.
Focus on:
- why this matters
- what the author is really getting at
- why someone should care
- how it connects (or doesn't) to modern life

Be direct, vivid, and helpful without dumbing it down.

{contextLine}

Chapter summary:
{baseSummaryText}";

    using var http = httpFactory.CreateClient("OpenAI");
    var model = cfg["OpenAI:ModelUltra"]
        ?? Environment.GetEnvironmentVariable("OPENAI_MODEL_ULTRA")
        ?? modelSelection.GetModelDeep();

    var reasoningEffort = cfg.GetValue<string>("AI:ReasoningEffort:UltraSummary") ?? "high";
    var maxCompletion = cfg.GetValue<int?>("AI:MaxCompletionTokens:UltraChapterSummary")
        ?? cfg.GetValue<int?>("AI:MaxCompletionTokens:FullChapterSummary")
        ?? 1400;

    var payload = modelHelper.BuildChatCompletionPayload(
        model,
        new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        },
        maxCompletionTokens: maxCompletion,
        temperature: null,
        reasoningEffort: reasoningEffort
    );

    var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"❌ OpenAI ultra summary failed: {response.StatusCode}");
        Console.WriteLine($"   Response body: {body}");
        return Results.Problem($"Ultra summary failed: {(int)response.StatusCode}");
    }

    using var stream = await response.Content.ReadAsStreamAsync();
    using var doc = await JsonDocument.ParseAsync(stream);
    var summary = aiResponseParser.ExtractText(doc.RootElement);
    if (string.IsNullOrWhiteSpace(summary))
        return Results.Problem("Ultra summary response was empty.");

    var promptTokens = 0;
    var completionTokens = 0;
    if (doc.RootElement.TryGetProperty("usage", out var usage))
    {
        promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
        completionTokens = usage.GetProperty("completion_tokens").GetInt32();
        var userId = UserHelpers.GetUserIdFromContext(context);
        if (userId != null)
            tokenUsage.AddUsage(userId, promptTokens, completionTokens);
    }

    // Note: No longer calculating global allowance stats (now tracked per-user)
    var summaryData = new
    {
        summary = summary,
        promptTokens,
        completionTokens,
        totalTokens = promptTokens + completionTokens,
        cachedAt = DateTime.UtcNow
    };

    AiContentCache.SaveUltraChapterSummary(request.DropboxPath, request.ChapterId, summaryData);
    return Results.Ok(summaryData);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Retrieve cached ultra "I'm a Dummy" summary (if any)
app.MapGet("/api/ai/summarize/chapter/dummy", (
    [FromQuery] string dropboxPath,
    [FromQuery] int chapterId) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
        return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

    var cached = AiContentCache.LoadUltraChapterSummary<Dictionary<string, object>>(dropboxPath, chapterId);
    if (cached == null)
        return ApiResponse.NotFound("No dummy summary cached for this chapter.");

    return Results.Ok(cached);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Get all cached summaries for a book
app.MapGet("/api/ai/summarize/book", (
    [FromQuery] string dropboxPath) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required." });

    var summaries = AiContentCache.LoadAllChapterSummaries(dropboxPath);
    return Results.Ok(summaries);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Token usage status
app.MapGet("/api/ai/usage", (HttpContext context, IConfiguration cfg, ITokenUsageService tokenUsage) =>
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
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Get all users' AI usage
app.MapGet("/api/ai/usage/all-users", (IConfiguration cfg, ITokenUsageService tokenUsage) =>
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
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Reset token usage counter
app.MapPost("/api/ai/usage/reset", (ITokenUsageService tokenUsage) =>
{
    tokenUsage.Reset();
    Console.WriteLine("✅ Token usage counter has been reset");
    return Results.Ok(new { success = true, message = "Token usage counter has been reset" });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5j) Chunk boundaries detection with SSE progress ────────────────────────
app.MapGet("/api/ai/chunk-boundaries", async (
    HttpContext context,
    [FromQuery] string dropboxPath,
    [FromQuery] int chapterId,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "dropboxPath and valid chapterId are required." });
        return;
    }

    // Check cache first
    var cached = AiContentCache.LoadChunkBoundaries(dropboxPath, chapterId);
    if (cached != null)
    {
        Console.WriteLine($"✅ Returning cached chunk boundaries for chapter {chapterId}");
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(cached);
        return;
    }

    var chunkBoundaryLockKey = $"chunk-boundaries:{dropboxPath}:{chapterId}";
    if (!TryStartAiJob(chunkBoundaryLockKey))
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "Chunk boundary detection already in progress." });
        return;
    }

    try
    {
        // Not cached - detect boundaries with SSE progress
        Console.WriteLine($"🔍 Detecting chunk boundaries for chapter {chapterId}...");

    // Set up SSE
    context.Response.Headers["Content-Type"] = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache";
    context.Response.Headers["Connection"] = "keep-alive";

    // Check token limit
    if (TokenLimitHelpers.IsTokenLimitExceeded(cfg, tokenUsage, context))
    {
        await ServerSentEventsHelper.SendEventAsync(context.Response, new
        {
            stage = "error",
            stepNumber = 0,
            totalSteps = 1,
            message = "Monthly AI usage allowance exceeded"
        });
        return;
    }

    // Load chapter content (index if needed)
    var existingKeys = AiContentCache.GetExistingSummaryKeys();
    var isLibrary = TryResolveLibraryFileForReaderKey(dropboxPath, existingKeys, out _, out var libraryPath);
    var cacheRoot = isLibrary ? LibraryEpubCache.GetCacheRoot() : DropboxEpubCache.GetCacheRoot();
    var epubHash = isLibrary
        ? LibraryEpubCache.ComputeHashPublic(dropboxPath)
        : DropboxEpubCache.ComputeHashPublic(dropboxPath);
    var chapterPath = Path.Combine(cacheRoot, epubHash, $"chapter-{chapterId:D4}.txt");

    if (!File.Exists(chapterPath))
    {
        // Chapter not indexed - index it now
        await ServerSentEventsHelper.SendEventAsync(context.Response, new
        {
            stage = "indexing",
            stepNumber = 0,
            totalSteps = 1,
            message = "Indexing book (first time only)..."
        });
        Console.WriteLine($"📑 Chapter {chapterId} not indexed - indexing entire book now...");

        try
        {
            var cacheDir = Path.Combine(cacheRoot, epubHash);
            if (isLibrary)
            {
                await LibraryEpubCache.EnsureCacheBuildAsync(libraryPath, dropboxPath, cacheDir);
            }
            else
            {
                await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, dropboxPath, cacheDir);
            }
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "indexing",
                stepNumber = 1,
                totalSteps = 1,
                message = "Book indexed successfully"
            });
            Console.WriteLine($"✅ Book indexed successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to index book: {ex.Message}");
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "error",
                stepNumber = 0,
                totalSteps = 1,
                message = $"Failed to index book: {ex.Message}"
            });
            return;
        }

        // Verify chapter file now exists
        if (!File.Exists(chapterPath))
        {
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "error",
                stepNumber = 0,
                totalSteps = 1,
                message = "Chapter file not found after indexing"
            });
            return;
        }
    }

    var chapterText = await File.ReadAllTextAsync(chapterPath);
    var words = chapterText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    var totalWords = words.Length;

    Console.WriteLine($"📖 Chapter has {totalWords} words");

    // Estimate total chunks
    var estimatedChunks = Math.Max(1, (int)Math.Ceiling(totalWords / 500.0));
    await ServerSentEventsHelper.SendEventAsync(context.Response, new
    {
        stage = "detecting",
        stepNumber = 0,
        totalSteps = estimatedChunks,
        message = $"Analyzing {totalWords:N0} words..."
    });

    // Use GPT-4o to detect chunk boundaries
    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        await ServerSentEventsHelper.SendEventAsync(context.Response, new
        {
            stage = "error",
            stepNumber = 0,
            totalSteps = 1,
            message = "OpenAI API key not configured"
        });
        return;
    }

    var chunks = new List<ChunkBoundary>();
    var currentStart = 0;
    var targetChunkSize = 500;
    var maxChunkSize = 600;

    using var http = httpFactory.CreateClient("OpenAI");
        var model = "gpt-4o"; // Use GPT-4o for cost-effective chunking

        Console.WriteLine($"🤖 Using model for chunk detection: {model}");
        Console.WriteLine($"   Model info: {modelHelper.GetModelDescription(model)}");

        while (currentStart < totalWords)
        {
            var chunkIndex = chunks.Count + 1;
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "detecting",
                stepNumber = chunkIndex,
                totalSteps = estimatedChunks,
                message = $"Detecting section {chunkIndex} of ~{estimatedChunks}..."
            });

            // Extract text window (target 500-600 words)
            var endWord = Math.Min(currentStart + maxChunkSize, totalWords);
            var windowWords = words.Skip(currentStart).Take(endWord - currentStart).ToArray();
            var windowText = string.Join(" ", windowWords);

            if (endWord >= totalWords)
            {
                // Last chunk - just add it
                chunks.Add(new ChunkBoundary(currentStart, totalWords, totalWords - currentStart));
                break;
            }

            // Ask GPT-4o to find the best break point
            var prompt = $@"You are analyzing a section of a book chapter to find the best place to split it into readable chunks.

The text below is approximately {windowWords.Length} words. I need to split this into a chunk of around 500 words (±100 words flexibility).

Your task:
1. Read through the text and identify natural breaking points (paragraph boundaries, topic shifts, scene breaks)
2. Find the best break point between word 400 and word 600 that:
   - Ends at a paragraph boundary (double newline)
   - Completes a thought or topic
   - Does NOT cut off mid-sentence or mid-paragraph
3. Return ONLY a JSON object with the word index where the break should occur

Text to analyze:
{windowText}

Return format (JSON only, no explanation):
{{
  ""breakWordIndex"": <number between 400 and {windowWords.Length}>
}}";

            // Safely read config values with fallbacks
            int maxTokens = 100; // Default for chunk boundary detection
            double temp = 0.3;   // Default temperature
            try
            {
                maxTokens = cfg.GetValue<int>("AI:MaxCompletionTokens:ChunkBoundary", 100);
                temp = cfg.GetValue<double>("AI:Temperature:ChunkBoundary", 0.3);
            }
            catch (Exception configEx)
            {
                Console.WriteLine($"⚠️ Config read error (using defaults): {configEx.Message}");
            }

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new object[]
                {
                    new { role = "user", content = prompt }
                },
                maxCompletionTokens: maxTokens,
                temperature: temp
            );

            // Retry logic for rate limiting (429 errors)
            HttpResponseMessage? response = null;
            int maxRetries = 3;
            int retryCount = 0;
            double baseDelaySeconds = 1.5;

            while (retryCount <= maxRetries)
            {
                response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);

                if (response.IsSuccessStatusCode)
                {
                    break; // Success, exit retry loop
                }

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < maxRetries)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"⚠️  Rate limited (attempt {retryCount + 1}/{maxRetries + 1}): {body}");

                    // Extract retry-after time from error message
                    double retryAfterSeconds = baseDelaySeconds * Math.Pow(2, retryCount); // Exponential backoff
                    try
                    {
                        // Parse "Please try again in X.XXs" from error message
                        var match = System.Text.RegularExpressions.Regex.Match(body, @"try again in ([\d.]+)s");
                        if (match.Success && double.TryParse(match.Groups[1].Value, out var parsedDelay))
                        {
                            retryAfterSeconds = Math.Max(parsedDelay, retryAfterSeconds);
                        }
                    }
                    catch { /* Use exponential backoff if parsing fails */ }

                    Console.WriteLine($"⏳ Waiting {retryAfterSeconds:F2}s before retry...");
                    await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds));
                    retryCount++;
                    continue;
                }

                // Non-retryable error
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ OpenAI chunk detection failed: {errorBody}");
                await ServerSentEventsHelper.SendEventAsync(context.Response, new
                {
                    stage = "error",
                    stepNumber = chunkIndex,
                    totalSteps = estimatedChunks,
                    message = $"Detection failed: {(int)response.StatusCode}"
                });
                return;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                var body = response != null ? await response.Content.ReadAsStringAsync() : "No response";
                Console.WriteLine($"❌ OpenAI chunk detection failed after {maxRetries} retries: {body}");
                await ServerSentEventsHelper.SendEventAsync(context.Response, new
                {
                    stage = "error",
                    stepNumber = chunkIndex,
                    totalSteps = estimatedChunks,
                    message = $"Detection failed after {maxRetries} retries (rate limited)"
                });
                return;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var aiText = aiResponseParser.ExtractText(doc.RootElement);

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            // Parse the break point
            int breakPoint = targetChunkSize; // Default fallback
            if (!string.IsNullOrWhiteSpace(aiText))
            {
                Console.WriteLine($"🤖 AI response: {aiText}");
                try
                {
                    // Try to extract JSON from response (handle markdown code blocks)
                    var cleanedText = aiText.Trim();

                    // Remove markdown code blocks if present
                    if (cleanedText.StartsWith("```"))
                    {
                        var lines = cleanedText.Split('\n');
                        cleanedText = string.Join('\n', lines.Skip(1).SkipLast(1));
                    }

                    // Try direct JSON parse first
                    JsonDocument? jsonDoc = null;
                    try
                    {
                        jsonDoc = JsonDocument.Parse(cleanedText);
                    }
                    catch
                    {
                        // Fall back to regex extraction
                        var jsonMatch = Regex.Match(cleanedText, @"\{[^\}]*""breakWordIndex""[^\}]*\}");
                        if (jsonMatch.Success)
                        {
                            jsonDoc = JsonDocument.Parse(jsonMatch.Value);
                        }
                    }

                    if (jsonDoc != null && jsonDoc.RootElement.TryGetProperty("breakWordIndex", out var idx))
                    {
                        breakPoint = idx.GetInt32();
                        // Clamp to valid range
                        breakPoint = Math.Max(400, Math.Min(breakPoint, windowWords.Length));
                        Console.WriteLine($"✂️ AI suggested break at word {breakPoint}");
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ No breakWordIndex found in response, using default: {breakPoint}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Failed to parse break point: {ex.Message}, using default: {breakPoint}");
                }
            }
            else
            {
                Console.WriteLine($"⚠️ Empty AI response, using default break point: {breakPoint}");
            }

            // If we're still at default (500), try to find a paragraph boundary as fallback
            if (breakPoint == targetChunkSize)
            {
                Console.WriteLine($"⚠️ Using fallback: finding nearest paragraph boundary around word {breakPoint}");

                // Look for paragraph breaks (double newlines) near the target position
                var searchStart = Math.Max(400, breakPoint - 50);
                var searchEnd = Math.Min(windowWords.Length, breakPoint + 50);

                // Reconstruct text to find paragraph boundaries
                var searchText = string.Join(" ", windowWords.Skip(searchStart).Take(searchEnd - searchStart));
                var paragraphs = searchText.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.None);

                if (paragraphs.Length > 1)
                {
                    // Find the paragraph break closest to target position
                    var currentPos = searchStart;
                    var bestBreak = breakPoint;
                    var bestDistance = int.MaxValue;

                    foreach (var para in paragraphs.Take(paragraphs.Length - 1)) // Don't include last paragraph
                    {
                        var paraWordCount = para.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                        currentPos += paraWordCount;

                        var distance = Math.Abs(currentPos - breakPoint);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestBreak = currentPos;
                        }
                    }

                    if (bestBreak >= 400 && bestBreak <= windowWords.Length)
                    {
                        breakPoint = bestBreak;
                        Console.WriteLine($"✂️ Found paragraph boundary at word {breakPoint} (distance from target: {bestDistance})");
                    }
                }
            }

            var chunkEnd = currentStart + breakPoint;
            chunks.Add(new ChunkBoundary(currentStart, chunkEnd, chunkEnd - currentStart));
            currentStart = chunkEnd;

            Console.WriteLine($"✂️ Chunk detected: words {chunks[^1].Start}-{chunks[^1].End} ({chunks[^1].WordCount} words)");
        }

        // Save to cache
        AiContentCache.SaveChunkBoundaries(dropboxPath, chapterId, chunks);

        // Send completion event
        var result = new
        {
            chapterId,
            chunks,
            cachedAt = DateTime.UtcNow
        };
        await ServerSentEventsHelper.SendEventAsync(context.Response, result);

        Console.WriteLine($"✅ Detected {chunks.Count} sections for chapter {chapterId}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Chunk boundary detection failed: {ex.Message}");
        await ServerSentEventsHelper.SendEventAsync(context.Response, new
        {
            stage = "error",
            stepNumber = 0,
            totalSteps = 1,
            message = $"Detection failed: {ex.Message}"
        });
    }
    finally
    {
        EndAiJob(chunkBoundaryLockKey);
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5j) Get cached section summary (no generation) ─────────────────────────
app.MapGet("/api/ai/section-summary", ([FromQuery] string dropboxPath, [FromQuery] int chapterId, [FromQuery] int sectionIndex) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required." });
    if (chapterId < 0 || sectionIndex < 0)
        return Results.BadRequest(new { error = "chapterId and sectionIndex must be zero or positive." });

    var cached = AiContentCache.LoadSectionSummary(dropboxPath, chapterId, sectionIndex);
    if (cached != null)
    {
        // Load associated vocab if it exists
        var vocab = AiContentCache.LoadSectionVocab(dropboxPath, chapterId, sectionIndex);

        // Filter out known AND study words from vocab
        if (vocab != null && vocab.Count > 0)
        {
            Console.WriteLine($"🔍 [GET /api/ai/section-summary] Loading {vocab.Count} vocab cards from cache");
            var knownWords = AiContentCache.LoadKnownWords();
            var studyWords = AiContentCache.LoadStudyWordsWithBooks();
            Console.WriteLine($"📚 [GET /api/ai/section-summary] Loaded {knownWords.Count} known words and {studyWords.Count} study words from server");

            var beforeCount = vocab.Count;
            var filteredVocab = vocab.Where(card =>
            {
                var normalized = AiContentCache.NormalizeTerm(card.Term);
                var isKnown = knownWords.Contains(normalized);
                var isStudy = studyWords.ContainsKey(normalized);

                if (isKnown)
                {
                    Console.WriteLine($"  🚫 Filtering out known word: '{card.Term}' (normalized: '{normalized}')");
                }
                else if (isStudy)
                {
                    Console.WriteLine($"  🚫 Filtering out study word: '{card.Term}' (normalized: '{normalized}')");
                }

                return !isKnown && !isStudy;
            }).ToList();

            var removedCount = beforeCount - filteredVocab.Count;
            Console.WriteLine($"✅ [GET /api/ai/section-summary] Filtered vocab: {beforeCount} cards → {filteredVocab.Count} cards (removed {removedCount} known/study words)");
            vocab = filteredVocab;
        }
        else
        {
            Console.WriteLine($"ℹ️ [GET /api/ai/section-summary] No vocab to filter (vocab={vocab?.Count ?? 0})");
        }

        // Create new response with filtered vocab included
        var response = cached with { Vocab = vocab };

        Console.WriteLine($"✅ Returning cached section summary for chapter {chapterId}, section {sectionIndex} (vocab: {vocab?.Count ?? 0} cards)");
        return Results.Ok(response);
    }

    return Results.NotFound(new { error = "No cached summary found for this section." });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5k) Section summary generation using GPT-5.2 ────────────────────────────
app.MapPost("/api/ai/section-summary", async (
    HttpContext context,
    [FromBody] SectionSummaryRequest request,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required." });
    if (request.ChapterId < 0 || request.SectionIndex < 0)
        return Results.BadRequest(new { error = "chapterId and sectionIndex must be zero or positive." });

    // Check if summary already cached
    var cached = AiContentCache.LoadSectionSummary(request.DropboxPath, request.ChapterId, request.SectionIndex);
    if (cached != null)
    {
        Console.WriteLine($"✅ Returning cached section summary for chapter {request.ChapterId}, section {request.SectionIndex}");
        return Results.Ok(cached);
    }

    var sectionSummaryLockKey = $"section-summary:{request.DropboxPath}:{request.ChapterId}:{request.SectionIndex}";
    if (!TryStartAiJob(sectionSummaryLockKey))
    {
        return Results.Conflict(new { error = "Section summary already in progress." });
    }

    try
    {
        // Check token limit
        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

    // Load chunk boundaries
    var boundaries = AiContentCache.LoadChunkBoundaries(request.DropboxPath, request.ChapterId);
    if (boundaries == null || request.SectionIndex >= boundaries.Chunks.Count)
        return Results.BadRequest(new { error = "Invalid sectionIndex or chunk boundaries not detected." });

    // Load chapter content
    var existingKeys = AiContentCache.GetExistingSummaryKeys();
    var isLibrary = TryResolveLibraryFileForReaderKey(request.DropboxPath, existingKeys, out _, out var libraryPath);
    var cacheRoot = isLibrary ? LibraryEpubCache.GetCacheRoot() : DropboxEpubCache.GetCacheRoot();
    var epubHash = isLibrary
        ? LibraryEpubCache.ComputeHashPublic(request.DropboxPath)
        : DropboxEpubCache.ComputeHashPublic(request.DropboxPath);
    var chapterPath = Path.Combine(cacheRoot, epubHash, $"chapter-{request.ChapterId:D4}.txt");

    if (!File.Exists(chapterPath))
    {
        if (isLibrary)
        {
            await LibraryEpubCache.EnsureCacheBuildAsync(libraryPath, request.DropboxPath, Path.Combine(cacheRoot, epubHash));
        }
        else
        {
            await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, request.DropboxPath, Path.Combine(cacheRoot, epubHash));
        }
    }

    if (!File.Exists(chapterPath))
        return Results.NotFound(new { error = "Chapter not indexed." });

    var chapterText = await File.ReadAllTextAsync(chapterPath);
    var words = chapterText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    var chunk = boundaries.Chunks[request.SectionIndex];
    var sectionWords = words.Skip(chunk.Start).Take(chunk.WordCount).ToArray();
    var sectionText = string.Join(" ", sectionWords);

    Console.WriteLine($"📝 Generating summary for chapter {request.ChapterId}, section {request.SectionIndex} ({chunk.WordCount} words)");

    // Use GPT-5.2 (deep model) for high-quality summaries
    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("❌ OpenAI API key not configured");
        return Results.Problem("OpenAI API key not configured.");
    }

    using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelDeep();

        Console.WriteLine($"🤖 Using model: {model}");
        Console.WriteLine($"   Model info: {modelHelper.GetModelDescription(model)}");

        var bookContext = !string.IsNullOrWhiteSpace(request.BookTitle)
            ? $" from the book \"{request.BookTitle}\""
            : "";

        // Build prompt for educational, explanatory summary
        var prompt = $@"You are an expert educator explaining this text section{bookContext} to someone who wants to deeply understand it.

Provide a comprehensive summary that:

1. **What Happens**: Summarize the key events, dialogue, and developments in this section

2. **Explain Concepts**: When you encounter complex ideas, philosophical terms, or specialized vocabulary:
   - Define and explain the concept in accessible language
   - Provide historical or cultural context
   - Explain WHY this concept matters and what problem it addresses
   - Connect abstract ideas to concrete examples

3. **Clarify References**: For any historical, literary, philosophical, or cultural references:
   - Identify who/what is being referenced
   - Explain the significance and context
   - Show how it relates to the current text

4. **Thematic Analysis**: Explain the deeper meaning and themes being explored

Your goal is to make this text comprehensible and meaningful. If the section discusses abstract theory, explain it in plain language. If it references obscure ideas, provide the background needed to understand them. Assume the reader is intelligent but may not be familiar with specialized academic or philosophical concepts.

Keep your summary thorough but focused (2-5 paragraphs depending on complexity).

Text to summarize:
{sectionText}";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new object[]
            {
                new { role = "user", content = prompt }
            },
            maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:SectionSummary"),
            temperature: cfg.GetValue<double>("AI:Temperature:SectionSummary")
        );

        Console.WriteLine($"📤 Sending request to OpenAI Chat Completions API...");
        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI section summary failed: {response.StatusCode}");
            Console.WriteLine($"   Response body: {body}");
            return Results.Problem($"Section summary failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        var summary = aiResponseParser.ExtractText(doc.RootElement);
        Console.WriteLine($"✅ Summary generated: {summary?.Length ?? 0} characters");

        // Track token usage
        int promptTokens = 0, completionTokens = 0;
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            Console.WriteLine($"📊 Token usage: {promptTokens} prompt + {completionTokens} completion = {promptTokens + completionTokens} total");
        }

        // Save to cache
        var result = new SectionSummaryResponse(
            summary ?? "No summary generated.",
            request.SectionIndex,
            promptTokens,
            completionTokens,
            promptTokens + completionTokens,
            DateTime.UtcNow
        );

        AiContentCache.SaveSectionSummary(request.DropboxPath, request.ChapterId, request.SectionIndex, result);
        Console.WriteLine($"💾 Section summary cached successfully");

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Section summary generation failed: {ex.Message}");
        Console.WriteLine($"   Stack trace: {ex.StackTrace}");
        return Results.Problem("Failed to generate section summary.");
    }
    finally
    {
        EndAiJob(sectionSummaryLockKey);
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5l) Save section vocabulary to cache ────────────────────────────────────
app.MapPost("/api/ai/section-vocab", ([FromBody] SaveSectionVocabRequest request) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required." });
    if (request.ChapterId < 0 || request.SectionIndex < 0)
        return Results.BadRequest(new { error = "chapterId and sectionIndex must be zero or positive." });
    if (request.Vocab == null)
        return Results.BadRequest(new { error = "vocab is required." });

    Console.WriteLine($"💾 Saving {request.Vocab.Count} vocab cards for chapter {request.ChapterId}, section {request.SectionIndex}");

    AiContentCache.SaveSectionVocab(request.DropboxPath, request.ChapterId, request.SectionIndex, request.Vocab);

    return Results.Ok(new { success = true, vocabCount = request.Vocab.Count });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5m-5r2) Vocab endpoints (moved to VocabEndpoints.cs) ─────────────────────

// ─── 5s) Suggest authors for a book title ────────────────────────────────────
app.MapPost("/api/ai/suggest-authors", async (
    HttpContext context,
    [FromBody] SuggestAuthorsRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.BookTitle))
        return Results.BadRequest(new { error = "BookTitle is required." });

    try
    {
        var forceOpenAi = false;
        if (context.Request.Headers.TryGetValue("x-force-openai", out var forceHeader))
        {
            forceOpenAi = string.Equals(forceHeader.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        if (!forceOpenAi)
        {
            var openLibraryAuthors = await FetchAuthorsFromOpenLibraryAsync(request.BookTitle, httpFactory);
            if (openLibraryAuthors.Count > 0)
            {
                Console.WriteLine($"✅ Author suggestions (OpenLibrary) for '{request.BookTitle}': {openLibraryAuthors.Count} authors found");
                return Results.Ok(new SuggestAuthorsResponse(openLibraryAuthors));
            }
        }

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelFast();  // Uses gpt-4o by default
        if (context.Request.Headers.TryGetValue("x-openai-model", out var modelHeader))
        {
            var overrideModel = modelHeader.ToString();
            if (!string.IsNullOrWhiteSpace(overrideModel) &&
                overrideModel.StartsWith("gpt-4", StringComparison.OrdinalIgnoreCase))
            {
                model = overrideModel;
            }
        }

        var systemPrompt = @"You are a book metadata expert. Given a book title, suggest the 3-5 most likely authors sorted by probability. Return ONLY valid JSON with no markdown, explanation, or additional text.";

        var userPrompt = $@"Book title: ""{request.BookTitle}""

Return ONLY a JSON array of likely authors sorted by probability (most likely first). Each entry should have ""author"" (full name) and ""confidence"" (high/medium/low).

Example format:
[
  {{""author"": ""J.R.R. Tolkien"", ""confidence"": ""high""}},
  {{""author"": ""Christopher Tolkien"", ""confidence"": ""medium""}}
]

If the title is ambiguous or you don't recognize it, return an empty array: []

Do NOT include any markdown formatting, explanations, or text outside the JSON array.";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: 500,
            temperature: 0.3
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI suggest-authors failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        // Parse the JSON array of authors
        var authors = new List<AuthorSuggestion>();
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            try
            {
                // Remove markdown code blocks if present
                var cleanedText = rawText.Trim();
                if (cleanedText.StartsWith("```"))
                {
                    cleanedText = cleanedText
                        .Replace("```json", "")
                        .Replace("```", "")
                        .Trim();
                }

                // If the model adds extra text, extract the JSON array.
                var arrayMatch = Regex.Match(cleanedText, @"\[[\s\S]*\]");
                var jsonPayload = arrayMatch.Success ? arrayMatch.Value : cleanedText;

                var authorsDoc = JsonDocument.Parse(jsonPayload);
                if (authorsDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in authorsDoc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("author", out var authorProp) &&
                            item.TryGetProperty("confidence", out var confidenceProp))
                        {
                            authors.Add(new AuthorSuggestion(
                                authorProp.GetString() ?? "",
                                confidenceProp.GetString() ?? "low"
                            ));
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"⚠️ Failed to parse author suggestions JSON: {ex.Message}");
                Console.WriteLine($"Raw text: {rawText}");
                // Return empty array on parse failure
            }
        }

        Console.WriteLine($"✅ Author suggestions for '{request.BookTitle}': {authors.Count} authors found");
        return Results.Ok(new SuggestAuthorsResponse(authors));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI suggest-authors failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to suggest authors.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5n) Find related books (series + other series by author) ────────────────
app.MapPost("/api/ai/related-books", async (
    HttpContext context,
    [FromBody] RelatedBooksRequest request,
    AnnaArchiveService annaArchiveService,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection,
    IGoogleBooksService googleBooks,
    IOpenLibraryService openLibrary) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.BookTitle) || string.IsNullOrWhiteSpace(request.Author))
        return Results.BadRequest(new { error = "BookTitle and Author are required." });

    var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelFast();

        var systemPrompt = @"You are a literary expert with comprehensive knowledge of book series and author bibliographies. Given a book title and author, identify related books. Return ONLY valid JSON with no markdown or explanations.";

        var userPrompt = $@"Book: ""{request.BookTitle}"" by {request.Author}

Provide:
1. A summary of the current series (if this book is part of a series)
2. Other books in the SAME SERIES (if this book is part of a series)
3. OTHER SERIES by this author (different series they've written) with ALL books in each series

Return ONLY this JSON structure:
{{
  ""seriesSummary"": ""A 2-3 sentence overview of the current series, its themes, and significance. Null if not part of a series."",
  ""sameSeries"": [
    {{""title"": ""Book Title"", ""order"": 1, ""description"": ""Brief 1-line description""}}
  ],
  ""seriesName"": ""Series Name (optional)"",
  ""seriesSearchQuery"": ""Search query to find series books (optional)"",
  ""otherSeries"": [
    {{
      ""seriesName"": ""Series Name"",
      ""bookCount"": 3,
      ""books"": [
        {{""title"": ""Book 1 Title"", ""order"": 1, ""description"": ""Brief description""}}
      ],
      ""description"": ""Brief 1-line description of series"",
      ""summary"": ""2-3 sentence overview of this series""
    }}
  ]
}}

Rules:
- If the book is NOT part of a series, return null for seriesSummary
- If the series has MANY books, still return ALL known published titles (no ellipses)
- If you cannot list all titles, set seriesName and seriesSearchQuery for lookup
- For otherSeries, include ALL books in each series in the ""books"" array
- Only include PUBLISHED books (no unreleased/rumored books)
- Sort all books by publication/reading order
- For otherSeries, include 3-5 most notable series
- Each series summary should be 2-3 sentences covering themes, plot arc, and significance
- Keep individual book descriptions concise (max 15 words)
- Return ONLY the JSON object, no markdown formatting";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: 3500,
            temperature: 0.3
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI related-books failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        // Parse the JSON response
        var sameSeries = new List<SeriesBook>();
        var otherSeries = new List<AuthorSeries>();
        string? seriesName = null;
        string? seriesSearchQuery = null;
        string? seriesSummary = null;

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            try
            {
                // Remove markdown code blocks if present
                var cleanedText = rawText.Trim();
                if (cleanedText.StartsWith("```"))
                {
                    cleanedText = cleanedText
                        .Replace("```json", "")
                        .Replace("```", "")
                        .Trim();
                }

                var relatedDoc = JsonDocument.Parse(cleanedText);

                // Parse seriesSummary
                if (relatedDoc.RootElement.TryGetProperty("seriesSummary", out var summaryProp) &&
                    summaryProp.ValueKind == JsonValueKind.String)
                {
                    seriesSummary = summaryProp.GetString();
                }

                // Parse sameSeries
                if (relatedDoc.RootElement.TryGetProperty("sameSeries", out var sameSeriesArray) &&
                    sameSeriesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in sameSeriesArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("title", out var titleProp))
                        {
                            sameSeries.Add(new SeriesBook(
                                titleProp.GetString() ?? "",
                                item.TryGetProperty("order", out var orderProp) ? orderProp.GetInt32() : 0,
                                item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "",
                                null  // CoverUrl will be populated later
                            ));
                        }
                    }
                }

                if (relatedDoc.RootElement.TryGetProperty("seriesName", out var seriesNameProp) &&
                    seriesNameProp.ValueKind == JsonValueKind.String)
                {
                    seriesName = seriesNameProp.GetString();
                }

                if (relatedDoc.RootElement.TryGetProperty("seriesSearchQuery", out var seriesSearchProp) &&
                    seriesSearchProp.ValueKind == JsonValueKind.String)
                {
                    seriesSearchQuery = seriesSearchProp.GetString();
                }

                // Parse otherSeries
                if (relatedDoc.RootElement.TryGetProperty("otherSeries", out var otherSeriesArray) &&
                    otherSeriesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in otherSeriesArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("seriesName", out var nameProp))
                        {
                            // Parse books array for this series
                            var seriesBooks = new List<SeriesBook>();
                            if (item.TryGetProperty("books", out var booksArray) &&
                                booksArray.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var book in booksArray.EnumerateArray())
                                {
                                    if (book.TryGetProperty("title", out var bookTitleProp))
                                    {
                                        seriesBooks.Add(new SeriesBook(
                                            bookTitleProp.GetString() ?? "",
                                            book.TryGetProperty("order", out var bookOrderProp) ? bookOrderProp.GetInt32() : 0,
                                            book.TryGetProperty("description", out var bookDescProp) ? bookDescProp.GetString() ?? "" : "",
                                            null  // CoverUrl will be populated later
                                        ));
                                    }
                                }
                            }

                            otherSeries.Add(new AuthorSeries(
                                nameProp.GetString() ?? "",
                                item.TryGetProperty("bookCount", out var countProp) ? countProp.GetInt32() : seriesBooks.Count,
                                seriesBooks,
                                item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "",
                                item.TryGetProperty("summary", out var seriesSummaryProp) ? seriesSummaryProp.GetString() ?? "" : ""
                            ));
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"⚠️ Failed to parse related books JSON: {ex.Message}");
                Console.WriteLine($"Raw text: {rawText}");
            }
        }

        if (sameSeries.Count < 15)
        {
            string Normalize(string value) =>
                Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

            var query = seriesSearchQuery ?? seriesName ?? $"{request.BookTitle} {request.Author}";
            try
            {
                var searchResults = await annaArchiveService.SearchAsync(query, 80, exact: false);
                var normalizedAuthor = Normalize(request.Author);
                var normalizedSeries = Normalize(seriesName ?? request.BookTitle);

                var matches = searchResults
                    .Where(b => b.Authors.Any(a => Normalize(a).Contains(normalizedAuthor)))
                    .Select(b => b.Title)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct()
                    .Where(t => Normalize(t!).Contains(normalizedSeries))
                    .Select((t, index) => new SeriesBook(t!, index + 1, "", null))
                    .ToList();

                if (matches.Count > sameSeries.Count)
                {
                    sameSeries = matches;
                    Console.WriteLine($"✅ Series expanded via search: {matches.Count} titles");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Series expansion failed: {ex.Message}");
            }
        }

        // ───────── Fetch descriptions (Google Books -> OpenLibrary -> GPT-4) ─────────
        Console.WriteLine("[Books API] Fetching descriptions for books...");

        // Process sameSeries books
        for (int i = 0; i < sameSeries.Count; i++)
        {
            var book = sameSeries[i];

            // Only fetch if description is missing or very short
            if (string.IsNullOrWhiteSpace(book.Description) || book.Description.Length < 10)
            {
                // Try Google Books first
                var gbDescription = await googleBooks.GetBookDescriptionAsync(book.Title, request.Author);

                if (!string.IsNullOrWhiteSpace(gbDescription))
                {
                    sameSeries[i] = new SeriesBook(book.Title, book.Order, gbDescription, book.CoverUrl, "googlebooks");
                    Console.WriteLine($"[GoogleBooks] ✓ Got description for '{book.Title}'");
                }
                else
                {
                    // Fallback to OpenLibrary
                    var olDescription = await openLibrary.GetBookDescriptionAsync(book.Title, request.Author);

                    if (!string.IsNullOrWhiteSpace(olDescription))
                    {
                        sameSeries[i] = new SeriesBook(book.Title, book.Order, olDescription, book.CoverUrl, "openlibrary");
                        Console.WriteLine($"[OpenLibrary] ✓ Got description for '{book.Title}'");
                    }
                    else
                    {
                        // Fallback to GPT-4 generated no-spoiler description
                        var gptDescription = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                            book.Title, request.Author, http, model, modelHelper, aiResponseParser);
                        sameSeries[i] = new SeriesBook(book.Title, book.Order, gptDescription, book.CoverUrl, "gpt");
                        Console.WriteLine($"[GPT-4] ✓ Generated description for '{book.Title}'");
                    }
                }
            }
        }

        // Process otherSeries books
        for (int i = 0; i < otherSeries.Count; i++)
        {
            var series = otherSeries[i];
            var updatedBooks = new List<SeriesBook>();

            foreach (var book in series.Books)
            {
                if (string.IsNullOrWhiteSpace(book.Description) || book.Description.Length < 10)
                {
                    // Try Google Books first
                    var gbDescription = await googleBooks.GetBookDescriptionAsync(book.Title, request.Author);

                    if (!string.IsNullOrWhiteSpace(gbDescription))
                    {
                        updatedBooks.Add(new SeriesBook(book.Title, book.Order, gbDescription, book.CoverUrl, "googlebooks"));
                        Console.WriteLine($"[GoogleBooks] ✓ Got description for '{book.Title}'");
                    }
                    else
                    {
                        // Fallback to OpenLibrary
                        var olDescription = await openLibrary.GetBookDescriptionAsync(book.Title, request.Author);

                        if (!string.IsNullOrWhiteSpace(olDescription))
                        {
                            updatedBooks.Add(new SeriesBook(book.Title, book.Order, olDescription, book.CoverUrl, "openlibrary"));
                            Console.WriteLine($"[OpenLibrary] ✓ Got description for '{book.Title}'");
                        }
                        else
                        {
                            var gptDescription = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                                book.Title, request.Author, http, model, modelHelper, aiResponseParser);
                            updatedBooks.Add(new SeriesBook(book.Title, book.Order, gptDescription, book.CoverUrl, "gpt"));
                            Console.WriteLine($"[GPT-4] ✓ Generated description for '{book.Title}'");
                        }
                    }
                }
                else
                {
                    updatedBooks.Add(book);
                }
            }

            otherSeries[i] = new AuthorSeries(
                series.SeriesName,
                series.BookCount,
                updatedBooks,
                series.Description,
                series.Summary
            );
        }

        Console.WriteLine($"✅ Related books for '{request.BookTitle}': {sameSeries.Count} series books, {otherSeries.Count} other series");

        return Results.Ok(new RelatedBooksResponse(sameSeries, otherSeries, seriesSummary));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI related-books failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to get related books.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5n-2) AI book search (freeform query) ───────────────────────────────
app.MapPost("/api/ai/book-search", async (
    HttpContext context,
    [FromBody] AiBookSearchRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection,
    IGoogleBooksService googleBooks,
    IOpenLibraryService openLibrary,
    CancellationToken cancellationToken) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Query))
        return Results.BadRequest(new { error = "query is required." });

    var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelDeep();
        var hasUrl = request.Query.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || request.Query.Contains("https://", StringComparison.OrdinalIgnoreCase);
        var extractedTitles = hasUrl
            ? await BookTitleExtractionHelpers.ExtractBookTitlesFromQueryAsync(request.Query, httpFactory, cancellationToken)
            : new List<string>();
        var hasExtractedTitles = extractedTitles.Count > 0;
        var maxResults = hasExtractedTitles
            ? Math.Min(20, extractedTitles.Count)
            : 20;
        var perBookWordLimit = hasExtractedTitles && extractedTitles.Count >= 60 ? 24 : 45;

        var systemPrompt = @"You are a book discovery assistant. Determine whether the user query is asking for books.
If it is, return a list of relevant books with an engaging, spoiler-free summary of the search.
Return ONLY valid JSON with no markdown or extra text.";

        var extractedBlock = hasExtractedTitles
            ? $"ExtractedTitles (from the URL):\n- {string.Join("\n- ", extractedTitles.Take(100))}\n"
            : "ExtractedTitles: None\n";

        var userPrompt = $@"Query: ""{request.Query}""
{extractedBlock}

Return ONLY this JSON structure:
{{
  ""isBookQuery"": boolean,
  ""message"": string|null,
  ""summary"": string|null,
  ""books"": [
    {{
      ""title"": ""Book title"",
      ""author"": ""Author name"",
      ""summary"": ""Spoiler-free note on what makes this book special (2-3 sentences)"",
      ""importance"": ""Context/impact (historical, critical acclaim, cultural influence; 1 sentence)""
    }}
  ]
}}

Rules:
- If the query is NOT about books, set isBookQuery=false and return a brief message.
- If ExtractedTitles are provided, return those titles in that order and fill in author if known; do not invent titles not present.
- If ExtractedTitles are not provided, return up to {maxResults} books when the query includes a URL or asks for a list; otherwise return 10-25.
- Make the summary 2-3 sentences, spoiler-free, and engaging (max 80 words).
- The summary should briefly explain what the list represents and why it's notable (e.g., award significance, era, genre influence).
- Keep each book summary and importance concise (max {perBookWordLimit} words each).";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: hasUrl ? 6000 : 2000,
            temperature: 0.3
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI book-search failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        if (string.IsNullOrWhiteSpace(rawText))
            return Results.Problem("AI search returned empty response.");

        var cleaned = rawText.Trim();
        if (cleaned.StartsWith("```"))
        {
            cleaned = cleaned
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();
        }

        JsonDocument resultDoc;
        try
        {
            resultDoc = JsonDocument.Parse(cleaned);
        }
        catch (Exception ex)
        {
            var rawPreview = rawText.Length > 2000 ? rawText[..2000] + "…" : rawText;
            var cleanPreview = cleaned.Length > 2000 ? cleaned[..2000] + "…" : cleaned;
            Console.WriteLine($"❌ AI book-search JSON parse failed: {ex.Message}");
            Console.WriteLine($"❌ AI book-search raw preview: {rawPreview}");
            Console.WriteLine($"❌ AI book-search cleaned preview: {cleanPreview}");
            return Results.BadRequest(new { error = "AI response could not be parsed. Try again or simplify the query." });
        }

        var root = resultDoc.RootElement;

        var isBookQuery = root.TryGetProperty("isBookQuery", out var bookProp) && bookProp.ValueKind == JsonValueKind.True;
        if (!isBookQuery)
        {
            var message = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "Query is not about books.";
            return Results.BadRequest(new { error = message ?? "Query is not about books." });
        }

        var summary = root.TryGetProperty("summary", out var summaryProp) ? summaryProp.GetString() : null;
        var books = new List<AiBookSearchItem>();

        if (root.TryGetProperty("books", out var booksProp) && booksProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in booksProp.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                var gptSummary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                var importance = item.TryGetProperty("importance", out var i) ? i.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(title)) continue;

                // Try Google Books -> OpenLibrary -> GPT-4
                string bookSummary = gptSummary;
                string? descriptionSource = null;

                // Try Google Books first
                var gbDescription = await googleBooks.GetBookDescriptionAsync(title, author);

                if (!string.IsNullOrWhiteSpace(gbDescription))
                {
                    bookSummary = gbDescription;
                    descriptionSource = "googlebooks";
                    Console.WriteLine($"[GoogleBooks] ✓ Got description for '{title}' by {author}");
                }
                else
                {
                    // Fallback to OpenLibrary
                    var olDescription = await openLibrary.GetBookDescriptionAsync(title, author);

                    if (!string.IsNullOrWhiteSpace(olDescription))
                    {
                        bookSummary = olDescription;
                        descriptionSource = "openlibrary";
                        Console.WriteLine($"[OpenLibrary] ✓ Got description for '{title}' by {author}");
                    }
                    else if (string.IsNullOrWhiteSpace(gptSummary))
                    {
                        // If all sources failed, generate a fallback
                        bookSummary = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                            title, author, http, model, modelHelper, aiResponseParser);
                        descriptionSource = "gpt";
                        Console.WriteLine($"[GPT-4] ✓ Generated fallback description for '{title}'");
                    }
                    else
                    {
                        // Use GPT summary as fallback
                        descriptionSource = "gpt";
                        Console.WriteLine($"[GPT-4] ✓ Using GPT-generated description for '{title}' (no external sources)");
                    }
                }

                var coverUrl = await openLibrary.GetCoverUrlAsync(title, author)
                               ?? await googleBooks.GetCoverUrlAsync(title, author);

                books.Add(new AiBookSearchItem(title, author, bookSummary, importance, coverUrl, descriptionSource));
            }
        }

        if (books.Count == 0 && !hasExtractedTitles)
        {
            var retryPrompt = $@"Query: ""{request.Query}""

Return ONLY this JSON structure:
{{
  ""isBookQuery"": true,
  ""message"": null,
  ""summary"": string|null,
  ""books"": [
    {{
      ""title"": ""Book title"",
      ""author"": ""Author name"",
      ""summary"": ""Spoiler-free note on what makes this book special (2-3 sentences)"",
      ""importance"": ""Context/impact (historical, critical acclaim, cultural influence; 1 sentence)""
    }}
  ]
}}

Rules:
- You MUST return 10-20 books. Do not return an empty list.
- Make the summary 2-3 sentences, spoiler-free, and engaging (max 80 words).
- Keep each book summary and importance concise (max {perBookWordLimit} words each).";

            var retryPayload = modelHelper.BuildChatCompletionPayload(
                "gpt-4o",
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = retryPrompt }
                },
                maxCompletionTokens: 2500,
                temperature: 0.4
            );

            var retryResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", retryPayload, cancellationToken);
            if (retryResponse.IsSuccessStatusCode)
            {
                using var retryStream = await retryResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var retryDoc = await JsonDocument.ParseAsync(retryStream, cancellationToken: cancellationToken);
                var retryText = aiResponseParser.ExtractText(retryDoc.RootElement);
                if (!string.IsNullOrWhiteSpace(retryText))
                {
                    var retryClean = retryText.Trim();
                    if (retryClean.StartsWith("```"))
                    {
                        retryClean = retryClean
                            .Replace("```json", "")
                            .Replace("```", "")
                            .Trim();
                    }

                    var retryResultDoc = JsonDocument.Parse(retryClean);
                    var retryRoot = retryResultDoc.RootElement;
                    summary = retryRoot.TryGetProperty("summary", out var retrySummary) ? retrySummary.GetString() : summary;

                    if (retryRoot.TryGetProperty("books", out var retryBooks) && retryBooks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in retryBooks.EnumerateArray())
                        {
                            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                            var author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                            var gptSummary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                            var importance = item.TryGetProperty("importance", out var i) ? i.GetString() ?? "" : "";

                            if (string.IsNullOrWhiteSpace(title)) continue;

                            // Try Google Books -> OpenLibrary -> GPT-4 (retry path)
                            string bookSummary = gptSummary;
                            string? descriptionSource = null;

                            // Try Google Books first
                            var gbDescription = await googleBooks.GetBookDescriptionAsync(title, author);

                            if (!string.IsNullOrWhiteSpace(gbDescription))
                            {
                                bookSummary = gbDescription;
                                descriptionSource = "googlebooks";
                                Console.WriteLine($"[GoogleBooks] ✓ Got description for '{title}' by {author} (retry)");
                            }
                            else
                            {
                                // Fallback to OpenLibrary
                                var olDescription = await openLibrary.GetBookDescriptionAsync(title, author);

                                if (!string.IsNullOrWhiteSpace(olDescription))
                                {
                                    bookSummary = olDescription;
                                    descriptionSource = "openlibrary";
                                    Console.WriteLine($"[OpenLibrary] ✓ Got description for '{title}' by {author} (retry)");
                                }
                                else if (string.IsNullOrWhiteSpace(gptSummary))
                                {
                                    // If all sources failed, generate a fallback
                                    bookSummary = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                                        title, author, http, model, modelHelper, aiResponseParser);
                                    descriptionSource = "gpt";
                                    Console.WriteLine($"[GPT-4] ✓ Generated fallback description for '{title}' (retry)");
                                }
                                else
                                {
                                    // Use GPT summary as fallback
                                    descriptionSource = "gpt";
                                    Console.WriteLine($"[GPT-4] ✓ Using GPT-generated description for '{title}' (retry, no external sources)");
                                }
                            }

                            var coverUrl = await openLibrary.GetCoverUrlAsync(title, author)
                                           ?? await googleBooks.GetCoverUrlAsync(title, author);

                            books.Add(new AiBookSearchItem(title, author, bookSummary, importance, coverUrl, descriptionSource));
                        }
                    }
                }
            }
        }

        return Results.Ok(new AiBookSearchResponse(summary, books));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI book-search failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to run AI book search.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5o) Match series books intelligently using GPT ─────────────────────────
app.MapPost("/api/ai/match-series-books", async (
    HttpContext context,
    [FromBody] MatchSeriesBooksRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || request.Books is null || request.Books.Count == 0)
        return Results.BadRequest(new { error = "Books list is required." });

    var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelFast();

        // Build a comprehensive prompt with all search results
        var booksJson = System.Text.Json.JsonSerializer.Serialize(request.Books, new JsonSerializerOptions { WriteIndented = true });

        var systemPrompt = @"You are an expert book matcher. You analyze search results from a library database and select the best match for each book in a series.

Your task: For each book, examine all search result candidates and select the BEST match based on:
1. Title match (handle variations like subtitles, series numbers in parentheses)
2. Author match (exact or close match)
3. Format match (if specified)
4. Detect and AVOID: Omnibus editions, anthologies, collections, combined volumes
5. Prefer standalone individual books over compilations

Return ONLY valid JSON with no markdown or explanation.";

        var userPrompt = $@"Series: ""{request.SeriesName ?? "Unknown Series"}""
Author: ""{request.Author}""
Preferred Format: ""{request.PreferredFormat ?? "ANY"}""

For each book below, I'm providing the title we're looking for and the search results. Select the BEST candidate or flag if no good match exists.

Books and Search Results:
{booksJson}

Return ONLY this JSON structure:
{{
  ""matches"": [
    {{
      ""bookTitle"": ""Book title we searched for"",
      ""order"": 1,
      ""status"": ""matched|ambiguous|not_found"",
      ""selectedMd5"": ""md5_of_best_match"",
      ""selectedTitle"": ""Full title from search results"",
      ""confidence"": ""exact|likely|uncertain"",
      ""reason"": ""Brief explanation (e.g., 'Exact title and author match', 'Anthology detected', etc.)""
    }}
  ]
}}

Rules:
- status: ""matched"" if you found a good match, ""ambiguous"" if multiple viable options, ""not_found"" if no good match
- confidence: ""exact"" for perfect matches, ""likely"" for close matches, ""uncertain"" if you're not sure
- ALWAYS avoid omnibus/anthology editions unless that's the ONLY option
- If a book has ""(Books 1-3)"" or ""Complete Series"" in the title, flag it as ambiguous or not_found
- Match format if specified (e.g., only select EPUB if format is EPUB)";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: 2000,
            temperature: 0.2
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI match-series-books failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        // Parse the JSON response
        var matches = new List<SeriesBookMatch>();

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            try
            {
                // Remove markdown code blocks if present
                var cleanedText = rawText.Trim();
                if (cleanedText.StartsWith("```"))
                {
                    cleanedText = cleanedText
                        .Replace("```json", "")
                        .Replace("```", "")
                        .Trim();
                }

                var matchDoc = JsonDocument.Parse(cleanedText);

                if (matchDoc.RootElement.TryGetProperty("matches", out var matchesArray) &&
                    matchesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in matchesArray.EnumerateArray())
                    {
                        matches.Add(new SeriesBookMatch(
                            item.TryGetProperty("bookTitle", out var bt) ? bt.GetString() ?? "" : "",
                            item.TryGetProperty("order", out var ord) ? ord.GetInt32() : 0,
                            item.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                            item.TryGetProperty("selectedMd5", out var md5) ? md5.GetString() : null,
                            item.TryGetProperty("selectedTitle", out var title) ? title.GetString() : null,
                            item.TryGetProperty("confidence", out var conf) ? conf.GetString() ?? "" : "",
                            item.TryGetProperty("reason", out var rsn) ? rsn.GetString() ?? "" : ""
                        ));
                    }
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"⚠️ Failed to parse series match JSON: {ex.Message}");
                Console.WriteLine($"Raw text: {rawText}");
            }
        }

        Console.WriteLine($"✅ Matched {matches.Count(m => m.Status == "matched")} of {request.Books.Count} books");
        return Results.Ok(new MatchSeriesBooksResponse(matches));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI match-series-books failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to match series books.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── Character Graph Generation ─────────────────────────────────────────────

app.MapPost("/api/ai/characters/graph", async (
    HttpContext context,
    [FromBody] CharacterGraphRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser) =>
{
    if (string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "DropboxPath is required." });

    Console.WriteLine($"📊 Generating character graph for {request.BookTitle ?? request.DropboxPath}...");

    // Gather all existing summaries (both chapter and section) for this book
    var chapterSummaries = AiContentCache.GetAllChapterSummariesAsStrings(request.DropboxPath);
    var sectionSummaries = AiContentCache.GetAllSectionSummaries(request.DropboxPath);

    if (chapterSummaries.Count == 0 && sectionSummaries.Count == 0)
    {
        Console.WriteLine("⚠️ No summaries found. Generate some chapter or section summaries first.");
        return Results.BadRequest(new { error = "No summaries found. Please generate chapter or section summaries as you read the book first." });
    }

    Console.WriteLine($"📚 Found {chapterSummaries.Count} chapter summaries and {sectionSummaries.Count} section summaries to analyze");

    // Combine all summaries
    var allSummaries = new List<string>();
    allSummaries.AddRange(chapterSummaries);
    allSummaries.AddRange(sectionSummaries);
    var totalSummaryCount = allSummaries.Count;

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");

        // Build consolidated summary text
        var summaryText = string.Join("\n\n---\n\n", allSummaries.Select((s, i) =>
            $"Summary {i + 1}:\n{s}"));

        var systemPrompt = @"You are a character relationship analyzer for novels. Analyze the provided story summaries and create a network graph of character relationships.

IMPORTANT: Only include information that appears in the provided summaries. Do not add or infer information beyond what's explicitly mentioned.

Return ONLY valid JSON, no markdown, no code blocks.

JSON Structure:
{
  ""nodes"": [
    {
      ""id"": ""zhao"",
      ""label"": ""Adm. Zhao"",
      ""description"": ""Brief role (2-5 words)"",
      ""detailedDescription"": ""Detailed description of who they are, what they've done so far, their motivations and characteristics based ONLY on the summaries provided (2-3 sentences)""
    }
  ],
  ""edges"": [
    {
      ""from"": ""zhao"",
      ""to"": ""miller"",
      ""label"": ""relationship type (friend/enemy/spouse/etc.)"",
      ""detailedDescription"": ""Detailed description of their relationship and key interactions based ONLY on the summaries provided (1-2 sentences)""
    }
  ]
}

CRITICAL: The ""from"" and ""to"" fields in edges MUST use the simplified lowercase IDs, NOT the character labels.
Example: If a node has id=""zhao"" and label=""Adm. Zhao"", the edge must use ""zhao"", not ""Adm. Zhao"".

Rules:
- Include main and important secondary characters (5-15 characters max)
- Only include characters that appear in the provided summaries
- Character names MUST be properly capitalized (first letter of each word uppercase)
- If a character has a military/professional title (Admiral, Captain, Lieutenant, Sergeant, Doctor, etc.), include the abbreviated title before their name:
  * Admiral → Adm.
  * Captain → Capt.
  * Lieutenant → Lt.
  * Sergeant → Sgt.
  * Colonel → Col.
  * Doctor → Dr.
  * Professor → Prof.
  * Example: ""Adm. Zhao"", ""Capt. Miller"", ""Dr. Smith""
- Relationship labels should be concise
- Detailed descriptions should cite specific events from the summaries
- The ""id"" field should be a simplified lowercase version without titles (e.g., ""zhao"", ""miller"", ""smith"")
- Do NOT reveal information that hasn't appeared in the summaries";

        var userPrompt = $@"Analyze the characters and their relationships from these story summaries:

Book: {request.BookTitle ?? "Unknown"}

Story Summaries:
{summaryText}

Create a character relationship network graph based ONLY on information in these summaries.";

        var model = "gpt-4o"; // Use GPT-4o for cost-effective character graph generation
        Console.WriteLine($"🤖 Using model for character graph: {model}");

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:CharacterGraph"),
            temperature: cfg.GetValue<double>("AI:Temperature:CharacterGraph")
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ Character graph failed: {response.StatusCode}");
            return Results.Problem($"Character graph generation failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var content = aiResponseParser.ExtractText(doc.RootElement);

        if (string.IsNullOrWhiteSpace(content))
        {
            Console.WriteLine("❌ No content returned from GPT");
            return Results.Problem("No character graph data returned.");
        }

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        // Parse the character graph JSON
        try
        {
            using var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            var nodesJson = root.GetProperty("nodes").GetRawText();
            var edgesJson = root.GetProperty("edges").GetRawText();

            var nodes = JsonSerializer.Deserialize<List<CharacterNode>>(nodesJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<CharacterNode>();

            var edges = JsonSerializer.Deserialize<List<CharacterEdge>>(edgesJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<CharacterEdge>();

            // Create response with metadata
            var graph = new CharacterGraphResponse(nodes, edges, totalSummaryCount, DateTime.UtcNow);

            // Save to cache
            AiContentCache.SaveCharacterGraph(request.DropboxPath, graph);
            Console.WriteLine($"✅ Character graph generated with {graph.Nodes.Count} characters and {graph.Edges.Count} relationships from {totalSummaryCount} summaries ({chapterSummaries.Count} chapter + {sectionSummaries.Count} section)");

            return Results.Ok(graph);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to parse character graph: {ex.Message}");
            Console.WriteLine($"   Content: {content.Substring(0, Math.Min(200, content.Length))}");
            return Results.Problem("Failed to parse character graph data.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Character graph generation failed: {ex.Message}");
        return Results.Problem("Failed to generate character graph.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/ai/characters/graph", ([FromQuery] string dropboxPath) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath))
        return Results.BadRequest(new { error = "Query parameter 'dropboxPath' is required." });

    var graph = AiContentCache.LoadCharacterGraph(dropboxPath);
    if (graph == null)
        return Results.NotFound(new { error = "No character graph found. Generate one first." });

    // Check if the graph is stale (has fewer summaries than currently exist)
    var currentChapterSummaries = AiContentCache.GetAllChapterSummariesAsStrings(dropboxPath);
    var currentSectionSummaries = AiContentCache.GetAllSectionSummaries(dropboxPath);
    var currentTotalCount = currentChapterSummaries.Count + currentSectionSummaries.Count;
    var needsUpdate = currentTotalCount > graph.SummaryCount;

    return Results.Ok(new
    {
        graph.Nodes,
        graph.Edges,
        graph.SummaryCount,
        graph.CachedAt,
        CurrentSummaryCount = currentTotalCount,
        NeedsUpdate = needsUpdate
    });
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/ai/characters/update", async (
    HttpContext context,
    [FromBody] CharacterGraphUpdateRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser) =>
{
    if (string.IsNullOrWhiteSpace(request.DropboxPath) || string.IsNullOrWhiteSpace(request.NewContent))
        return Results.BadRequest(new { error = "DropboxPath and NewContent are required." });

    var existingGraph = AiContentCache.LoadCharacterGraph(request.DropboxPath);
    if (existingGraph == null)
        return Results.BadRequest(new { error = "No existing character graph. Generate one first." });

    Console.WriteLine($"🔄 Updating character graph with new content...");

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");

        var existingJson = JsonSerializer.Serialize(existingGraph);

        var systemPrompt = @"You are a character relationship analyzer. Update an existing character network graph based on new story content.

Return ONLY valid JSON, no markdown.

Rules:
- Add new characters if they appear and are important
- Add new relationships discovered
- Update relationship labels if they change
- Keep the same JSON structure as the existing graph
- Do NOT remove existing characters or relationships unless directly contradicted";

        var userPrompt = $@"Existing character graph:
{existingJson}

New story content:
{request.NewContent}

Update the character graph with any new information. Return the complete updated graph.";

        var model = "gpt-4o"; // Use GPT-4o for cost-effective character graph updates
        Console.WriteLine($"🤖 Using model for character graph update: {model}");

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:ChapterInsight"),
            temperature: cfg.GetValue<double>("AI:Temperature:ChapterInsight")
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"❌ Character graph update failed: {response.StatusCode}");
            return Results.Problem("Failed to update character graph.");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var content = aiResponseParser.ExtractText(doc.RootElement);

        if (string.IsNullOrWhiteSpace(content))
            return Results.Problem("No updated graph data returned.");

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        // Parse updated graph
        var updatedGraph = JsonSerializer.Deserialize<CharacterGraphResponse>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new Exception("Failed to parse updated graph");

        // Save to cache
        AiContentCache.SaveCharacterGraph(request.DropboxPath, updatedGraph);
        Console.WriteLine($"✅ Character graph updated: {updatedGraph.Nodes.Count} characters, {updatedGraph.Edges.Count} relationships");

        return Results.Ok(updatedGraph);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Character graph update failed: {ex.Message}");
        return Results.Problem("Failed to update character graph.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 6) Auth endpoints (login, user activity) ────────────────────────────
app.MapAuthEndpoints();

// ─── 6a) Anna download endpoints (GPT description, member download, send-to-library/boox/kindle)
app.MapAnnaDownloadEndpoints();

// ─── 6b) Book search endpoints ────────────────────────────────────────────
app.MapBookSearchEndpoints();

// ─── 6a2) LibGen endpoints ────────────────────────────────────────────────
app.MapLibGenEndpoints();

// ─── 6a3) Gaming endpoints ────────────────────────────────────────────────
app.MapGamingEndpoints();

// ─── 6a4) Media endpoints ─────────────────────────────────────────────────
app.MapMediaEndpoints();

// ─── 6b) Quiz endpoints ── (moved to QuizEndpoints.cs)
app.MapQuizEndpoints();

// ─── 6c) Vocab endpoints ── (moved to VocabEndpoints.cs)
app.MapVocabEndpoints();

// ─── 7) Development helper: Generate BCrypt hashes ───────────────────────
#if DEBUG
app.MapGet("/api/dev/hash", (string? code) =>
{
    if (string.IsNullOrEmpty(code))
        return Results.BadRequest(new { error = "Provide ?code=yourcode in the query string" });

    // Generate BCrypt hash with work factor 12 (good balance of security and performance)
    var hash = BCrypt.Net.BCrypt.HashPassword(code, workFactor: 12);

    return Results.Ok(new
    {
        original = code,
        hashed = hash,
        instructions = "Copy the 'hashed' value to appsettings.json Auth:AccessCodes:Code field"
    });
});
#endif

// ─── 8) Gaming PC Control ── (moved to GamingEndpoints.cs)

// ─── Shared Helper Functions ────────────────────────────────────────────

/// <summary>
/// Loads chapter content from Dropbox EPUB cache.
/// </summary>
/// <returns>Chapter content string, or null if not found/empty</returns>
static async Task<CachedChapterIndex?> LoadChapterIndexAsync(
    DropboxClient dropbox,
    string dropboxPath)
{
    var existingKeys = AiContentCache.GetExistingSummaryKeys();
    if (TryResolveLibraryFileForReaderKey(dropboxPath, existingKeys, out _, out var fullPath))
    {
        var (index, _) = await LibraryEpubCache.GetOrBuildChapterIndexAsync(fullPath, dropboxPath);
        return index;
    }

    var (dropboxIndex, _) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, dropboxPath);
    return dropboxIndex;
}

static async Task<string?> LoadChapterContentAsync(
    DropboxClient dropbox,
    string dropboxPath,
    int chapterId)
{
    var existingKeys = AiContentCache.GetExistingSummaryKeys();
    if (TryResolveLibraryFileForReaderKey(dropboxPath, existingKeys, out _, out var fullPath))
    {
        var (index, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexAsync(fullPath, dropboxPath);
        var chapter = index.Chapters.FirstOrDefault(c => c.Id == chapterId);

        if (chapter is null || string.IsNullOrWhiteSpace(chapter.FileName))
            return null;

        var chapterPath = Path.Combine(cacheDir, chapter.FileName);
        if (!File.Exists(chapterPath))
            await LibraryEpubCache.EnsureCacheBuildAsync(fullPath, dropboxPath, cacheDir);

        var content = await File.ReadAllTextAsync(chapterPath);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    var (dropboxIndex, dropboxCacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, dropboxPath);
    var dropboxChapter = dropboxIndex.Chapters.FirstOrDefault(c => c.Id == chapterId);

    if (dropboxChapter is null || string.IsNullOrWhiteSpace(dropboxChapter.FileName))
        return null;

    var dropboxChapterPath = Path.Combine(dropboxCacheDir, dropboxChapter.FileName);
    if (!File.Exists(dropboxChapterPath))
        await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, dropboxPath, dropboxCacheDir);

    var dropboxContent = await File.ReadAllTextAsync(dropboxChapterPath);
    return string.IsNullOrWhiteSpace(dropboxContent) ? null : dropboxContent;
}

/// <summary>
/// TIER 1: Summarizes text chunks with progress updates via SSE.
/// </summary>
/// <returns>Tuple of (chunk summaries list, total prompt tokens, total completion tokens)</returns>
static async Task<(List<string> chunkSummaries, int promptTokens, int completionTokens)> SummarizeChunksAsync(
    HttpClient http,
    string model,
    List<string> chunks,
    string contextLine,
    HttpResponse response,
    IConfiguration cfg,
    IAiResponseParser aiResponseParser,
    ITokenUsageService tokenUsage)
{
    var chunkSummaries = new List<string>();
    var promptTokensTotal = 0;
    var completionTokensTotal = 0;

    var chunkInstructions = @"You are an educational guide helping someone deeply understand complex texts. Analyze this passage with rich detail:

1. **What's Happening**: Summarize the main points, arguments, or narrative events
2. **Key Concepts**: Identify and explain central ideas or terminology
3. **Context**: What historical, philosophical, or intellectual background is relevant?
4. **Significance**: Why does this matter? What is the author building toward?

Write 300-400 words that assume the reader is intelligent but may lack specialized background knowledge. Explain references and provide context.";

    for (var i = 0; i < chunks.Count; i++)
    {
        var chunk = chunks[i];
        var chunkInput = $"{chunkInstructions}\n\nContext: {contextLine}\n\n{chunk}";

        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "chunks",
            stepNumber = i + 1,
            totalSteps = chunks.Count,
            message = $"Analyzing chunk {i + 1}/{chunks.Count}..."
        }, "progress");

        var payload = new
        {
            model,
            input = chunkInput,
            reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:ChunkSummary") },
            max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:ChunkSummary")
        };

        var chunkResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
        if (!chunkResponse.IsSuccessStatusCode)
        {
            var body = await chunkResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI chunk summary failed status={(int)chunkResponse.StatusCode} body={body}");
            await ServerSentEventsHelper.SendEventAsync(response, new
            {
                stage = "chunks",
                stepNumber = i + 1,
                totalSteps = chunks.Count,
                message = $"Failed at chunk {i + 1}/{chunks.Count}",
                error = $"HTTP {(int)chunkResponse.StatusCode}: {body}"
            }, "error");
            throw new HttpRequestException($"Chunk summarization failed at chunk {i + 1}");
        }

        using var stream = await chunkResponse.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        Console.WriteLine($"🔍 Chunk {i + 1} response JSON: {doc.RootElement.GetRawText()}");

        var chunkSummary = aiResponseParser.ExtractText(doc.RootElement) ?? string.Empty;
        Console.WriteLine($"🔍 Chunk {i + 1} extracted summary length: {chunkSummary.Length}");

        chunkSummaries.Add(chunkSummary);

        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            promptTokensTotal += usage.GetProperty("input_tokens").GetInt32();
            completionTokensTotal += usage.GetProperty("output_tokens").GetInt32();
        }

        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "chunks",
            stepNumber = i + 1,
            totalSteps = chunks.Count,
            message = $"Completed chunk {i + 1}/{chunks.Count}",
            success = true
        }, "progress");
    }

    return (chunkSummaries, promptTokensTotal, completionTokensTotal);
}

/// <summary>
/// TIER 2: Synthesizes chunk summaries into section summaries with progress updates via SSE.
/// </summary>
/// <returns>Tuple of (section summaries list, total prompt tokens, total completion tokens)</returns>
static async Task<(List<string> sectionSummaries, int promptTokens, int completionTokens)> SynthesizeSectionsAsync(
    HttpClient http,
    string model,
    List<string> chunkSummaries,
    string contextLine,
    HttpResponse response,
    IConfiguration cfg,
    IAiResponseParser aiResponseParser,
    ITokenUsageService tokenUsage,
    int chunksPerSection = 4)
{
    var sectionSummaries = new List<string>();
    var promptTokensTotal = 0;
    var completionTokensTotal = 0;

    var totalSections = (int)Math.Ceiling((double)chunkSummaries.Count / chunksPerSection);
    var sectionNum = 0;

    var sectionInstructions = @"You are synthesizing multiple passage analyses into a coherent section summary. Create a unified narrative that:

1. **Traces the Development**: How do the ideas/arguments/events progress through these passages?
2. **Identifies Core Themes**: What are the central concerns of this section?
3. **Contextualizes**: What intellectual traditions, historical debates, or prior thinkers is the author engaging with?
4. **Clarifies**: Explain difficult concepts in accessible terms

Write 400-500 words. Maintain educational depth while creating a flowing narrative.";

    for (var i = 0; i < chunkSummaries.Count; i += chunksPerSection)
    {
        sectionNum++;
        var sectionChunks = chunkSummaries.Skip(i).Take(chunksPerSection).ToList();
        if (sectionChunks.Count == 0) continue;

        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "sections",
            stepNumber = sectionNum,
            totalSteps = totalSections,
            message = $"Synthesizing section {sectionNum}/{totalSections}..."
        }, "progress");

        var sectionInput = $"{sectionInstructions}\n\nContext: {contextLine}\n\n{string.Join("\n\n---\n\n", sectionChunks)}";

        var sectionPayload = new
        {
            model,
            input = sectionInput,
            reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:SectionSynthesis") },
            max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:SectionSynthesis")
        };

        var sectionResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", sectionPayload);
        if (!sectionResponse.IsSuccessStatusCode)
        {
            var body = await sectionResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI section summary failed status={(int)sectionResponse.StatusCode} body={body}");
            await ServerSentEventsHelper.SendEventAsync(response, new
            {
                stage = "sections",
                stepNumber = sectionNum,
                totalSteps = totalSections,
                message = $"Failed at section {sectionNum}/{totalSections}",
                error = $"HTTP {(int)sectionResponse.StatusCode}: {body}"
            }, "error");
            throw new HttpRequestException($"Section synthesis failed at section {sectionNum}");
        }

        using var sectionStream = await sectionResponse.Content.ReadAsStreamAsync();
        using var sectionDoc = await JsonDocument.ParseAsync(sectionStream);
        var sectionSummary = aiResponseParser.ExtractText(sectionDoc.RootElement) ?? string.Empty;

        sectionSummaries.Add(sectionSummary);

        if (sectionDoc.RootElement.TryGetProperty("usage", out var sectionUsage))
        {
            promptTokensTotal += sectionUsage.GetProperty("input_tokens").GetInt32();
            completionTokensTotal += sectionUsage.GetProperty("output_tokens").GetInt32();
        }

        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "sections",
            stepNumber = sectionNum,
            totalSteps = totalSections,
            message = $"Completed section {sectionNum}/{totalSections}",
            success = true
        }, "progress");
    }

    return (sectionSummaries, promptTokensTotal, completionTokensTotal);
}

/// <summary>
/// TIER 3: Creates final comprehensive summary from section summaries with progress update via SSE.
/// </summary>
/// <returns>Tuple of (final summary text, prompt tokens used, completion tokens used)</returns>
static async Task<(string finalSummary, int promptTokens, int completionTokens)> CreateFinalSummaryAsync(
    HttpClient http,
    string model,
    List<string> sectionSummaries,
    List<string> contextParts,
    HttpResponse response,
    IConfiguration cfg,
    IAiResponseParser aiResponseParser)
{
    await ServerSentEventsHelper.SendEventAsync(response, new
    {
        stage = "final",
        stepNumber = 1,
        totalSteps = 1,
        message = "Creating final comprehensive summary..."
    }, "progress");

    var finalInstructions = $@"Create a comprehensive 700-900 word educational summary of this chapter that helps someone truly understand and appreciate the material.

Your summary should cover:

1. **Overview**:
   - What is this chapter fundamentally about?
   - What are the main arguments, ideas, or events?

2. **Historical & Intellectual Context**:
   - When and where was this written?
   - What historical events, political climate, or cultural conditions shaped this work?
   - What intellectual traditions or prior thinkers is the author responding to?
   - What debates or questions was the author engaging with?

3. **Core Arguments & Ideas**:
   - What are the key claims or propositions?
   - How does the author support these claims?
   - What concepts or terminology are central to understanding this?

4. **Significance & Interpretation**:
   - Why does this matter?
   - What impact has this had (or might it have)?
   - What makes this important or interesting?

5. **Connections**:
   - How does this relate to other thinkers, movements, or texts?
   - What contemporary issues or questions does this illuminate?

Write as if teaching an intelligent student. Define specialized terms, explain references, and provide context that helps someone new to this material truly understand what's going on and why it matters. Be thorough and educational.";

    var userContent = $"Book context: {string.Join(" | ", contextParts)}\n\nSection summaries:\n{string.Join("\n\n---\n\n", sectionSummaries)}";
    var fullInput = $"{finalInstructions}\n\n{userContent}";

    var finalPrompt = new
    {
        model,
        input = fullInput,
        reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:FinalSummary") },
        max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:FinalSummary")
    };

    var finalResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", finalPrompt);
    if (!finalResponse.IsSuccessStatusCode)
    {
        var body = await finalResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"❌ OpenAI final summary failed status={(int)finalResponse.StatusCode} body={body}");
        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "final",
            stepNumber = 1,
            totalSteps = 1,
            message = "Failed to create final summary",
            error = $"HTTP {(int)finalResponse.StatusCode}: {body}"
        }, "error");
        throw new HttpRequestException("Final summary creation failed");
    }

    using var finalStream = await finalResponse.Content.ReadAsStreamAsync();
    using var finalDoc = await JsonDocument.ParseAsync(finalStream);

    Console.WriteLine($"🔍 Final response JSON: {finalDoc.RootElement.GetRawText()}");

    string finalSummary = aiResponseParser.ExtractText(finalDoc.RootElement) ?? "No summary returned.";
    Console.WriteLine($"🔍 Extracted summary length: {finalSummary.Length}");

    var promptTokens = 0;
    var completionTokens = 0;

    if (finalDoc.RootElement.TryGetProperty("usage", out var finalUsage))
    {
        promptTokens = finalUsage.GetProperty("input_tokens").GetInt32();
        completionTokens = finalUsage.GetProperty("output_tokens").GetInt32();
    }

    return (finalSummary, promptTokens, completionTokens);
}

// DownloadBookFromAnnaArchiveAsync moved to AnnaDownloadHelpers.cs
// ResolveLibraryRoot moved to LibraryHelpers.cs

static string ResolveReaderKey(string fileName, ISet<string> existingKeys)
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

static bool TryResolveLibraryFileForReaderKey(
    string readerKey,
    ISet<string> existingKeys,
    out string fileName,
    out string fullPath)
{
    fileName = string.Empty;
    fullPath = string.Empty;
    var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
    if (!Directory.Exists(libraryRoot))
        return false;

    var safeFileName = Path.GetFileName(readerKey);
    if (!string.IsNullOrWhiteSpace(safeFileName))
    {
        var directPath = Path.Combine(libraryRoot, safeFileName);
        if (File.Exists(directPath))
        {
            fileName = safeFileName;
            fullPath = directPath;
            return true;
        }
    }

    var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
    foreach (var metaFile in Directory.GetFiles(libraryRoot, "*.meta.json"))
    {
        try
        {
            var json = File.ReadAllText(metaFile);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
            if (meta == null || string.IsNullOrWhiteSpace(meta.FileName))
                continue;

            var key = ResolveReaderKey(meta.FileName, existingKeys);
            if (!string.Equals(key, readerKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Path.Combine(libraryRoot, meta.FileName);
            if (File.Exists(candidate))
            {
                fileName = meta.FileName;
                fullPath = candidate;
                return true;
            }
        }
        catch
        {
            // ignore malformed meta files
        }
    }

    return false;
}

// Library helper functions moved to LibraryHelpers.cs
// Book title extraction functions moved to BookTitleExtractionHelpers.cs

app.Run();

// Helper classes moved to:
// - Helpers/ServerSentEventsHelper.cs
// - Helpers/ApiResponse.cs

// ─── DTOs ────────────────────────────────────────────────────────────────
// Note: DTOs moved to Models/DropboxEpubModels.cs and Models/AuthModels.cs
// ChapterLabeler moved to Helpers/ChapterLabeler.cs
// ChapterLabelingHelper moved to Helpers/ChapterLabelingHelper.cs
// AI request/response models moved to Models/AiRequestModels.cs
// Library models moved to Models/LibraryModels.cs

// EpubCachePathProviderAdapter moved to Helpers/EpubCachePathProviderAdapter.cs
// DropboxEpubCache moved to Helpers/DropboxEpubCache.cs
// LibraryEpubCache moved to Helpers/LibraryEpubCache.cs
// AiContentCache moved to Helpers/Cache/ (modularized into AiCacheBase, ChapterSummaryCache, SectionSummaryCache, CharacterGraphCache, VocabularyCache)

// Make Program accessible to integration tests
public partial class Program { }
