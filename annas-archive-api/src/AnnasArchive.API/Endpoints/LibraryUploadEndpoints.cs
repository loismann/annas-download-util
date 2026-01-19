using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services.Library;
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

    private const long MaxFileSizeBytes = 500 * 1024 * 1024; // 500MB

    /// <summary>
    /// Maps Library upload endpoints to the application.
    /// </summary>
    public static WebApplication MapLibraryUploadEndpoints(this WebApplication app)
    {
        // POST /api/library/book/upload - Upload a book file
        app.MapPost("/api/library/book/upload", HandleUploadBook)
            .RequireAuthorization()
            .RequireRateLimiting("api")
            .DisableAntiforgery();

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
        // Check if user is admin
        var isAdmin = context.User?.HasClaim("IsAdmin", "true") ?? false;
        if (!isAdmin)
        {
            Log.Warning("[LibraryUpload] Non-admin user attempted to upload a book");
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
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        // Handle both Unix and Windows path separators for cross-platform compatibility
        var name = fileName;
        var lastSlash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (lastSlash >= 0)
            name = name.Substring(lastSlash + 1);

        // Remove any null bytes or control characters (0x00-0x1F)
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (c >= 0x20) // Only keep characters >= space
                sb.Append(c);
        }
        name = sb.ToString();

        // Replace problematic characters with safe alternatives
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            name = name.Replace(c, '_');
        }

        // Prevent directory traversal attempts
        name = name.Replace("..", "_");

        // Trim whitespace and dots from start/end
        name = name.Trim().Trim('.');

        // Limit filename length
        if (name.Length > 255)
        {
            var ext = Path.GetExtension(name);
            var baseName = Path.GetFileNameWithoutExtension(name);
            var maxBaseLength = 255 - ext.Length;
            name = baseName.Substring(0, Math.Min(baseName.Length, maxBaseLength)) + ext;
        }

        return name;
    }
}
