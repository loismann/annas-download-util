namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Service for extracting metadata from ebook files.
/// </summary>
public interface IMetadataExtractionService
{
    /// <summary>
    /// Extracts metadata from an EPUB file including title, authors, date, pages, and cover.
    /// </summary>
    /// <param name="filePath">Path to the EPUB file.</param>
    /// <param name="libraryRoot">Library root directory for cover storage.</param>
    /// <param name="skipCoverIfLocalExists">If true, skip cover extraction when local cover exists.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Extracted metadata, or null if extraction fails.</returns>
    Task<EpubExtractedMetadata?> ExtractEpubMetadataAsync(
        string filePath,
        string libraryRoot,
        bool skipCoverIfLocalExists,
        CancellationToken token);

    /// <summary>
    /// Extracts a cover image from a .sdr sidecar folder.
    /// </summary>
    /// <param name="filePath">Path to the book file.</param>
    /// <param name="libraryRoot">Library root directory for cover storage.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Cover URL (e.g., "_covers/filename.cover.jpg") or null if not found.</returns>
    Task<string?> ExtractSdrCoverAsync(string filePath, string libraryRoot, CancellationToken token);

    /// <summary>
    /// Parses title and author information from a filename.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>Tuple of (title, authors) where either may be null.</returns>
    (string? Title, string[]? Authors) ParseTitleAuthorFromFileName(string filePath);
}

/// <summary>
/// Metadata extracted from an EPUB file.
/// </summary>
public record EpubExtractedMetadata(
    string? Title,
    string[]? Authors,
    string? PublishedDate,
    string? Pages,
    string? CoverUrl);
