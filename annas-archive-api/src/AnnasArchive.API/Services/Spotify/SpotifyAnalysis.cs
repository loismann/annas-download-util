using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

/// <summary>
/// Duplicate, overlap and emptiness calculations over already-fetched contents.
///
/// Pure functions on purpose: analysis never calls Spotify and never mutates
/// anything, so the only thing that can be wrong here is arithmetic, and arithmetic
/// is cheap to test. Everything it returns is evidence for a human decision.
///
/// The recurring rule is that an unreadable playlist is excluded from every
/// conclusion rather than treated as empty. A cleanup suggestion built on a partial
/// view is worse than no suggestion.
/// </summary>
public static class SpotifyAnalysis
{
    /// <summary>Spec default. Two playlists sharing this fraction are worth a look.</summary>
    public const double NearDuplicateThreshold = 0.85;

    public static SpotifyLibraryAnalysis Analyze(
        IReadOnlyList<SpotifyPlaylistContents> library,
        double nearDuplicateThreshold = NearDuplicateThreshold,
        IReadOnlyList<SpotifyRecentPlaylistContextDto>? recentContexts = null)
    {
        var readable = library.Where(c => c.IsReadable).ToList();
        var unreadable = library.Where(c => !c.IsReadable).Select(c => c.Playlist).ToList();
        var observedIds = (recentContexts ?? [])
            .Select(context => context.PlaylistId)
            .ToHashSet(StringComparer.Ordinal);
        var observed = library.Select(c => c.Playlist).Where(p => observedIds.Contains(p.Id)).ToList();
        var limitations = new List<string>();
        if (unreadable.Count > 0)
            limitations.Add($"{unreadable.Count} playlist(s) were unavailable or incomplete and were excluded.");
        limitations.Add("Recent playback is a bounded observation window; no observed play is not evidence of no use.");

        return new SpotifyLibraryAnalysis(
            PlaylistsScanned: library.Count,
            PlaylistsRead: readable.Count,
            Unreadable: unreadable,
            Empty: FindEmpty(readable),
            DuplicateItems: readable.SelectMany(FindDuplicateItems).ToList(),
            OverlappingPlaylists: FindOverlaps(readable, nearDuplicateThreshold),
            NamingCollisions: FindNamingCollisions(library.Select(c => c.Playlist).ToList()),
            RecentlyObserved: observed,
            UsageUnknown: library.Count - observed.Count,
            Limitations: limitations,
            GeneratedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Only playlists Spotify let us read and that genuinely hold nothing. A
    /// playlist we could not open is not empty; it is unknown, and it is reported
    /// separately so it can never end up in a delete list.
    /// </summary>
    public static IReadOnlyList<SpotifyEmptyPlaylist> FindEmpty(
        IReadOnlyList<SpotifyPlaylistContents> readable) =>
        readable
            .Where(c => c.Items.Count == 0)
            .Select(c => new SpotifyEmptyPlaylist(c.Playlist.Id, c.Playlist.Name))
            .ToList();

    /// <summary>
    /// Repeats within one playlist. Exact means the same Spotify URI and is a fact.
    /// Probable means the same normalized artist and title — a live version and a
    /// studio cut can collide, so it is reported for review, never auto-selected.
    /// </summary>
    public static IReadOnlyList<SpotifyDuplicateItemGroup> FindDuplicateItems(SpotifyPlaylistContents contents)
    {
        var groups = new List<SpotifyDuplicateItemGroup>();

        var byUri = contents.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Uri))
            .GroupBy(i => i.Uri!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in byUri)
        {
            groups.Add(new SpotifyDuplicateItemGroup(
                contents.Playlist.Id,
                contents.Playlist.Name,
                Describe(group.First()),
                SpotifyDuplicateConfidence.Exact,
                group.Select(i => i.Position).OrderBy(p => p).ToList()));
        }

        // Positions already reported as exact duplicates must not be reported again
        // as probable ones — the same repeat would otherwise appear twice.
        var alreadyReported = groups.SelectMany(g => g.Positions).ToHashSet();

        var byRecording = contents.Items
            .Where(i => i.Kind == SpotifyItemKind.Track && !string.IsNullOrWhiteSpace(i.Name))
            .Where(i => !alreadyReported.Contains(i.Position))
            .GroupBy(RecordingMatchKey, StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in byRecording)
        {
            groups.Add(new SpotifyDuplicateItemGroup(
                contents.Playlist.Id,
                contents.Playlist.Name,
                Describe(group.First()),
                group.Key.StartsWith("isrc:", StringComparison.Ordinal)
                    ? SpotifyDuplicateConfidence.Recording
                    : SpotifyDuplicateConfidence.Probable,
                group.Select(i => i.Position).OrderBy(p => p).ToList()));
        }

        return groups;
    }

    /// <summary>
    /// Every readable pair that overlaps at all, keeping exact ordered matches,
    /// multisets contained by another playlist, or pairs above the set-Jaccard
    /// threshold. Ordering and repeated occurrences therefore remain part of the
    /// exact-duplicate decision without distorting near-duplicate similarity.
    /// </summary>
    public static IReadOnlyList<SpotifyPlaylistOverlap> FindOverlaps(
        IReadOnlyList<SpotifyPlaylistContents> readable,
        double nearDuplicateThreshold = NearDuplicateThreshold)
    {
        var sets = readable
            .Select(c => (Contents: c, Sequence: c.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Uri))
                .Select(i => i.Uri!)
                .ToList()))
            .Where(x => x.Sequence.Count > 0)
            .ToList();

        var overlaps = new List<SpotifyPlaylistOverlap>();

        for (var i = 0; i < sets.Count; i++)
        {
            for (var j = i + 1; j < sets.Count; j++)
            {
                var (left, leftSequence) = sets[i];
                var (right, rightSequence) = sets[j];
                var leftUris = leftSequence.ToHashSet(StringComparer.Ordinal);
                var rightUris = rightSequence.ToHashSet(StringComparer.Ordinal);

                var shared = leftUris.Count(rightUris.Contains);
                if (shared == 0)
                    continue;

                var union = leftUris.Count + rightUris.Count - shared;
                var jaccard = (double)shared / union;

                var identical = leftSequence.SequenceEqual(rightSequence, StringComparer.Ordinal);
                var supersetOf =
                    !identical && leftSequence.Count > rightSequence.Count && ContainsMultiset(leftSequence, rightSequence) ? right.Playlist.Id
                    : !identical && rightSequence.Count > leftSequence.Count && ContainsMultiset(rightSequence, leftSequence) ? left.Playlist.Id
                    : null;

                if (!identical && supersetOf == null && jaccard < nearDuplicateThreshold)
                    continue;

                overlaps.Add(new SpotifyPlaylistOverlap(
                    left.Playlist.Id, left.Playlist.Name,
                    right.Playlist.Id, right.Playlist.Name,
                    shared,
                    leftUris.Count - shared,
                    rightUris.Count - shared,
                    jaccard,
                    identical,
                    supersetOf));
            }
        }

        return overlaps.OrderByDescending(o => o.Overlap).ToList();
    }

    /// <summary>
    /// Playlists whose names differ only by punctuation, spacing or case. Includes
    /// unreadable ones — a name collision is visible without reading contents, and
    /// it is exactly the case where the assistant must ask which one you meant.
    /// </summary>
    public static IReadOnlyList<SpotifyNamingCollision> FindNamingCollisions(
        IReadOnlyList<SpotifyPlaylistDto> playlists) =>
        playlists
            .GroupBy(p => NormalizeComparableName(p.Name), StringComparer.Ordinal)
            .Where(g => g.Count() > 1 && g.Key.Length > 0)
            .Select(g => new SpotifyNamingCollision(g.Key, g.ToList()))
            .ToList();

    /// <summary>
    /// Artist + title, normalized. Deliberately blunt: it forgives punctuation and
    /// case but not a remix or a live version, because merging those is a judgement
    /// only the listener can make.
    /// </summary>
    internal static string RecordingKey(SpotifyPlaylistItemDto item) =>
        $"{SpotifyPlaylistResolver.Normalize(item.Artists)}|{SpotifyPlaylistResolver.Normalize(item.Name)}";

    private static string RecordingMatchKey(SpotifyPlaylistItemDto item) =>
        !string.IsNullOrWhiteSpace(item.Isrc)
            ? $"isrc:{item.Isrc.Trim().ToUpperInvariant()}"
            : $"name:{RecordingKey(item)}";

    private static bool ContainsMultiset(IReadOnlyList<string> possibleSuperset, IReadOnlyList<string> subset)
    {
        var counts = possibleSuperset
            .GroupBy(uri => uri, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var uri in subset)
        {
            if (!counts.TryGetValue(uri, out var remaining) || remaining == 0)
                return false;
            counts[uri] = remaining - 1;
        }
        return true;
    }

    private static string NormalizeComparableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        var withoutYear = System.Text.RegularExpressions.Regex.Replace(
            name, @"(?:[\s\-–—_()\[\]]*)(?:19|20)\d{2}\s*$", string.Empty);
        return SpotifyPlaylistResolver.Normalize(withoutYear);
    }

    private static string Describe(SpotifyPlaylistItemDto item) =>
        string.IsNullOrWhiteSpace(item.Artists) ? item.Name ?? "Unknown" : $"{item.Name} — {item.Artists}";
}
