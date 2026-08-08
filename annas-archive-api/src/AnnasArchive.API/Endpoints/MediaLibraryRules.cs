using AnnasArchive.API.Services;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// The two decisions in <see cref="MediaLibraryEndpoints"/> that are pure functions of
/// their input: rewriting an HLS playlist so its segment URLs come back through this
/// API, and deciding whether a metadata edit is one this app is willing to store.
///
/// Both were private statics in an 827-line endpoint file, so neither had ever been run
/// without an HTTP request and a Jellyfin instance behind it.
/// </summary>
public static class MediaLibraryRules
{
    private static readonly HashSet<string> ValidOwners =
        new(StringComparer.OrdinalIgnoreCase) { "Paul", "Mom", "Dad" };

    /// <summary>
    /// Points every segment URI in a Jellyfin HLS playlist back at this API, carrying an
    /// access token.
    ///
    /// The player fetches segments itself, with no Authorization header and no cookie
    /// it would send cross-origin — so without a token in the URL every segment request
    /// after the playlist is anonymous and gets rejected. Comment lines (<c>#EXTINF</c>
    /// and friends) are structure, not URIs, and must survive untouched or the playlist
    /// stops parsing.
    /// </summary>
    public static string RewriteHlsPlaylist(string playlistText, string itemId, string accessToken)
    {
        var lines = playlistText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // Playlists are served with CRLF; the trailing \r would otherwise land in
            // the middle of the rewritten URL.
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                lines[i] = line;
                continue;
            }

            // A segment URI may already carry query parameters of its own.
            var sep = line.Contains('?') ? '&' : '?';
            lines[i] = $"/api/media/hls/{itemId}/{line}{sep}access_token={Uri.EscapeDataString(accessToken)}";
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The metadata to store for a media item, or null if the request named an owner
    /// this household does not have.
    ///
    /// Rejecting rather than dropping the unknown owner is deliberate: silently saving a
    /// subset would report success for an edit that did not happen. Genres are free text
    /// and are only tidied.
    /// </summary>
    public static MediaItemMetadata? ValidateMetadata(SetMediaMetadataRequest request)
    {
        var owners = Tidy(request.Owners);
        if (owners.Any(o => !ValidOwners.Contains(o)))
            return null;

        return new MediaItemMetadata(owners, Tidy(request.Genres));
    }

    /// <summary>Trimmed, blanks dropped, de-duplicated without regard to case — the
    /// same treatment for both lists, since both arrive from the same free-text UI.</summary>
    private static List<string> Tidy(List<string>? values) =>
        (values ?? [])
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
