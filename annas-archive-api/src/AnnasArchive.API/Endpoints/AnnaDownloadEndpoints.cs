using System.Security.Claims;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.AspNetCore.Mvc;
using Serilog;

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
        [FromQuery] string? title,
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

        Log.Information("📖 GPT-4 description lookup: title='{title}', author='{author}'");

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

        Log.Information(string.IsNullOrEmpty(description)
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
        // Use shared extended validation helper for all parameters
        var validationError = SendToTargetHelpers.ValidateSendParametersExtended(
            md5, title, coverUrl, authors, fileSize, description: null, validation);
        if (validationError != null)
            return Results.BadRequest(new { error = validationError });

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
        Log.Information("[download-member] Recorded download for user {userName}, MD5: {md5}");

        // Get updated download status
        var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();

        using (resp)
        {
            Stream ebookStream = await resp.Content.ReadAsStreamAsync();

            // Attempt cover replacement using shared helper
            ebookStream = await SendToTargetHelpers.TryReplaceCoverAsync(
                ebookStream, coverUrl, fileName, coverService, "download-member");

            // Stream the file back to the client
            var contentType = SendToTargetHelpers.GetEbookContentType(fileName);
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
        // Use shared extended validation helper for all parameters
        var validationError = SendToTargetHelpers.ValidateSendParametersExtended(
            md5, title, coverUrl, authors, fileSize, description, validation);
        if (validationError != null)
            return Results.BadRequest(new { error = validationError });

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
        Log.Information("[library-anna] Recorded download for user {userName}, MD5: {md5}");

        // Get updated download status
        var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();
        var currentTrackingInfo = new AccountFastDownloadInfoDto(currentDownloadsLeft, currentDownloadsPerDay);

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        Directory.CreateDirectory(libraryRoot);

        using (resp)
        {
            Stream ebookStream = await resp.Content.ReadAsStreamAsync();

            // Attempt cover replacement using shared helper
            ebookStream = await SendToTargetHelpers.TryReplaceCoverAsync(
                ebookStream, coverUrl, fileName, coverService, "library-anna");

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
        // Use shared validation helper with coverUrl validation
        var validationError = SendToTargetHelpers.ValidateSendParametersExtended(
            md5, title, coverUrl, authors: null, fileSize: null, description: null, validation);
        if (validationError != null)
            return Results.BadRequest(new { error = validationError });

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

            // Attempt cover replacement using shared helper
            ebookStream = await SendToTargetHelpers.TryReplaceCoverAsync(
                ebookStream, coverUrl, fileName, coverService, "send-to-boox");

            using var stream = ebookStream;

            try
            {
                Log.Information("Uploading '{fileName}' to Dropbox: {uploadPath}");

                var uploaded = await dropbox.Files.UploadAsync(
                    uploadPath,
                    WriteMode.Overwrite.Instance,
                    body: stream
                );

                Log.Information(" Dropbox upload successful! File: {uploaded.PathDisplay}");

                // Get user name from auth context
                var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
                    ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
                    ?? "unknown";

                // Record successful download in our tracking system
                downloadTracking.RecordDownload(md5, userName);
                Log.Information("[send-to-boox] Recorded download for user {userName}, MD5: {md5}");

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
                Log.Warning("Dropbox upload failed: {ErrorMessage} | Details: {Details}", ex.Message, details ?? "N/A");

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
                Log.Warning(" Dropbox upload failed (HTTP {ex.StatusCode}): {ex.Message} | Uri: {ex.RequestUri} | Details: {details}");
                return Results.Ok(new
                {
                    success         = false,
                    message         = "Failed to upload file to Dropbox. Please try again.",
                    accountFastInfo = acctInfo
                });
            }
            catch (Dropbox.Api.DropboxException ex)
            {
                Log.Warning(" Dropbox upload failed (DropboxException): {ex}");
                return Results.Ok(new
                {
                    success         = false,
                    message         = "Failed to upload file to Dropbox. Please try again.",
                    accountFastInfo = acctInfo
                });
            }
            catch (HttpRequestException ex)
            {
                Log.Warning(" Dropbox upload failed (HTTP): {ex}");
                return Results.Ok(new
                {
                    success         = false,
                    message         = "Failed to upload file to Dropbox. Please try again.",
                    accountFastInfo = acctInfo
                });
            }
            catch (ArgumentException ex)
            {
                Log.Information("❌ Invalid argument for send-to-boox: {Message}", ex.Message);
                return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
            }
            catch (Exception ex)
            {
                Log.Warning(" Dropbox upload failed: {ex.Message}");

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
        [FromQuery] string? target,
        [FromQuery] string? coverUrl,
        AnnaArchiveService anna,
        IEmailService emailService,
        IEbookCoverService coverService,
        DropboxClient dropbox,
        IConfiguration cfg,
        IValidationService validation,
        IDownloadTrackingService downloadTracking)
    {
        // Use shared validation helpers with coverUrl validation
        var validationError = SendToTargetHelpers.ValidateSendParametersExtended(
            md5, title, coverUrl, authors: null, fileSize: null, description: null, validation);
        if (validationError != null)
            return Results.BadRequest(new { error = validationError });

        var kindleTargetError = SendToTargetHelpers.ValidateKindleTarget(target);
        if (kindleTargetError != null)
            return Results.BadRequest(new { error = kindleTargetError });

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

                // Attempt cover replacement using shared helper
                ebookStream = await SendToTargetHelpers.TryReplaceCoverAsync(
                    ebookStream, coverUrl, fileName, coverService, "send-to-kindle");

                // Write to temp file
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await ebookStream.CopyToAsync(fileStream);
                }

                // Send email to the appropriate Kindle using shared helper
                var kindleEmail = SendToTargetHelpers.GetKindleEmailForTarget(target!, cfg);

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
                    var dropboxFolder = SendToTargetHelpers.GetDropboxFolderForKindleTarget(target!);
                    var dropboxPath = $"{dropboxFolder}/{fileName}";

                    using (var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        Log.Information("[send-to-kindle] Uploading '{fileName}' to Dropbox: {dropboxPath}");

                        var uploaded = await dropbox.Files.UploadAsync(
                            dropboxPath,
                            WriteMode.Overwrite.Instance,
                            body: fileStream
                        );

                        dropboxPathResult = uploaded.PathDisplay;
                        dropboxSuccess = true;
                        Log.Information(" Dropbox backup successful! Path: {dropboxPathResult}");
                    }
                }
                catch (Dropbox.Api.ApiException<UploadError> ex)
                {
                    var details = ex.ErrorResponse?.ToString() ?? ex.ToString();
                    Log.Information("⚠️ Dropbox backup failed (non-critical): {ex.Message} | Details: {details}");
                }
                catch (Dropbox.Api.HttpException ex)
                {
                    Log.Information("⚠️ Dropbox backup failed (non-critical, HTTP {ex.StatusCode}): {ex.Message}");
                }
                catch (Dropbox.Api.DropboxException ex)
                {
                    Log.Information("⚠️ Dropbox backup failed (non-critical): {ex}");
                }
                catch (ArgumentException ex)
                {
                    Log.Information("⚠️ Dropbox backup failed (non-critical, ArgumentException): {Message}", ex.Message);
                }
                catch (Exception ex)
                {
                    Log.Information("⚠️ Dropbox backup failed (non-critical): {ex.Message}");
                }

                // Get user name from auth context
                var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
                    ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
                    ?? "unknown";

                // Record successful download in our tracking system
                downloadTracking.RecordDownload(md5, userName);
                Log.Information("[send-to-kindle] Recorded download for user {userName}, MD5: {md5}");

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
            catch (ArgumentException ex)
            {
                Log.Information("❌ Invalid argument for send-to-kindle: {Message}", ex.Message);
                return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
            }
            catch (Exception ex)
            {
                Log.Warning(" Send to Kindle failed: {ex.Message}");
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
