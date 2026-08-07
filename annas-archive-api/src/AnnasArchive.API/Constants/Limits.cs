namespace AnnasArchive.API.Constants;

/// <summary>
/// Limit constants for rate limiting, uploads and data constraints.
///
/// Every value here is read by something. Eleven others used to sit alongside
/// them referenced by nothing, while the numbers that actually governed the
/// behaviour were written out again at the call site. That is not a cosmetic
/// problem: <see cref="MaxRequestBodySize"/> spent a long time saying 20 MB
/// while the three real limits were independently set to 500 MB, 500 MB and
/// 10 MB, and nobody could tell by reading this file.
///
/// The rule: a number belongs here once more than one place needs it, or once a
/// call site would otherwise hardcode a value this file also claims to own. A
/// constant nothing reads is worse than no constant, because it reads as the
/// answer.
/// </summary>
public static class Limits
{
    // ========================================================================
    // Rate Limiting
    // ========================================================================

    /// <summary>
    /// Default API rate limit per minute per IP, used when neither
    /// <c>API_RATE_LIMIT</c> nor <c>E2E_API_RATE_LIMIT</c> is configured.
    /// </summary>
    public const int DefaultApiRateLimit = 60;

    /// <summary>
    /// Media-proxy rate limit per minute per IP — covers and streams, which are
    /// one request per tile. Sized to the library rather than to typical traffic:
    /// a fast scroll through ~1000 items must fit inside one window. This is an
    /// anti-runaway guard on a Tailscale-only app, not an abuse defence.
    /// </summary>
    public const int MediaRateLimit = 2000;

    /// <summary>
    /// Login attempt rate limit per minute per IP, used when neither
    /// <c>LOGIN_RATE_LIMIT</c> nor <c>E2E_LOGIN_RATE_LIMIT</c> is configured.
    /// </summary>
    public const int LoginRateLimit = 5;

    // ========================================================================
    // Content Limits
    // ========================================================================

    /// <summary>
    /// Maximum request body size in bytes (500 MB).
    ///
    /// The single source of truth for this, referenced by Kestrel's limit, the
    /// body-size middleware and the upload endpoint alike. It is sized for the
    /// largest thing anyone actually posts — an ebook upload.
    /// </summary>
    public const long MaxRequestBodySize = 500L * 1024 * 1024;

    // ========================================================================
    // Download Tracking
    // ========================================================================

    /// <summary>
    /// Default download limit per rolling window, used when
    /// <c>DownloadTracking:DownloadLimit</c> is not configured.
    /// </summary>
    public const int DefaultDownloadLimit = 50;

    /// <summary>
    /// Default rolling window for download tracking in hours, used when
    /// <c>DownloadTracking:RollingWindowHours</c> is not configured.
    /// </summary>
    public const double DefaultDownloadWindowHours = 18;
}
