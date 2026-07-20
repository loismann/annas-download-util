using System;
using System.Text.RegularExpressions;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for validating input data
/// </summary>
public class ValidationService : IValidationService
{
    /// <summary>
    /// Validates MD5 hash format (32 hexadecimal characters)
    /// </summary>
    public bool IsValidMd5(string md5) =>
        !string.IsNullOrWhiteSpace(md5) &&
        Regex.IsMatch(md5, "^[a-f0-9]{32}$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Validates Dropbox path format
    /// </summary>
    public bool IsValidDropboxPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // Must start with /
        if (!path.StartsWith('/'))
            return false;

        // Check for path traversal attempts
        if (path.Contains("..") || path.Contains("~"))
            return false;

        // Must end with .epub
        if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            return false;

        // Reasonable length limit (max 500 chars)
        if (path.Length > 500)
            return false;

        return true;
    }

    /// <summary>
    /// Validates chapter ID (must be non-negative and less than 10000)
    /// </summary>
    public bool IsValidChapterId(int chapterId) =>
        chapterId >= 0 && chapterId < 10000; // Reasonable max chapter limit

    /// <summary>
    /// Validates text length against a maximum
    /// </summary>
    public bool IsValidTextLength(string? text, int maxLength = 1_000_000)
    {
        if (string.IsNullOrEmpty(text))
            return true; // Empty is valid, required checks should be separate

        return text.Length <= maxLength;
    }

    /// <summary>
    /// Validates search query length constraints
    /// </summary>
    public bool IsValidSearchQuery(string? query, int minLength = 1, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var trimmed = query.Trim();
        return trimmed.Length >= minLength && trimmed.Length <= maxLength;
    }

    /// <summary>
    /// Validates title length
    /// </summary>
    public bool IsValidTitle(string? title, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(title))
            return true; // Empty is valid for optional fields

        return title.Length <= maxLength;
    }
}
