using System.Globalization;
using System.Text;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

/// <summary>
/// Turns a phrase the user typed ("my Road Trip playlist") into exactly one
/// playlist, or admits it cannot.
///
/// Spotify does not enforce unique playlist names, and people reuse them heavily —
/// so guessing is the one thing this must never do. Ambiguity returns every
/// candidate for the user to choose from; the language model is not consulted,
/// because it has no way to know which of two identically named playlists is meant.
/// </summary>
public static class SpotifyPlaylistResolver
{
    /// <summary>
    /// Ownership tiers, tried in order. Something you own beats something you
    /// collaborate on, which beats something you merely follow — a name collision
    /// with a stranger's public playlist should not shadow your own.
    /// </summary>
    public static SpotifyPlaylistResolution Resolve(
        string? reference,
        IReadOnlyList<SpotifyPlaylistDto> playlists)
    {
        if (string.IsNullOrWhiteSpace(reference) || playlists.Count == 0)
            return SpotifyPlaylistResolution.NotFound();

        var needle = reference.Trim();

        // 1-3: exact name, most-owned tier first.
        foreach (var (tier, label) in OwnershipTiers)
        {
            var exact = playlists
                .Where(tier)
                .Where(p => string.Equals(p.Name, needle, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exact.Count == 1)
                return SpotifyPlaylistResolution.Resolved(exact[0], $"exact name ({label})");

            if (exact.Count > 1)
                return SpotifyPlaylistResolution.Ambiguous(exact);
        }

        // 4: normalised — punctuation, spacing and case removed. "Road Trip - 2026"
        // and "road trip 2026" are the same playlist to a person.
        var normalizedNeedle = Normalize(needle);
        if (normalizedNeedle.Length > 0)
        {
            var normalized = playlists
                .Where(p => Normalize(p.Name) == normalizedNeedle)
                .ToList();

            if (normalized.Count == 1)
                return SpotifyPlaylistResolution.Resolved(normalized[0], "normalized name");

            if (normalized.Count > 1)
                return SpotifyPlaylistResolution.Ambiguous(normalized);
        }

        // 5: substring, and only when it leaves exactly one answer. Two hits is a
        // question for the user, not a coin toss.
        var partial = playlists
            .Where(p => Normalize(p.Name).Contains(normalizedNeedle, StringComparison.Ordinal))
            .ToList();

        return partial.Count switch
        {
            1 => SpotifyPlaylistResolution.Resolved(partial[0], "partial name"),
            > 1 => SpotifyPlaylistResolution.Ambiguous(partial),
            _ => SpotifyPlaylistResolution.NotFound()
        };
    }

    /// <summary>Name-fragment filter behind "show my Best Of playlists".</summary>
    public static IReadOnlyList<SpotifyPlaylistDto> Filter(
        string? query,
        IReadOnlyList<SpotifyPlaylistDto> playlists)
    {
        if (string.IsNullOrWhiteSpace(query))
            return playlists;

        var needle = Normalize(query);
        if (needle.Length == 0)
            return playlists;

        return playlists
            .Where(p => Normalize(p.Name).Contains(needle, StringComparison.Ordinal))
            .ToList();
    }

    private static readonly (Func<SpotifyPlaylistDto, bool> Predicate, string Label)[] OwnershipTiers =
    [
        (p => p.IsOwnedByUser, "owned"),
        (p => !p.IsOwnedByUser && p.IsCollaborative, "collaborative"),
        (p => !p.IsOwnedByUser && !p.IsCollaborative, "followed")
    ];

    /// <summary>
    /// Case-folded, accent-stripped, alphanumerics only. Deliberately crude: it
    /// exists to forgive punctuation and spacing, not to fuzzy-match different
    /// names onto each other.
    /// </summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
