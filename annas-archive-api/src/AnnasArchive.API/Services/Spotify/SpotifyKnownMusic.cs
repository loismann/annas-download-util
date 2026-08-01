using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

/// <summary>
/// Builds a best-effort picture of music Paul has demonstrably been exposed to.
///
/// Spotify cannot prove anyone has never heard a song, so this index only ever
/// supports the phrase "does not appear in the Spotify data I can access". The
/// counts of what was *excluded* travel with it for exactly that reason: a library
/// where forty playlists were unreadable makes a much weaker claim than one where
/// none were, and the caller has to be able to say so.
/// </summary>
public static class SpotifyKnownMusic
{
    public static SpotifyKnownMusicIndex Build(
        IReadOnlyList<SpotifyPlaylistContents> library,
        SpotifyTopItemsDto? topTracks = null,
        SpotifyTopItemsDto? topArtists = null,
        IReadOnlyList<SpotifyPlaylistItemDto>? recentTracks = null)
    {
        var artists = new HashSet<string>(StringComparer.Ordinal);
        var tracks = new HashSet<string>(StringComparer.Ordinal);

        var readable = library.Where(c => c.IsReadable).ToList();

        foreach (var item in readable.SelectMany(c => c.Items))
            Add(item, artists, tracks);

        foreach (var item in recentTracks ?? [])
            Add(item, artists, tracks);

        // Top *tracks* carry their artists in Detail; top *artists* are the name.
        foreach (var item in topTracks?.Items ?? [])
        {
            AddKey(tracks, $"{SpotifyPlaylistResolver.Normalize(item.Detail)}|{SpotifyPlaylistResolver.Normalize(item.Name)}");
            foreach (var artist in SplitArtists(item.Detail))
                AddKey(artists, artist);
        }

        foreach (var item in topArtists?.Items ?? [])
            AddKey(artists, SpotifyPlaylistResolver.Normalize(item.Name));

        return new SpotifyKnownMusicIndex(
            artists,
            tracks,
            PlaylistsIncluded: readable.Count,
            UnreadablePlaylists: library.Count - readable.Count,
            IncludesTopItems: topTracks != null || topArtists != null,
            IncludesRecentHistory: recentTracks is { Count: > 0 });
    }

    /// <summary>
    /// True only when the artist is absent from everything we could read. False
    /// means "seen"; true means "not seen", which is weaker than "unfamiliar" and
    /// must be worded that way.
    /// </summary>
    public static bool IsArtistAbsent(this SpotifyKnownMusicIndex index, string? artist)
    {
        var key = SpotifyPlaylistResolver.Normalize(artist);
        return key.Length > 0 && !index.ArtistKeys.Contains(key);
    }

    public static bool IsTrackAbsent(this SpotifyKnownMusicIndex index, string? artist, string? title)
    {
        var key = $"{SpotifyPlaylistResolver.Normalize(artist)}|{SpotifyPlaylistResolver.Normalize(title)}";
        return SpotifyPlaylistResolver.Normalize(title).Length > 0 && !index.TrackKeys.Contains(key);
    }

    /// <summary>
    /// How much of the library the index actually saw. Callers surface this next to
    /// any "probably unfamiliar" claim so the user can judge it.
    /// </summary>
    public static string DescribeCoverage(this SpotifyKnownMusicIndex index)
    {
        var sources = new List<string> { $"{index.PlaylistsIncluded} readable playlists" };
        if (index.IncludesTopItems) sources.Add("your top artists and tracks");
        if (index.IncludesRecentHistory) sources.Add("recent listening");

        var basis = $"Based on {string.Join(", ", sources)}.";

        return index.UnreadablePlaylists == 0
            ? basis
            : basis + $" {index.UnreadablePlaylists} playlist(s) could not be read, so this is a "
                    + "partial picture — absence here is not proof you have not heard something.";
    }

    private static void Add(SpotifyPlaylistItemDto item, HashSet<string> artists, HashSet<string> tracks)
    {
        // Episodes and removed items say nothing about musical familiarity.
        if (item.Kind is SpotifyItemKind.Episode or SpotifyItemKind.Unavailable)
            return;

        AddKey(tracks, SpotifyAnalysis.RecordingKey(item));

        foreach (var artist in SplitArtists(item.Artists))
            AddKey(artists, artist);
    }

    private static void AddKey(HashSet<string> set, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
            set.Add(key);
    }

    /// <summary>
    /// "Robert Johnson, Guest" is two artists. Indexing the joined string would miss
    /// both of them individually.
    /// </summary>
    private static IEnumerable<string> SplitArtists(string? artists) =>
        string.IsNullOrWhiteSpace(artists)
            ? []
            : artists.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(SpotifyPlaylistResolver.Normalize)
                     .Where(key => key.Length > 0);
}
