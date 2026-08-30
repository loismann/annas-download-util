using AnnasArchive.Core.Models;
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
    /// One log line for a failed Dropbox upload, carrying whatever detail the
    /// specific exception type happens to have.
    ///
    /// This existed as two ladders of four and five catch clauses whose bodies
    /// were byte-identical — the type was being matched only to choose a log
    /// message. Matching here instead lets each call site keep a single catch.
    /// </summary>
    /// <param name="logPrefix">Which endpoint is reporting, e.g. "send-to-boox".</param>
    /// <param name="what">What failed, e.g. "upload" or "backup (non-critical)".</param>
    public static void LogDropboxFailure(string logPrefix, string what, Exception ex)
    {
        switch (ex)
        {
            case Dropbox.Api.ApiException<Dropbox.Api.Files.UploadError> api:
                Log.Warning(api, "[{LogPrefix}] Dropbox {What} failed | Details: {Details}",
                    logPrefix, what, api.ErrorResponse?.ToString() ?? "N/A");
                break;

            case Dropbox.Api.HttpException http:
                Log.Warning(http, "[{LogPrefix}] Dropbox {What} failed (HTTP {StatusCode}) | Uri: {Uri}",
                    logPrefix, what, http.StatusCode, http.RequestUri);
                break;

            default:
                Log.Warning(ex, "[{LogPrefix}] Dropbox {What} failed", logPrefix, what);
                break;
        }
    }

    /// <summary>
    /// Records a completed download against the signed-in user and returns the
    /// updated quota counters.
    /// </summary>
    /// <param name="countsAgainstQuota">
    /// False when the file came from the LibGen fallback. LibGen has no membership
    /// and no daily allowance, so charging one of Anna's slots for it would take a
    /// download away from the reader that Anna's never served. The counters are
    /// still read and returned either way, because the badge has to stay truthful.
    /// </param>
    public static AccountFastDownloadInfoDto RecordDownload(
        HttpContext context,
        IDownloadTrackingService downloadTracking,
        string md5,
        string logPrefix,
        bool countsAgainstQuota = true)
    {
        var userName = context.User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? context.User?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
            ?? "unknown";

        if (countsAgainstQuota)
        {
            downloadTracking.RecordDownload(md5, userName);
            Log.Information("[{LogPrefix}] Recorded download for user {UserName}, MD5: {Md5}", logPrefix, userName, md5);
        }
        else
        {
            Log.Information(
                "[{LogPrefix}] Served {Md5} from LibGen for user {UserName}; not charged to the Anna's allowance",
                logPrefix, md5, userName);
        }

        return AnnaDownloadHelpers.CurrentCounters(downloadTracking);
    }

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
    /// <param name="ct">Cancellation token for the SSRF guard's DNS lookup</param>
    /// <returns>The modified stream with the new cover, or the original stream</returns>
    /// <remarks>
    /// The SSRF guard lives here rather than at the six endpoints that call this,
    /// because here it cannot be forgotten by the seventh. Every one of those
    /// endpoints takes <c>coverUrl</c> straight off the query string and the only
    /// check it got was <c>Uri.TryCreate(…, UriKind.Absolute)</c> — which happily
    /// accepts <c>http://172.18.0.4:7878/api</c> or <c>http://169.254.169.254/</c>,
    /// and <see cref="IEbookCoverService.ReplaceCoverAsync"/> then fetches it from
    /// inside the compose network. `LibraryCoverEndpoints` already guarded its own
    /// copy of this fetch; these six did not.
    /// </remarks>
    public static async Task<Stream> TryReplaceCoverAsync(
        Stream ebookStream,
        string? coverUrl,
        string fileName,
        IEbookCoverService coverService,
        string logPrefix,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
            return ebookStream;

        if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var coverUri) ||
            (coverUri.Scheme != Uri.UriSchemeHttp && coverUri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Warning("[{LogPrefix}] Rejected cover URL that is not an http(s) URL", logPrefix);
            return ebookStream;
        }

        if (!await ValidationHelpers.IsPubliclyRoutableAsync(coverUri, ct))
        {
            Log.Warning("[{LogPrefix}] Rejected cover URL resolving to a non-public address: {CoverHost}",
                logPrefix, coverUri.Host);
            return ebookStream;
        }

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
            Log.Warning(ex, "[{LogPrefix}] Cover replacement failed for {FileName}", logPrefix, fileName);
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

    /// <summary>Validates the Kindle target. Resolution lives in
    /// <see cref="KindleTarget"/> so validation and dispatch cannot drift apart.</summary>
    public static string? ValidateKindleTarget(string? target) =>
        KindleTarget.For(target) is null
            ? $"Invalid target. Must be {KindleTarget.Names}."
            : null;

    /// <summary>The configured Kindle address for a target that has already been validated.</summary>
    /// <exception cref="InvalidOperationException">If the target is unknown, or its email is not configured.</exception>
    public static string GetKindleEmailForTarget(string target, IConfiguration cfg) =>
        Required(target).EmailAddress(cfg);

    /// <summary>The Dropbox folder for a target that has already been validated.</summary>
    public static string GetDropboxFolderForKindleTarget(string target) =>
        Required(target).DropboxFolder;

    /// <summary>
    /// Throws rather than defaulting. Every one of these used to fall through to one
    /// household member or the other, so an unrecognised target produced a confident
    /// send to the wrong person instead of an error.
    /// </summary>
    private static KindleTarget Required(string target) =>
        KindleTarget.For(target)
        ?? throw new InvalidOperationException($"'{target}' is not a Kindle target.");
}
