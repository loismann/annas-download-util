using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for send-to-target operations (Library, Boox, Kindle).
/// Consolidates common patterns across send operations.
/// </summary>
public static class SendToTargetHelpers
{
    /// <summary>
    /// Attempts to replace the cover of an ebook with a new cover from a URL.
    /// Returns the modified stream if successful, or the original stream if cover replacement
    /// is not supported or fails.
    /// </summary>
    /// <param name="ebookStream">The original ebook stream</param>
    /// <param name="coverUrl">URL of the new cover image (can be null/empty to skip)</param>
    /// <param name="fileName">Name of the ebook file (used for extension detection)</param>
    /// <param name="coverService">The cover service to use for replacement</param>
    /// <param name="logPrefix">Prefix for log messages (e.g., "send-to-library")</param>
    /// <returns>The modified stream with the new cover, or the original stream</returns>
    public static async Task<Stream> TryReplaceCoverAsync(
        Stream ebookStream,
        string? coverUrl,
        string fileName,
        IEbookCoverService coverService,
        string logPrefix)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
            return ebookStream;

        var ext = Path.GetExtension(fileName).TrimStart('.');
        if (!coverService.IsFormatSupported(ext))
        {
            Log.Information("[{LogPrefix}] Format {Extension} not supported for cover replacement, skipping",
                logPrefix, ext);
            return ebookStream;
        }

        Log.Information("[{LogPrefix}] Attempting cover replacement for {FileName}",
            logPrefix, fileName);

        try
        {
            return await coverService.ReplaceCoverAsync(ebookStream, coverUrl, ext);
        }
        catch (Exception ex)
        {
            Log.Warning("[{LogPrefix}] Cover replacement failed for {FileName}: {Message}",
                logPrefix, fileName, ex.Message);
            return ebookStream;
        }
    }

    /// <summary>
    /// Gets the content type for an ebook file based on its extension.
    /// </summary>
    /// <param name="fileName">The file name with extension</param>
    /// <returns>The MIME type for the file</returns>
    public static string GetEbookContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".epub" => "application/epub+zip",
            ".pdf" => "application/pdf",
            ".mobi" => "application/x-mobipocket-ebook",
            ".azw3" => "application/vnd.amazon.ebook",
            ".azw" => "application/vnd.amazon.ebook",
            ".kfx" => "application/vnd.amazon.ebook",
            ".fb2" => "text/xml",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Validates common send operation parameters.
    /// </summary>
    /// <param name="md5">The MD5 hash to validate</param>
    /// <param name="title">The title to validate (optional)</param>
    /// <param name="validation">The validation service</param>
    /// <returns>An error message if validation fails, null if valid</returns>
    public static string? ValidateSendParameters(
        string md5,
        string? title,
        IValidationService validation)
    {
        if (!validation.IsValidMd5(md5))
            return "Invalid MD5 format. Must be 32 hexadecimal characters.";

        if (!validation.IsValidTitle(title))
            return "Title too long. Maximum 500 characters.";

        return null;
    }

    /// <summary>
    /// Validates extended send operation parameters including metadata fields.
    /// </summary>
    /// <param name="md5">The MD5 hash to validate</param>
    /// <param name="title">The title to validate (optional)</param>
    /// <param name="coverUrl">The cover URL to validate (optional)</param>
    /// <param name="authors">The authors string to validate (optional)</param>
    /// <param name="fileSize">The file size string to validate (optional)</param>
    /// <param name="description">The description string to validate (optional)</param>
    /// <param name="validation">The validation service</param>
    /// <returns>An error message if validation fails, null if valid</returns>
    public static string? ValidateSendParametersExtended(
        string md5,
        string? title,
        string? coverUrl,
        string? authors,
        string? fileSize,
        string? description,
        IValidationService validation)
    {
        // Run base validation first
        var baseError = ValidateSendParameters(md5, title, validation);
        if (baseError != null)
            return baseError;

        // Validate coverUrl format
        if (!string.IsNullOrEmpty(coverUrl) && !Uri.TryCreate(coverUrl, UriKind.Absolute, out _))
            return "coverUrl is not a valid URL.";

        // Validate authors length
        if (!string.IsNullOrEmpty(authors) && authors.Length > 1000)
            return "authors exceeds maximum length of 1000 characters.";

        // Validate fileSize is a valid numeric string
        if (!string.IsNullOrEmpty(fileSize) && !long.TryParse(fileSize, out var fileSizeValue))
            return "fileSize must be a valid numeric value.";

        if (!string.IsNullOrEmpty(fileSize) && long.TryParse(fileSize, out var parsedSize) && parsedSize < 0)
            return "fileSize must be a non-negative value.";

        // Validate description length
        if (!string.IsNullOrEmpty(description) && description.Length > 5000)
            return "description exceeds maximum length of 5000 characters.";

        return null;
    }

    /// <summary>
    /// Validates Kindle target parameter.
    /// </summary>
    /// <param name="target">The target to validate ("dad" or "mom")</param>
    /// <returns>An error message if validation fails, null if valid</returns>
    public static string? ValidateKindleTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || (target != "dad" && target != "mom"))
            return "Invalid target. Must be 'dad' or 'mom'.";

        return null;
    }

    /// <summary>
    /// Gets the Kindle email address for the specified target.
    /// </summary>
    /// <param name="target">The target ("dad" or "mom")</param>
    /// <param name="cfg">The configuration</param>
    /// <returns>The Kindle email address</returns>
    /// <exception cref="InvalidOperationException">If the email is not configured</exception>
    public static string GetKindleEmailForTarget(string target, IConfiguration cfg)
    {
        return target.ToLower() == "dad"
            ? cfg["Email:DadsKindleEmail"] ?? throw new InvalidOperationException("Email:DadsKindleEmail not configured")
            : cfg["Email:MomsKindleEmail"] ?? throw new InvalidOperationException("Email:MomsKindleEmail not configured");
    }

    /// <summary>
    /// Gets the Dropbox folder path for the specified Kindle target.
    /// </summary>
    /// <param name="target">The target ("dad" or "mom")</param>
    /// <returns>The Dropbox folder path</returns>
    public static string GetDropboxFolderForKindleTarget(string target)
    {
        return target.ToLower() == "dad" ? "/dad_downloads" : "/mom_downloads";
    }
}
