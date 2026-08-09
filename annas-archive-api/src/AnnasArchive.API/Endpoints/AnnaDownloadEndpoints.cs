using System.Security.Claims;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services.Ai;
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
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/anna/book")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GPT-4 book description
        group.MapGet("/description/gpt", HandleGptDescription);

        // Non-member download links
        group.MapGet("/{md5}/download", HandleDownloadLinks);

        // Member download (stream file to client)
        group.MapPost("/{md5}/download/member", HandleMemberDownload);

        // Send to library (save to Synology disk)
        group.MapPost("/{md5}/send-to-library", HandleSendToLibrary);

        // Send to Boox (upload to Dropbox)
        group.MapPost("/{md5}/send-to-boox", HandleSendToBoox);

        // Send to Kindle (email)
        group.MapPost("/{md5}/send-to-kindle", HandleSendToKindle);

        return app;
    }

    // ─── GPT-4 Description Endpoint ────────────────────────────────────────────

    private static async Task<IResult> HandleGptDescription(
        HttpContext context,
        [FromQuery] string? title,
        [FromQuery] string? author,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IModelSelectionService modelSelection,
        IAiChatCompletion chat)
    {
        // Checked here as well as inside FetchAsync, and deliberately before the
        // token gate: a blank title is a malformed request, and answering it with
        // "you are out of tokens" would blame the user's quota for the caller's bug.
        if (string.IsNullOrWhiteSpace(title))
            return ApiResponse.BadRequest("title is required.");

        // The only source with a spend gate, so it stays here rather than moving
        // into the shared helper — the other three are free.
        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        // The helper records what the call actually cost. This used to add a
        // flat AddUsage(userId, 150, 50) here, charged identically whether the
        // model returned three words or the request failed outright.
        //
        // async, not a bare expression: this source returns a non-null Task<string>
        // and signals "nothing" with an empty string, so it needs adapting to the
        // shared "null means the source has none" contract rather than casting.
        return await DescriptionEndpoint.FetchAsync("GPT-4", title, author,
            async (t, a) => await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                t,
                a ?? "",
                modelSelection.GetModelFast(),
                chat,
                UserHelpers.GetUserIdFromContext(context)));
    }

    // ─── Non-Member Download Links Endpoint ────────────────────────────────────

    private static async Task<IResult> HandleDownloadLinks(
        [FromRoute] string md5,
        AnnasArchiveDownloads svc,
        IValidationService validation)
    {
        if (!validation.IsValidMd5(md5))
            return ApiResponse.BadRequest("Invalid MD5 format. Must be 32 hexadecimal characters.");

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
        AnnasArchiveDownloads anna,
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
            return ApiResponse.BadRequest(validationError);

        var memberKey = cfg["Anna:MemberKey"]
            ?? throw new InvalidOperationException("Missing Anna:MemberKey.");

        // Get user name from auth context
        var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
            ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
            ?? "unknown";

        // Use shared helper to download book from Anna's Archive
        var (resp, fileName, acctInfo, errorMessage, failure) = await AnnaDownloadHelpers.DownloadBookFromAnnasArchiveAsync(md5, title, anna, memberKey);

        if (errorMessage != null)
        {
            // Get current download status even on failure
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            // Non-2xx, but the body is unchanged: accountFastInfo still rides along
            // because a failed attempt can still have consumed a quota slot, and the
            // counter has to stay truthful. The browser reads it from the error.
            return Results.Json(
                new { success = false, message = errorMessage, accountFastInfo = trackingInfo },
                statusCode: AnnaDownloadHelpers.StatusCodeFor(failure));
        }

        if (resp == null || fileName == null)
        {
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
            return Results.Json(
                new { success = false, message = "Failed to download book.", accountFastInfo = trackingInfo },
                statusCode: StatusCodes.Status502BadGateway);
        }

        // Record successful download in our tracking system
        downloadTracking.RecordDownload(md5, userName);
        Log.Information("[download-member] Recorded download for user {UserName}, MD5: {Md5}", userName, md5);

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

    private static IResult HandleSendToLibrary(
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
        IConfiguration cfg,
        Services.IBookDownloadJobService jobs,
        IServiceScopeFactory scopeFactory,
        HttpContext context)
    {
        // Use shared extended validation helper for all parameters
        var validationError = SendToTargetHelpers.ValidateSendParametersExtended(
            md5, title, coverUrl, authors, fileSize, description, validation);
        if (validationError != null)
            return ApiResponse.BadRequest(validationError);

        var memberKey = cfg["Anna:MemberKey"]
            ?? throw new InvalidOperationException("Missing Anna:MemberKey.");

        // Get user name from auth context — resolved here (synchronously, while
        // the request's HttpContext is still valid) rather than inside the
        // detached background task below, since HttpContext isn't safe to touch
        // once this handler returns and the context is recycled for the next request.
        var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
            ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
            ?? "unknown";
        var userTag = LibraryHelpers.ResolveUserLibraryTag(context);
        if (userTag is null)
            // Not fatal — LibraryWatcher:AutoTagNewBooks still gives the book an
            // owner — but it means the download is about to be attributed to the
            // fallback person rather than whoever actually asked for it, and that
            // is worth a line rather than happening in silence.
            Log.Warning("[Anna] Could not resolve a household member for this download; " +
                "the book will fall back to LibraryWatcher:AutoTagNewBooks");

        var job = jobs.Start(title ?? md5);

        // Fire-and-forget: large books can take several minutes to download, far
        // longer than it's reasonable to hold the client's HTTP connection open
        // for. Runs in its own DI scope (AnnasArchiveDownloads is request-scoped —
        // this request's scope is disposed the moment we return the jobId below)
        // and is intentionally not awaited; the frontend polls job status instead.
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var anna = scope.ServiceProvider.GetRequiredService<AnnasArchiveDownloads>();
            var coverService = scope.ServiceProvider.GetRequiredService<IEbookCoverService>();

            try
            {
                var (resp, fileName, _, errorMessage, _) =
                    await AnnaDownloadHelpers.DownloadBookFromAnnasArchiveAsync(md5, title, anna, memberKey);

                if (errorMessage != null || resp == null || fileName == null)
                {
                    jobs.Fail(job.JobId, errorMessage ?? "Failed to download book.");
                    return;
                }

                downloadTracking.RecordDownload(md5, userName);
                Log.Information("[library-anna] Recorded download for user {UserName}, MD5: {Md5}", userName, md5);

                var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
                Directory.CreateDirectory(libraryRoot);

                using (resp)
                {
                    Stream ebookStream = await resp.Content.ReadAsStreamAsync();

                    ebookStream = await SendToTargetHelpers.TryReplaceCoverAsync(
                        ebookStream, coverUrl, fileName, coverService, "library-anna");

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
                Log.Warning(ex, "[library-anna] Background download failed for MD5 {Md5}", md5);
                jobs.Fail(job.JobId, "Download failed: " + ex.Message);
            }
        });

        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);

        return Results.Ok(new
        {
            success = true,
            jobId = job.JobId,
            message = "Download started.",
            accountFastInfo = trackingInfo
        });
    }

    // ─── Send to Boox Endpoint ─────────────────────────────────────────────────

    private static async Task<IResult> HandleSendToBoox(
        HttpContext context,
        [FromRoute] string md5,
        [FromQuery] string? title,
        [FromQuery] string? coverUrl,
        IValidationService validation,
        AnnasArchiveDownloads anna,
        IEbookCoverService coverService,
        DropboxClient dropbox,
        IConfiguration cfg,
        IDownloadTrackingService downloadTracking)
    {
        Log.Information("[send-to-boox] Request received md5={md5} title='{title}' coverUrl='{coverUrl}'", md5, title, coverUrl);

        // Use shared validation helper with coverUrl validation
        var validationError = SendToTargetHelpers.ValidateSendParametersExtended(
            md5, title, coverUrl, authors: null, fileSize: null, description: null, validation);
        if (validationError != null)
            return ApiResponse.BadRequest(validationError);

        var (resp, fileName, acctInfo, downloadError) =
            await AnnaDownloadHelpers.DownloadForSendAsync(md5, title, anna, cfg, downloadTracking);
        if (downloadError != null)
            return downloadError;

        using (resp)
        {
            var uploadFolder = cfg["Dropbox:UploadFolderPath"] ?? string.Empty;
            var uploadPath = string.IsNullOrWhiteSpace(uploadFolder)
                ? $"/{fileName}"
                : $"{uploadFolder.TrimEnd('/')}/{fileName}";
            Stream ebookStream = await resp.Content.ReadAsStreamAsync();

            // Attempt cover replacement using shared helper
            ebookStream = await SendToTargetHelpers.TryReplaceCoverAsync(
                ebookStream, coverUrl, fileName, coverService, "send-to-boox");

            using var stream = ebookStream;

            try
            {
                Log.Information("[send-to-boox] Uploading '{fileName}' to Dropbox: {uploadPath}", fileName, uploadPath);

                var uploaded = await dropbox.Files.UploadAsync(
                    uploadPath,
                    WriteMode.Overwrite.Instance,
                    body: stream
                );

                Log.Information("[send-to-boox] Dropbox upload successful! File: {UploadedPath}", uploaded.PathDisplay);

                return Results.Ok(new
                {
                    success         = true,
                    dropboxPath     = uploaded.PathDisplay,
                    dropboxFileId   = uploaded.Id,
                    accountFastInfo = SendToTargetHelpers.RecordDownload(context, downloadTracking, md5, "send-to-boox")
                });
            }
            // One catch, not five. The five it replaced had byte-identical
            // bodies and differed only in how they logged, which is now the
            // helper's job. ArgumentException is still excluded so the global
            // handler can turn it into a 400.
            catch (Exception ex) when (ex is not ArgumentException)
            {
                SendToTargetHelpers.LogDropboxFailure("send-to-boox", "upload", ex);

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
        AnnasArchiveDownloads anna,
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
            return ApiResponse.BadRequest(validationError);

        var kindleTargetError = SendToTargetHelpers.ValidateKindleTarget(target);
        if (kindleTargetError != null)
            return ApiResponse.BadRequest(kindleTargetError);

        var (resp, fileName, acctInfo, downloadError) =
            await AnnaDownloadHelpers.DownloadForSendAsync(md5, title, anna, cfg, downloadTracking);
        if (downloadError != null)
            return downloadError;

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
                        Log.Information("[send-to-kindle] Uploading '{FileName}' to Dropbox: {DropboxPath}", fileName, dropboxPath);

                        var uploaded = await dropbox.Files.UploadAsync(
                            dropboxPath,
                            WriteMode.Overwrite.Instance,
                            body: fileStream
                        );

                        dropboxPathResult = uploaded.PathDisplay;
                        dropboxSuccess = true;
                        Log.Information(" Dropbox backup successful! Path: {DropboxPath}", dropboxPathResult);
                    }
                }
                // Deliberately swallows everything, including ArgumentException:
                // this is a best-effort backup, and its failure must not fail the
                // download that already succeeded. Unlike the endpoint-level
                // catches, there is nothing here for the global handler to
                // improve — no response is being produced.
                catch (Exception ex)
                {
                    SendToTargetHelpers.LogDropboxFailure("send-to-kindle", "backup (non-critical)", ex);
                }

                return Results.Ok(new
                {
                    success         = true,
                    message         = dropboxSuccess
                        ? $"Book sent to {target}'s Kindle and backed up to Dropbox"
                        : $"Book sent to {target}'s Kindle (Dropbox backup failed, but email succeeded)",
                    dropboxPath     = dropboxPathResult,
                    accountFastInfo = SendToTargetHelpers.RecordDownload(context, downloadTracking, md5, "send-to-kindle")
                });
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                Log.Warning(ex, " Send to Kindle failed");
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
