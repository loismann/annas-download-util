using AnnasArchive.API.Helpers;
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
}
