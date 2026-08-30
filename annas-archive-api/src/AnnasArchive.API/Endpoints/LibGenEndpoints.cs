using System.Security.Claims;
using System.Text.RegularExpressions;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Helpers;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

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
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/libgen")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/book", HandleLibGenSearch);

        group.MapPost("/book/{md5}/download/member", HandleLibGenDownload);

        group.MapPost("/book/{md5}/send-to-library", HandleLibGenSendToLibrary);

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

    private static (string safeTitle, string ext, string fileName) BuildFileInfo(
        string? title,
        string md5,
        string? downloadUrl,
        HttpResponseMessage resp)
        => BookFileNaming.For(title, md5, downloadUrl, resp);

    private static async Task<IResult> HandleLibGenSearch(
        [FromQuery] string? name,
        LibGenService svc,
        IValidationService validation,
        IConfiguration cfg,
        [FromQuery] bool exact = false)
    {
        Log.Information("[API LibGen Search] Received request: name='{Name}', exact={Exact}", name, exact);

        if (!validation.IsValidSearchQuery(name))
        {
            Log.Information("[API LibGen Search] Validation failed for query: '{Name}'", name);
            return ApiResponse.BadRequest("Query parameter 'name' is required and must be between 1 and 500 characters.");
        }

        var searchLimit = cfg.GetValue<int>("Anna:SearchLimit", 25);
        Log.Information("[API LibGen Search] Calling LibGenService.SearchAsync...");

        List<BookDto> books;
        try
        {
            books = (await svc.SearchAsync(name, searchLimit, exact)).ToList();
        }
        // The search no longer reports an unreachable LibGen as an empty result,
        // so this arm has to exist: without it the exception reaches the global
        // handler as a 500, which says "this endpoint is broken" rather than
        // "the site it asks is down". Same mapping as /api/anna/book, because it
        // is now the same failure.
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "[API LibGen Search] Every LibGen domain refused");
            return Results.Json(
                new { error = "External search service unavailable", details = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        Log.Information("[API LibGen Search] Service returned {BooksCount} books", books.Count);

        if (exact)
        {
            var originalCount = books.Count;
            books = books
                .Where(b => string.Equals(b.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            Log.Information("[API LibGen Search] After exact filter: {BooksCount} books (was {OriginalCount})", books.Count, originalCount);
        }

        if (books.Any())
        {
            var result = books.Count == 1 ? Results.Ok(books[0]) : Results.Ok(books);
            Log.Information("[API LibGen Search] Returning {BooksCount} books", books.Count);
            return result;
        }
        else
        {
            Log.Information("[API LibGen Search] No books found, returning 404");
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
        // Use shared extended validation helper for all parameters
        var validationError = SendToTargetHelpers.ValidateSendParametersExtended(
            md5, title, coverUrl, authors, fileSize, description: null, validation);
        if (validationError != null)
            return ApiResponse.BadRequest(validationError);

        var userName = GetUserName(context);
        Log.Information("[LibGen] Downloading book {Md5} for user {UserName}...", md5, userName);

        var resp = await libgen.GetDownloadResponseAsync(md5, HttpCompletionOption.ResponseHeadersRead);
        if (resp == null || !resp.IsSuccessStatusCode)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            Log.Warning("[LibGen] Failed to download book {Md5}", md5);
            // 502: LibGen refused or returned nothing. accountFastInfo still rides
            // along for the same reason as the Anna's Archive download path — the
            // quota counter must stay truthful whether or not the file arrived.
            return Results.Json(
                new { success = false, message = "Failed to download book from LibGen.", accountFastInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay) },
                statusCode: StatusCodes.Status502BadGateway);
        }

        var downloadUrl = await libgen.GetDownloadUrlAsync(md5);
        var (_, ext, fileName) = BuildFileInfo(title, md5, downloadUrl, resp);
        Log.Information("[LibGen] Downloaded: {FileName}", fileName);

        downloadTracking.RecordDownload(md5, userName);
        Log.Information("[download-libgen] Recorded download for user {UserName}, MD5: {Md5}", userName, md5);

        using (resp)
        {
            var ebookStream = await resp.Content.ReadAsStreamAsync();
            ebookStream = await SendToTargetHelpers.TryReplaceCoverAsync(
                ebookStream, coverUrl, fileName, coverService, "download-libgen");
            return Results.Stream(ebookStream, GetContentType(ext), fileName);
        }
    }

    private static IResult HandleLibGenSendToLibrary(
        [FromRoute] string md5,
        [FromQuery] string? title,
        [FromQuery] string? coverUrl,
        [FromQuery] string? authors,
        [FromQuery] string? format,
        [FromQuery] string? fileSize,
        [FromQuery] string? source,
        [FromQuery] string? description,
        IValidationService validation,
        IDownloadTrackingService downloadTracking,
        Services.IBookDownloadJobService jobs,
        IServiceScopeFactory scopeFactory,
        HttpContext context)
    {
        // Use shared extended validation helper for all parameters
        var validationError = SendToTargetHelpers.ValidateSendParametersExtended(
            md5, title, coverUrl, authors, fileSize, description, validation);
        if (validationError != null)
            return ApiResponse.BadRequest(validationError);

        var userName = GetUserName(context);
        var userTag = LibraryHelpers.ResolveUserLibraryTag(context);
        if (userTag is null)
            // Not fatal — LibraryWatcher:AutoTagNewBooks still gives the book an
            // owner — but it means the download is about to be attributed to the
            // fallback person rather than whoever actually asked for it, and that
            // is worth a line rather than happening in silence.
            Log.Warning("[LibGen] Could not resolve a household member for this download; " +
                "the book will fall back to LibraryWatcher:AutoTagNewBooks");
        Log.Information("[LibGen] Saving book {Md5} to library for user {UserName}...", md5, userName);

        var job = jobs.Start(title ?? md5);

        // Fire-and-forget — see the identical comment on HandleSendToLibrary in
        // AnnaDownloadEndpoints.cs for why this doesn't await the download.
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var libgen = scope.ServiceProvider.GetRequiredService<LibGenService>();
            var coverService = scope.ServiceProvider.GetRequiredService<IEbookCoverService>();

            try
            {
                var resp = await libgen.GetDownloadResponseAsync(md5, HttpCompletionOption.ResponseHeadersRead);
                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    Log.Warning("[LibGen] Failed to download book {Md5}", md5);
                    jobs.Fail(job.JobId, "Failed to download book from LibGen.");
                    return;
                }

                var downloadUrl = await libgen.GetDownloadUrlAsync(md5);
                var (_, ext, fileName) = BuildFileInfo(title, md5, downloadUrl, resp);

                downloadTracking.RecordDownload(md5, userName);
                Log.Information("[library-libgen] Recorded download for user {UserName}, MD5: {Md5}", userName, md5);

                var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
                Directory.CreateDirectory(libraryRoot);

                using (resp)
                {
                    var ebookStream = await resp.Content.ReadAsStreamAsync();
                    ebookStream = await SendToTargetHelpers.TryReplaceCoverAsync(
                        ebookStream, coverUrl, fileName, coverService, "library-libgen");

                    var destinationPath = Path.Combine(libraryRoot, fileName);
                    if (File.Exists(destinationPath))
                    {
                        jobs.Complete(job.JobId, fileName, "File already exists in library.");
                        return;
                    }

                    await LibraryDownloadHelpers.CopyToLibraryAtomicallyAsync(
                        ebookStream,
                        destinationPath,
                        resp.Content.Headers.ContentLength,
                        (bytesDownloaded, totalBytes) => jobs.UpdateProgress(job.JobId, bytesDownloaded, totalBytes));

                    await LibraryHelpers.WriteLibraryMetadataAsync(libraryRoot, fileName, md5, title, authors, format, fileSize, coverUrl, source, userTag, description);

                    jobs.Complete(job.JobId, fileName, "Saved to library.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[library-libgen] Background download failed for MD5 {Md5}", md5);
                jobs.Fail(job.JobId, "Download failed: " + ex.Message);
            }
        });

        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        return Results.Ok(new
        {
            success = true,
            jobId = job.JobId,
            message = "Download started.",
            accountFastInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay)
        });
    }
}
