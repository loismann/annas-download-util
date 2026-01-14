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

// AI job locks moved to IAiJobLockService

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


// ─── 3c-3h) Library endpoints (moved to LibraryEndpoints.cs) ─────────────────

// ─── 5b) Dropbox EPUB reader endpoints (moved to DropboxReaderEndpoints.cs) ───


// ─── AI Summarize endpoints (moved to AiSummarizeEndpoints.cs) ───

// ─── AI Section Summary endpoints (moved to AiSectionSummaryEndpoints.cs) ───


// ─── 5l) Save section vocabulary (moved to AiVocabEndpoints.cs) ─────────────

// ─── 5m-5r2) Vocab endpoints (moved to VocabEndpoints.cs) ─────────────────────


// ─── 5s) Suggest authors (moved to AiBookSearchEndpoints.cs) ──────────────


// ─── 5n/5n-2/5o) Book search endpoints (moved to AiBookSearchEndpoints.cs) ───


// ─── Character Graph endpoints (moved to AiCharacterEndpoints.cs) ───


// ─── Auth endpoints ── (moved to AuthEndpoints.cs)
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

// ─── 6d) Dropbox Reader endpoints ── (moved to DropboxReaderEndpoints.cs)
app.MapDropboxReaderEndpoints();

// ─── 6e) Library endpoints ── (moved to LibraryEndpoints.cs)
app.MapLibraryEndpoints();

// ─── 6f) AI Usage endpoints ── (moved to AiUsageEndpoints.cs)
app.MapAiUsageEndpoints();

// ─── 6g) AI Flashcards endpoints ── (moved to AiFlashcardsEndpoints.cs)
app.MapAiFlashcardsEndpoints();

// ─── 6h) AI Vocab endpoints ── (moved to AiVocabEndpoints.cs)
app.MapAiVocabEndpoints();

// ─── 6i) AI Book Search endpoints ── (moved to AiBookSearchEndpoints.cs)
app.MapAiBookSearchEndpoints();

// ─── 6j) AI Character Graph endpoints ── (moved to AiCharacterEndpoints.cs)
app.MapAiCharacterEndpoints();

// ─── 6k) AI Summarize endpoints ── (moved to AiSummarizeEndpoints.cs)
app.MapAiSummarizeEndpoints();

// ─── 6l) AI Section Summary endpoints ── (moved to AiSectionSummaryEndpoints.cs)
app.MapAiSectionSummaryEndpoints();

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


// ─── Shared Helper Functions (moved to Helpers/AiSummaryHelpers.cs) ───

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
