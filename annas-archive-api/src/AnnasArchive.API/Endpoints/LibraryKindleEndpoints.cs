using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Library Kindle distribution endpoints.
/// </summary>
public static class LibraryKindleEndpoints
{
    /// <summary>
    /// Maps Library Kindle endpoints to the application.
    /// </summary>
    public static WebApplication MapLibraryKindleEndpoints(this WebApplication app)
    {
        // POST /api/library/book/send-to-kindle - Send book to Kindle
        app.MapPost("/api/library/book/send-to-kindle", HandleSendToKindle)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleSendToKindle(
        [FromQuery] string? fileName,
        [FromQuery] string? target,
        [FromQuery] string? title,
        [FromQuery] bool toDropbox,
        IEmailService emailService,
        DropboxClient dropbox,
        IConfiguration cfg,
        LibraryIndexCache cache)
    {
        Log.Information("[library-send] Request fileName='{fileName}' target='{target}' toDropbox={toDropbox}", fileName, target, toDropbox);

        if (string.IsNullOrWhiteSpace(fileName))
            return ApiResponse.BadRequest("fileName is required.");

        // Validate fileName length and title length
        var fileNameValidation = ValidationHelpers.ValidateStringLength(fileName, "fileName", 500);
        if (fileNameValidation != null)
            return fileNameValidation;

        var titleValidation = ValidationHelpers.ValidateStringLength(title, "title", 500);
        if (titleValidation != null)
            return titleValidation;

        // The shared validator, not a fourth copy of the same two string comparisons.
        if (SendToTargetHelpers.ValidateKindleTarget(target) is { } targetError)
            return ApiResponse.BadRequest(targetError);
        var kindleTarget = KindleTarget.For(target)!;

        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return ApiResponse.BadRequest("Invalid fileName.");

        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return ApiResponse.BadRequest("Reader supports EPUB files only.");

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return ApiResponse.NotFound("File not found.");

        var kindleEmail = kindleTarget.EmailAddress(cfg);

        if (toDropbox)
        {
            var uploadFolder = cfg["Dropbox:UploadFolderPath"] ?? string.Empty;
            var dropboxPath = string.IsNullOrWhiteSpace(uploadFolder)
                ? $"/{safeFileName}"
                : $"{uploadFolder.TrimEnd('/')}/{safeFileName}";

            try
            {
                await using var fileStream = File.OpenRead(fullPath);
                Log.Information("[library-send] Uploading '{fileName}' to Dropbox: {dropboxPath}", safeFileName, dropboxPath);
                await dropbox.Files.UploadAsync(
                    dropboxPath,
                    WriteMode.Overwrite.Instance,
                    body: fileStream);
                Log.Information("[library-send] Dropbox upload successful: {dropboxPath}", dropboxPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[library-send] Dropbox upload failed");
                return Results.Json(
                    new { success = false, message = "Failed to upload file to Dropbox." },
                    statusCode: StatusCodes.Status502BadGateway);
            }

        }
        else
        {
            var subject = "Book from Library";
            var body = $"Sent from Library: {title ?? safeFileName}";

            try
            {
                await emailService.SendEmailWithAttachmentAsync(kindleEmail, subject, body, fullPath, safeFileName);
                Log.Information("[library-send] Email sent successfully to {Target} ({KindleEmail}): {FileName}", target, kindleEmail, safeFileName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[library-send] Email failed to {Target} ({KindleEmail})", target, kindleEmail);
                return Results.Json(
                    new { success = false, message = "Failed to send email to Kindle." },
                    statusCode: StatusCodes.Status502BadGateway);
            }

        }

        // Tagged with the *recipient*, never with whoever happened to be signed in and
        // pressed the button — that falsely made the sender look like a co-owner of a
        // book they never downloaded. Both branches did this identically, so it sits
        // after them rather than twice inside.
        await LibraryHelpers.AddTagsToLibraryBookAsync(libraryRoot, safeFileName, kindleTarget.BookTag);

        cache.InvalidateCache();
        return Results.Ok(new { success = true, message = toDropbox ? "Sent to Dropbox." : "Sent to Kindle." });
    }
}
