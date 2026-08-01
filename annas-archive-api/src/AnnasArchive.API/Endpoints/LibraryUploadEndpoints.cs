using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services.Library;
using AnnasArchive.Core.Helpers;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Library file upload endpoints.
/// Admin-only endpoints for uploading book files to the library.
/// </summary>
public static class LibraryUploadEndpoints
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx", ".pobi", ".fb2", ".txt", ".rtf", ".lit", ".djvu"
    };

    // Same ceiling as Kestrel and the body-size middleware, so a file that gets
    // past the transport is never rejected here for a different reason.
    private const long MaxFileSizeBytes = Limits.MaxRequestBodySize;

    /// <summary>
    /// Maps Library upload endpoints to the application.
    /// </summary>
    public static WebApplication MapLibraryUploadEndpoints(this WebApplication app)
    {
        // POST /api/library/book/upload - Upload a book file
        app.MapPost("/api/library/book/upload", HandleUploadBook)
            .RequireAuthorization()
            .RequireRateLimiting("api")
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxFileSizeBytes))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxFileSizeBytes });

        // GET /api/library/upload/supported-formats - Get list of supported formats
        app.MapGet("/api/library/upload/supported-formats", HandleGetSupportedFormats)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleGetSupportedFormats()
    {
        return Results.Ok(new
        {
            formats = SupportedExtensions.OrderBy(e => e).ToArray(),
            maxFileSizeMb = MaxFileSizeBytes / (1024 * 1024)
        });
    }

    private static async Task<IResult> HandleUploadBook(
        HttpRequest request,
        HttpContext context,
        IDuplicateDetectionService duplicateDetection,
        IMetadataExtractionService metadataExtraction)
    {
        // Check if user is admin (Role claim is set in AuthEndpoints)
        var isAdmin = context.User?.IsInRole("Admin") ?? false;
        if (!isAdmin)
        {
            Log.Warning("[LibraryUpload] Non-admin user {User} attempted to upload a book",
                context.User?.Identity?.Name ?? "unknown");
            return Results.Forbid();
        }

        // Read the form with the file
        IFormFile? file;
        try
        {
            var form = await request.ReadFormAsync();
            file = form.Files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warning("[LibraryUpload] Failed to read form data: {Error}", ex.Message);
            return Results.BadRequest(new { error = "Failed to read upload data." });
        }

        if (file == null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "No file provided." });
        }

        // Validate file size
        if (file.Length > MaxFileSizeBytes)
        {
            return Results.BadRequest(new
            {
                error = $"File too large. Maximum size is {MaxFileSizeBytes / (1024 * 1024)}MB."
            });
        }

        // Sanitize and validate filename
        var originalFileName = file.FileName;
        var safeFileName = SanitizeFileName(originalFileName);

        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            return Results.BadRequest(new { error = "Invalid filename." });
        }

        // Validate extension
        var extension = Path.GetExtension(safeFileName);
        if (!SupportedExtensions.Contains(extension))
        {
            return Results.BadRequest(new
            {
                error = $"Unsupported file format. Supported formats: {string.Join(", ", SupportedExtensions.OrderBy(e => e))}"
            });
        }

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        Directory.CreateDirectory(libraryRoot);

        // Parse title and author from filename for duplicate check
        var parsed = metadataExtraction.ParseTitleAuthorFromFileName(safeFileName);
        var title = parsed.Title ?? Path.GetFileNameWithoutExtension(safeFileName);
        var authors = parsed.Authors ?? Array.Empty<string>();

        // Check for duplicates by title/author
        var existingDuplicate = duplicateDetection.FindExistingDuplicate(libraryRoot, title, authors);
        if (!string.IsNullOrWhiteSpace(existingDuplicate))
        {
            var existingFileName = Path.GetFileName(existingDuplicate);
            Log.Information("[LibraryUpload] Duplicate detected: {FileName} matches existing {ExistingFileName}",
                safeFileName, existingFileName);
            return Results.Conflict(new
            {
                error = "A book with this title and author already exists in the library.",
                existingFileName
            });
        }

        // Check if file already exists (exact filename match)
        var targetPath = Path.Combine(libraryRoot, safeFileName);
        if (File.Exists(targetPath))
        {
            // Generate unique filename by appending a number
            var baseName = Path.GetFileNameWithoutExtension(safeFileName);
            var counter = 1;
            while (File.Exists(targetPath))
            {
                safeFileName = $"{baseName} ({counter}){extension}";
                targetPath = Path.Combine(libraryRoot, safeFileName);
                counter++;

                if (counter > 100)
                {
                    return Results.Conflict(new { error = "Too many files with similar names." });
                }
            }
        }

        // Save the file
        try
        {
            await using var stream = new FileStream(targetPath, FileMode.Create);
            await file.CopyToAsync(stream);

            Log.Information("[LibraryUpload] Successfully uploaded {FileName} ({Size})",
                safeFileName, LibraryHelpers.FormatFileSize(file.Length));
        }
        catch (Exception ex)
        {
            Log.Error("[LibraryUpload] Failed to save file {FileName}: {Error}", safeFileName, ex.Message);
            return Results.Problem("Failed to save the uploaded file.");
        }

        // The LibraryWatcherService will automatically detect the new file and process it
        // We'll return success immediately without waiting for enrichment

        return Results.Ok(new
        {
            success = true,
            fileName = safeFileName,
            fileSize = LibraryHelpers.FormatFileSize(file.Length),
            message = "File uploaded successfully. Metadata enrichment will happen automatically."
        });
    }

    /// <summary>
    /// Sanitizes a filename to prevent directory traversal and remove unsafe characters.
    ///
    /// An empty fallback rather than the shared default: the caller treats a
    /// blank result as "reject this upload", so a name that sanitises away to
    /// nothing must stay blank instead of becoming a plausible-looking file.
    /// </summary>
    private static string SanitizeFileName(string fileName) =>
        SafeFileName.ForUserInput(fileName, fallback: string.Empty);
}
