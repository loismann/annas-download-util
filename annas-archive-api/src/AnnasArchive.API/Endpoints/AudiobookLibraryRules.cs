using AnnasArchive.API.Services;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// The decisions in <see cref="AudiobookLibraryEndpoints"/> that are pure functions
/// of their input.
///
/// <para>All four were private statics in a 513-line endpoint file with no test
/// file, so none had ever been run against anything but a live Audiobookshelf.
/// Two of them are the kind that fails quietly: <see cref="SanitizeId"/> is a
/// traversal guard, and <see cref="ValidateMetadata"/> decides whether an edit is
/// one this app is willing to store. Moved out verbatim, the same way
/// <see cref="MediaLibraryRules"/> was.</para>
/// </summary>
public static class AudiobookLibraryRules
{
    /// <summary>
    /// Audiobookshelf ids are UUID-shaped strings — reject anything containing
    /// path-traversal or separator characters before it is used to build an
    /// outbound Audiobookshelf request or a <see cref="MediaMetadataService"/> key,
    /// same defensive intent as the filename traversal guards elsewhere in this
    /// codebase, adapted for an id forwarded into an upstream API call rather than
    /// a filesystem path.
    /// </summary>
    public static string? SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Any(c => c is '/' or '\\' or ':') || id.Contains("..")) return null;
        return id;
    }

    /// <summary>
    /// The audiobook edit, which is the shared media edit plus a title override.
    ///
    /// <para>Owners and genres are delegated rather than re-implemented: this file
    /// used to carry its own copy of both the tidy-and-dedupe logic and the list of
    /// household members, which meant the same set was written out in three places
    /// and adding a member silently changed only two of them.</para>
    ///
    /// <para>A null or empty title means "not part of this save" —
    /// <c>MediaMetadataService.Set()</c> merges it forward from whatever override
    /// already existed rather than clearing it. There is no way to explicitly
    /// revert to Audiobookshelf's own title once overridden; not needed yet, same
    /// tradeoff as the cover override.</para>
    /// </summary>
    public static MediaItemMetadata? ValidateMetadata(SetMediaMetadataRequest request)
    {
        var shared = MediaLibraryRules.ValidateMetadata(request);
        if (shared is null) return null;

        var title = request.Title?.Trim();
        return shared with { Title = string.IsNullOrEmpty(title) ? null : title };
    }

    /// <summary>
    /// True for the failure shapes an unreachable or slow Audiobookshelf produces
    /// through the resilience pipeline: connection errors, the pipeline's
    /// per-attempt timeout, and HttpClient's own timeout. These become a friendly
    /// 502 rather than an unhandled 500.
    /// </summary>
    public static bool IsUpstreamFailure(Exception ex) =>
        ex is HttpRequestException
        or Polly.Timeout.TimeoutRejectedException
        or TaskCanceledException;

    /// <summary>What to serve a stored cover override as, from its extension alone.</summary>
    public static string ContentTypeForCoverFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
}
