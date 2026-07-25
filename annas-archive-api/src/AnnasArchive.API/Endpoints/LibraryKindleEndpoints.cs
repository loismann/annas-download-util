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
                Log.Warning("[library-send] Dropbox upload failed: {Message}", ex.Message);
                return Results.Ok(new { success = false, message = "Failed to upload file to Dropbox." });
            }

            // Tag the book with its Kindle recipient — NOT with whoever
            // happens to be logged in and triggered the send. This book may
            // already have a real owner tag from whoever downloaded it;
            // adding the acting user's own tag on top would falsely make
            // them look like a co-owner (see HandleSendToKindle's other
            // branch below for the same fix).
            var kindleTargetTag = LibraryHelpers.GetKindleTargetTag(target);
            await LibraryHelpers.AddTagsToLibraryBookAsync(libraryRoot, safeFileName, kindleTargetTag);
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
                Log.Warning("[library-send] Email failed to {Target} ({KindleEmail}): {ErrorMessage}", target, kindleEmail, ex.Message);
                return Results.Ok(new { success = false, message = "Failed to send email to Kindle." });
            }

            // Tag the book with its Kindle recipient — see the Dropbox
            // branch above for why the acting user's own tag isn't added
            // here anymore (it was falsely making them look like a co-owner
            // of books they didn't actually download).
            var kindleTargetTag = LibraryHelpers.GetKindleTargetTag(target);
            await LibraryHelpers.AddTagsToLibraryBookAsync(libraryRoot, safeFileName, kindleTargetTag);
        }

        cache.InvalidateCache();
        return Results.Ok(new { success = true, message = toDropbox ? "Sent to Dropbox." : "Sent to Kindle." });
    }
}
