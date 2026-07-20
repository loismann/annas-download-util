namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for validating input data
/// </summary>
public interface IValidationService
{
    /// <summary>
    /// Validates MD5 hash format (32 hexadecimal characters)
    /// </summary>
    bool IsValidMd5(string md5);

    /// <summary>
    /// Validates Dropbox path format
    /// </summary>
    bool IsValidDropboxPath(string? path);

    /// <summary>
    /// Validates chapter ID (must be non-negative)
    /// </summary>
    bool IsValidChapterId(int chapterId);

    /// <summary>
    /// Validates text length against a maximum
    /// </summary>
    bool IsValidTextLength(string? text, int maxLength = 1_000_000);

    /// <summary>
    /// Validates search query length constraints
    /// </summary>
    bool IsValidSearchQuery(string? query, int minLength = 1, int maxLength = 500);

    /// <summary>
    /// Validates title length
    /// </summary>
    bool IsValidTitle(string? title, int maxLength = 500);
}
