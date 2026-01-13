using System.Security.Claims;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.AspNetCore.Mvc;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Anna's Archive download-related endpoints.
/// </summary>
public static class AnnaDownloadEndpoints
{
    /// <summary>
    /// Maps Anna's Archive download endpoints to the application.
    /// </summary>
    public static WebApplication MapAnnaDownloadEndpoints(this WebApplication app)
    {
        // GPT-4 book description
        app.MapGet("/api/anna/book/description/gpt", HandleGptDescription)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Non-member download links
        app.MapGet("/api/anna/book/{md5}/download", HandleDownloadLinks)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Member download (stream file to client)
        app.MapPost("/api/anna/book/{md5}/download/member", HandleMemberDownload)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Send to library (save to Synology disk)
        app.MapPost("/api/anna/book/{md5}/send-to-library", HandleSendToLibrary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Send to Boox (upload to Dropbox)
        app.MapPost("/api/anna/book/{md5}/send-to-boox", HandleSendToBoox)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Send to Kindle (email)
        app.MapPost("/api/anna/book/{md5}/send-to-kindle", HandleSendToKindle)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    // ─── GPT-4 Description Endpoint ────────────────────────────────────────────

    private static async Task<IResult> HandleGptDescription(
        HttpContext context,
        [FromQuery] string title,
        [FromQuery] string? author,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new { error = "title is required." });

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        Console.WriteLine($"📖 GPT-4 description lookup: title='{title}', author='{author}'");

        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelFast();
        var description = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
            title,
            author ?? "",
            http,
            model,
            modelHelper,
            aiResponseParser);

        // Track token usage (estimate ~50 completion tokens for description)
        var userId = UserHelpers.GetUserIdFromContext(context);
        if (userId != null)
            tokenUsage.AddUsage(userId, 150, 50);

        Console.WriteLine(string.IsNullOrEmpty(description)
            ? $"⚠️ GPT-4 description not generated for '{title}'"
            : $"✅ GPT-4 description generated for '{title}'");

        return Results.Ok(new { description });
    }

    // ─── Non-Member Download Links Endpoint ────────────────────────────────────

    private static async Task<IResult> HandleDownloadLinks(
        [FromRoute] string md5,
        AnnaArchiveService svc,
        IValidationService validation)
    {
        if (!validation.IsValidMd5(md5))
            return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

        var links = await svc.GetDownloadLinksAsync(md5);
        return links.Any()
            ? Results.Ok(new { id = md5, downloadLinks = links })
            : ApiResponse.NotFound("No download links found.");
    }

    // ─── Member Download Endpoint ──────────────────────────────────────────────

    private static async Task<IResult> HandleMemberDownload(
        [FromRoute] string md5,
        [FromQuery] string? title,
        [FromQuery] string? coverUrl,
        [FromQuery] string? authors,
        [FromQuery] string? format,
        [FromQuery] string? fileSize,
        [FromQuery] string? source,
        AnnaArchiveService anna,
        IValidationService validation,
        IEbookCoverService coverService,
        IDownloadTrackingService downloadTracking,
        IConfiguration cfg,
        HttpContext context)
    {
        if (!validation.IsValidMd5(md5))
            return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

        if (!validation.IsValidTitle(title))
            return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

        var memberKey = cfg["Anna:MemberKey"]
            ?? throw new InvalidOperationException("Missing Anna:MemberKey.");

        // Get user name from auth context
        var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
            ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
            ?? "unknown";

        // Use shared helper to download book from Anna's Archive
        var (resp, fileName, acctInfo, errorMessage) = await AnnaDownloadHelpers.DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

        if (errorMessage != null)
        {
            // Get current download status even on failure
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = trackingInfo });
        }

        if (resp == null || fileName == null)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = trackingInfo });
        }

        // Record successful download in our tracking system
        downloadTracking.RecordDownload(md5, userName);
        Console.WriteLine($"[download-member] Recorded download for user {userName}, MD5: {md5}");

        // Get updated download status
        var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();

        using (resp)
        {
            Stream ebookStream = await resp.Content.ReadAsStreamAsync();

            // Attempt cover replacement if coverUrl is provided and format is supported
            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                var ext = Path.GetExtension(fileName).TrimStart('.');
                if (coverService.IsFormatSupported(ext))
                {
                    Console.WriteLine($"[download-member] Attempting cover replacement for {fileName}");
                    ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, ext);
                }
                else
                {
                    Console.WriteLine($"[download-member] Format {ext} not supported for cover replacement, skipping");
                }
            }

            // Set content type based on file extension
            var contentType = Path.GetExtension(fileName).ToLowerInvariant() switch
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
    }

    // ─── Send to Library Endpoint ──────────────────────────────────────────────

    private static async Task<IResult> HandleSendToLibrary(
        [FromRoute] string md5,
        [FromQuery] string? title,
        [FromQuery] string? coverUrl,
        [FromQuery] string? authors,
        [FromQuery] string? format,
        [FromQuery] string? fileSize,
        [FromQuery] string? source,
        [FromQuery] string? description,
        AnnaArchiveService anna,
        IValidationService validation,
        IEbookCoverService coverService,
        IDownloadTrackingService downloadTracking,
        IConfiguration cfg,
        HttpContext context)
    {
        if (!validation.IsValidMd5(md5))
            return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

        if (!validation.IsValidTitle(title))
            return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

        var memberKey = cfg["Anna:MemberKey"]
            ?? throw new InvalidOperationException("Missing Anna:MemberKey.");

        // Get user name from auth context
        var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
            ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
            ?? "unknown";
        var userTag = LibraryHelpers.ResolveUserLibraryTag(context);

        // Use shared helper to download book from Anna's Archive
        var (resp, fileName, acctInfo, errorMessage) = await AnnaDownloadHelpers.DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

        if (errorMessage != null)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = trackingInfo });
        }

        if (resp == null || fileName == null)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = trackingInfo });
        }

        // Record successful download in our tracking system
        downloadTracking.RecordDownload(md5, userName);
        Console.WriteLine($"[library-anna] Recorded download for user {userName}, MD5: {md5}");

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
                var ext = Path.GetExtension(fileName).TrimStart('.');
                if (coverService.IsFormatSupported(ext))
                {
                    Console.WriteLine($"[library-anna] Attempting cover replacement for {fileName}");
                    ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, ext);
                }
                else
                {
                    Console.WriteLine($"[library-anna] Format {ext} not supported for cover replacement, skipping");
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
    }

    // ─── Send to Boox Endpoint ─────────────────────────────────────────────────

    private static async Task<IResult> HandleSendToBoox(
        HttpContext context,
        [FromRoute] string md5,
        [FromQuery] string? title,
        [FromQuery] string? coverUrl,
        IValidationService validation,
        AnnaArchiveService anna,
        IEbookCoverService coverService,
        DropboxClient dropbox,
        IConfiguration cfg,
        IDownloadTrackingService downloadTracking)
    {
        if (!validation.IsValidMd5(md5))
            return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

        if (!validation.IsValidTitle(title))
            return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

        var memberKey = cfg["Anna:MemberKey"]
            ?? throw new InvalidOperationException("Missing Anna:MemberKey.");

        // Use shared helper to download book from Anna's Archive
        var (resp, fileName, acctInfo, errorMessage) = await AnnaDownloadHelpers.DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

        if (errorMessage != null)
        {
            // Return current tracking status on error
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = trackingInfo });
        }

        if (resp == null || fileName == null)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = trackingInfo });
        }

        using (resp)
        {
            var uploadPath = $"{cfg["Dropbox:UploadFolderPath"]}/{fileName}";
            Stream ebookStream = await resp.Content.ReadAsStreamAsync();

            // Attempt cover replacement if coverUrl is provided and format is supported
            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                var ext = Path.GetExtension(fileName).TrimStart('.');
                if (coverService.IsFormatSupported(ext))
                {
                    Console.WriteLine($"[send-to-boox] Attempting cover replacement for {fileName}");
                    ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, ext);
                }
                else
                {
                    Console.WriteLine($"[send-to-boox] Format {ext} not supported for cover replacement, skipping");
                }
            }

            using var stream = ebookStream;

            try
            {
                Console.WriteLine($"Uploading '{fileName}' to Dropbox: {uploadPath}");

                var uploaded = await dropbox.Files.UploadAsync(
                    uploadPath,
                    WriteMode.Overwrite.Instance,
                    body: stream
                );

                Console.WriteLine($"✅ Dropbox upload successful! File: {uploaded.PathDisplay}");

                // Get user name from auth context
                var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
                    ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
                    ?? "unknown";

                // Record successful download in our tracking system
                downloadTracking.RecordDownload(md5, userName);
                Console.WriteLine($"[send-to-boox] Recorded download for user {userName}, MD5: {md5}");

                // Get updated download tracking status
                var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
                var counterInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);

                return Results.Ok(new
                {
                    success         = true,
                    dropboxPath     = uploaded.PathDisplay,
                    dropboxFileId   = uploaded.Id,
                    accountFastInfo = counterInfo
                });
            }
            catch (Dropbox.Api.ApiException<UploadError> ex)
            {
                var details = ex.ErrorResponse?.ToString() ?? ex.ToString();
                Console.WriteLine($"❌ Dropbox upload failed: {ex.Message}{(string.IsNullOrWhiteSpace(details) ? "" : $" | Details: {details}")}");

                return Results.Ok(new
                {
                    success         = false,
                    message         = "Failed to upload file to Dropbox. Please try again.",
                    accountFastInfo = acctInfo
                });
            }
            catch (Dropbox.Api.HttpException ex)
            {
                var details = ex.ToString();
                Console.WriteLine($"❌ Dropbox upload failed (HTTP {ex.StatusCode}): {ex.Message} | Uri: {ex.RequestUri} | Details: {details}");
                return Results.Ok(new
                {
                    success         = false,
                    message         = "Failed to upload file to Dropbox. Please try again.",
                    accountFastInfo = acctInfo
                });
            }
            catch (Dropbox.Api.DropboxException ex)
            {
                Console.WriteLine($"❌ Dropbox upload failed (DropboxException): {ex}");
                return Results.Ok(new
                {
                    success         = false,
                    message         = "Failed to upload file to Dropbox. Please try again.",
                    accountFastInfo = acctInfo
                });
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"❌ Dropbox upload failed (HTTP): {ex}");
                return Results.Ok(new
                {
                    success         = false,
                    message         = "Failed to upload file to Dropbox. Please try again.",
                    accountFastInfo = acctInfo
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Dropbox upload failed: {ex.Message}");

                return Results.Ok(new
                {
                    success         = false,
                    message         = "Failed to upload file to Dropbox. Please try again.",
                    accountFastInfo = acctInfo
                });
            }
        }
    }

    // ─── Send to Kindle Endpoint ───────────────────────────────────────────────

    private static async Task<IResult> HandleSendToKindle(
        HttpContext context,
        [FromRoute] string md5,
        [FromQuery] string? title,
        [FromQuery] string target,
        [FromQuery] string? coverUrl,
        AnnaArchiveService anna,
        IEmailService emailService,
        IEbookCoverService coverService,
        DropboxClient dropbox,
        IConfiguration cfg,
        IValidationService validation,
        IDownloadTrackingService downloadTracking)
    {
        if (!validation.IsValidMd5(md5))
            return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

        if (!validation.IsValidTitle(title))
            return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

        if (string.IsNullOrWhiteSpace(target) || (target != "dad" && target != "mom"))
            return Results.BadRequest(new { error = "Invalid target. Must be 'dad' or 'mom'." });

        var memberKey = cfg["Anna:MemberKey"]
            ?? throw new InvalidOperationException("Missing Anna:MemberKey.");

        // Use shared helper to download book from Anna's Archive
        var (resp, fileName, acctInfo, errorMessage) = await AnnaDownloadHelpers.DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

        if (errorMessage != null)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = trackingInfo });
        }

        if (resp == null || fileName == null)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = trackingInfo });
        }

        var tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");

        using (resp)
        {
            try
            {
                // Get the ebook stream
                Stream ebookStream = await resp.Content.ReadAsStreamAsync();

                // Attempt cover replacement if coverUrl is provided and format is supported
                if (!string.IsNullOrWhiteSpace(coverUrl))
                {
                    var ext = Path.GetExtension(fileName).TrimStart('.');
                    if (coverService.IsFormatSupported(ext))
                    {
                        Console.WriteLine($"[send-to-kindle] Attempting cover replacement for {fileName}");
                        ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, ext);
                    }
                    else
                    {
                        Console.WriteLine($"[send-to-kindle] Format {ext} not supported for cover replacement, skipping");
                    }
                }

                // Write to temp file
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await ebookStream.CopyToAsync(fileStream);
                }

                // Send email to the appropriate Kindle
                var kindleEmail = target.ToLower() == "dad"
                    ? cfg["Email:DadsKindleEmail"] ?? throw new InvalidOperationException("Email:DadsKindleEmail not configured")
                    : cfg["Email:MomsKindleEmail"] ?? throw new InvalidOperationException("Email:MomsKindleEmail not configured");

                await emailService.SendEmailWithAttachmentAsync(
                    kindleEmail,
                    "Book from Anna's Archive",
                    $"Sent from Anna's Archive: {title ?? fileName}",
                    tempFilePath,
                    fileName);

                // After successful email send, also backup to Dropbox
                bool dropboxSuccess = false;
                string? dropboxPathResult = null;

                try
                {
                    var dropboxFolder = target.ToLower() == "dad" ? "/dad_downloads" : "/mom_downloads";
                    var dropboxPath = $"{dropboxFolder}/{fileName}";

                    using (var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        Console.WriteLine($"[send-to-kindle] Uploading '{fileName}' to Dropbox: {dropboxPath}");

                        var uploaded = await dropbox.Files.UploadAsync(
                            dropboxPath,
                            WriteMode.Overwrite.Instance,
                            body: fileStream
                        );

                        dropboxPathResult = uploaded.PathDisplay;
                        dropboxSuccess = true;
                        Console.WriteLine($"✅ Dropbox backup successful! Path: {dropboxPathResult}");
                    }
                }
                catch (Dropbox.Api.ApiException<UploadError> ex)
                {
                    var details = ex.ErrorResponse?.ToString() ?? ex.ToString();
                    Console.WriteLine($"⚠️ Dropbox backup failed (non-critical): {ex.Message} | Details: {details}");
                }
                catch (Dropbox.Api.HttpException ex)
                {
                    Console.WriteLine($"⚠️ Dropbox backup failed (non-critical, HTTP {ex.StatusCode}): {ex.Message}");
                }
                catch (Dropbox.Api.DropboxException ex)
                {
                    Console.WriteLine($"⚠️ Dropbox backup failed (non-critical): {ex}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Dropbox backup failed (non-critical): {ex.Message}");
                }

                // Get user name from auth context
                var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
                    ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
                    ?? "unknown";

                // Record successful download in our tracking system
                downloadTracking.RecordDownload(md5, userName);
                Console.WriteLine($"[send-to-kindle] Recorded download for user {userName}, MD5: {md5}");

                // Get updated download tracking status
                var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
                var counterInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);

                return Results.Ok(new
                {
                    success         = true,
                    message         = dropboxSuccess
                        ? $"Book sent to {target}'s Kindle and backed up to Dropbox"
                        : $"Book sent to {target}'s Kindle (Dropbox backup failed, but email succeeded)",
                    dropboxPath     = dropboxPathResult,
                    accountFastInfo = counterInfo
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Send to Kindle failed: {ex.Message}");
                return Results.Ok(new
                {
                    success         = false,
                    message         = "Failed to send book to Kindle. Please try again.",
                    accountFastInfo = acctInfo
                });
            }
            finally
            {
                // Clean up temp file
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); }
                    catch { /* ignore */ }
                }
            }
        }
    }
}
