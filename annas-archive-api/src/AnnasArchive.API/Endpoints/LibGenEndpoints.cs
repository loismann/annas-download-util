using System.Security.Claims;
using System.Text.RegularExpressions;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping LibGen-related endpoints.
/// </summary>
public static class LibGenEndpoints
{
    /// <summary>
    /// Maps LibGen endpoints to the application.
    /// </summary>
    public static WebApplication MapLibGenEndpoints(this WebApplication app)
    {
        app.MapGet("/api/libgen/book", HandleLibGenSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/libgen/book/{md5}/download/member", HandleLibGenDownload)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/libgen/book/{md5}/send-to-library", HandleLibGenSendToLibrary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static string GetUserName(HttpContext context) =>
        context.User?.FindFirst(ClaimTypes.Email)?.Value
        ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
        ?? "unknown";

    private static string GetExtensionFromContentType(string? mediaType) =>
        mediaType switch
        {
            "application/pdf" => ".pdf",
            "application/epub+zip" => ".epub",
            "application/x-mobipocket-ebook" => ".mobi",
            _ => ".bin"
        };

    private static string GetContentType(string ext) =>
        ext.ToLowerInvariant() switch
        {
            ".epub" => "application/epub+zip",
            ".pdf" => "application/pdf",
            ".mobi" => "application/x-mobipocket-ebook",
            ".azw3" => "application/vnd.amazon.ebook",
            ".fb2" => "text/xml",
            _ => "application/octet-stream"
        };

    private static async Task<Stream> ProcessCoverAsync(
        Stream ebookStream,
        string? coverUrl,
        string ext,
        IEbookCoverService coverService,
        string logPrefix)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
            return ebookStream;

        var extNoDot = ext.TrimStart('.');
        if (coverService.IsFormatSupported(extNoDot))
        {
            Console.WriteLine($"[{logPrefix}] Attempting cover replacement");
            return await coverService.ReplaceCoverAsync(ebookStream, coverUrl, extNoDot);
        }

        Console.WriteLine($"[{logPrefix}] Format {extNoDot} not supported for cover replacement, skipping");
        return ebookStream;
    }

    private static (string safeTitle, string ext, string fileName) BuildFileInfo(
        string? title,
        string md5,
        string? downloadUrl,
        HttpResponseMessage resp)
    {
        var rawTitle = !string.IsNullOrWhiteSpace(title) ? title : md5;
        var safeTitle = Regex.Replace(rawTitle, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");

        var ext = !string.IsNullOrEmpty(downloadUrl)
            ? Path.GetExtension(new Uri(downloadUrl).AbsolutePath)
            : "";

        if (string.IsNullOrEmpty(ext))
            ext = GetExtensionFromContentType(resp.Content.Headers.ContentType?.MediaType);

        return (safeTitle, ext, $"{safeTitle}{ext}");
    }

    private static async Task<IResult> HandleLibGenSearch(
        [FromQuery] string name,
        LibGenService svc,
        IValidationService validation,
        IConfiguration cfg,
        [FromQuery] bool exact = false)
    {
        Console.WriteLine($"[API LibGen Search] Received request: name='{name}', exact={exact}");

        if (!validation.IsValidSearchQuery(name))
        {
            Console.WriteLine($"[API LibGen Search] Validation failed for query: '{name}'");
            return Results.BadRequest(new {
                error = "Query parameter 'name' is required and must be between 1 and 500 characters."
            });
        }

        var searchLimit = cfg.GetValue<int>("Anna:SearchLimit", 25);
        Console.WriteLine($"[API LibGen Search] Calling LibGenService.SearchAsync...");
        var books = (await svc.SearchAsync(name, searchLimit, exact)).ToList();
        Console.WriteLine($"[API LibGen Search] Service returned {books.Count} books");

        if (exact)
        {
            var originalCount = books.Count;
            books = books
                .Where(b => string.Equals(b.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            Console.WriteLine($"[API LibGen Search] After exact filter: {books.Count} books (was {originalCount})");
        }

        if (books.Any())
        {
            var result = books.Count == 1 ? Results.Ok(books[0]) : Results.Ok(books);
            Console.WriteLine($"[API LibGen Search] Returning {books.Count} books");
            return result;
        }
        else
        {
            Console.WriteLine($"[API LibGen Search] No books found, returning 404");
            return ApiResponse.NotFound("No books found matching that name.");
        }
    }

    private static async Task<IResult> HandleLibGenDownload(
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
        HttpContext context)
    {
        if (!validation.IsValidMd5(md5))
            return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

        if (!validation.IsValidTitle(title))
            return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

        var userName = GetUserName(context);
        Console.WriteLine($"📚 [LibGen] Downloading book {md5} for user {userName}...");

        var resp = await libgen.GetDownloadResponseAsync(md5, HttpCompletionOption.ResponseHeadersRead);
        if (resp == null || !resp.IsSuccessStatusCode)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            Console.WriteLine($"❌ [LibGen] Failed to download book {md5}");
            return Results.Ok(new { success = false, message = "Failed to download book from LibGen.", accountFastInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay) });
        }

        var downloadUrl = await libgen.GetDownloadUrlAsync(md5);
        var (_, ext, fileName) = BuildFileInfo(title, md5, downloadUrl, resp);
        Console.WriteLine($"✅ [LibGen] Downloaded: {fileName}");

        downloadTracking.RecordDownload(md5, userName);
        Console.WriteLine($"[download-libgen] Recorded download for user {userName}, MD5: {md5}");

        using (resp)
        {
            var ebookStream = await resp.Content.ReadAsStreamAsync();
            ebookStream = await ProcessCoverAsync(ebookStream, coverUrl, ext, coverService, "download-libgen");
            return Results.Stream(ebookStream, GetContentType(ext), fileName);
        }
    }

    private static async Task<IResult> HandleLibGenSendToLibrary(
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
        HttpContext context)
    {
        if (!validation.IsValidMd5(md5))
            return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

        if (!validation.IsValidTitle(title))
            return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

        var userName = GetUserName(context);
        var userTag = LibraryHelpers.ResolveUserLibraryTag(context);
        Console.WriteLine($"📚 [LibGen] Saving book {md5} to library for user {userName}...");

        var resp = await libgen.GetDownloadResponseAsync(md5, HttpCompletionOption.ResponseHeadersRead);
        if (resp == null || !resp.IsSuccessStatusCode)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            Console.WriteLine($"❌ [LibGen] Failed to download book {md5}");
            return Results.Ok(new { success = false, message = "Failed to download book from LibGen.", accountFastInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay) });
        }

        var downloadUrl = await libgen.GetDownloadUrlAsync(md5);
        var (_, ext, fileName) = BuildFileInfo(title, md5, downloadUrl, resp);

        downloadTracking.RecordDownload(md5, userName);
        Console.WriteLine($"[library-libgen] Recorded download for user {userName}, MD5: {md5}");

        var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(currentDownloadsLeft, currentDownloadsPerDay);

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        Directory.CreateDirectory(libraryRoot);

        using (resp)
        {
            var ebookStream = await resp.Content.ReadAsStreamAsync();
            ebookStream = await ProcessCoverAsync(ebookStream, coverUrl, ext, coverService, "library-libgen");

            var destinationPath = Path.Combine(libraryRoot, fileName);
            if (File.Exists(destinationPath))
            {
                return Results.Ok(new { success = true, message = "File already exists in library.", fileName, path = destinationPath, accountFastInfo = trackingInfo });
            }

            await using var outStream = File.Create(destinationPath);
            await ebookStream.CopyToAsync(outStream);

            await LibraryHelpers.WriteLibraryMetadataAsync(libraryRoot, fileName, md5, title, authors, format, fileSize, coverUrl, source, userTag, description);

            return Results.Ok(new { success = true, message = "Saved to library.", fileName, path = destinationPath, accountFastInfo = trackingInfo });
        }
    }
}
