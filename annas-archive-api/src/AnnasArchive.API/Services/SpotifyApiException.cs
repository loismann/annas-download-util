using System.Net;

namespace AnnasArchive.API.Services;

/// <summary>
/// Preserves the actionable parts of a failed Spotify response without carrying
/// or logging the complete upstream response body.
/// </summary>
public sealed class SpotifyApiException : HttpRequestException
{
    public SpotifyApiException(
        HttpStatusCode statusCode,
        string? spotifyMessage = null,
        string? reason = null,
        TimeSpan? retryAfter = null)
        : base(
            spotifyMessage is null
                ? $"Spotify API returned {(int)statusCode} ({statusCode})."
                : $"Spotify API returned {(int)statusCode} ({statusCode}): {spotifyMessage}",
            inner: null,
            statusCode)
    {
        SpotifyStatusCode = statusCode;
        SpotifyMessage = spotifyMessage;
        Reason = reason;
        RetryAfter = retryAfter;
    }

    public string? SpotifyMessage { get; }

    public HttpStatusCode SpotifyStatusCode { get; }

    public string? Reason { get; }

    public TimeSpan? RetryAfter { get; }

    public bool IsQuotaExceeded =>
        string.Equals(Reason, "QUOTA_EXCEEDED", StringComparison.OrdinalIgnoreCase);
}
