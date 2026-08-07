using System.Text.Json.Nodes;

namespace AnnasArchive.API.Services.Library;

/// <summary>
/// The one rule for "is this Audible edition the same book as this Audiobookshelf
/// item". Both the request reconciler and the search page's availability check ask
/// it, and they used to each carry their own copy — which drifted: one skipped
/// items whose files had gone missing and the other happily offered a Listen
/// button for them.
/// </summary>
public static class AudiobookCatalogMatch
{
    /// <summary>Catalogued but its files are gone. Still listed by Audiobookshelf,
    /// so every caller has to exclude it explicitly.</summary>
    public static bool IsMissing(JsonObject item) =>
        item["isMissing"]?.GetValue<bool?>() == true ||
        item["media"]?["isMissing"]?.GetValue<bool?>() == true;

    /// <summary>
    /// Audiobookshelf titles carry decoration Audible's do not — a series prefix
    /// ("Commonwealth Saga 2 - Judas Unchained"), an edition suffix ("Misspent Youth
    /// [Unabridged]"), a folder-derived year. A strict token-set similarity rejects
    /// every one of those, which is how five freshly imported books stayed
    /// unmatched. So: the wanted title must appear *whole* inside the candidate,
    /// and the author must independently agree.
    ///
    /// Containment is only safe because both guards below hold. A single-word title
    /// is excluded outright ("Exodus" is inside "Exodus: The Archimedes Engine",
    /// which is a different book by the same author), and the author still has to
    /// clear the same bar as before, so an unrelated book never matches.
    /// </summary>
    public static bool TitleAndAuthorMatch(string? wantedTitle, string[] wantedAuthors, JsonObject metadata)
    {
        var candidateTitle = metadata["title"]?.ToString();
        var candidateAuthor = metadata["authorName"]?.ToString();
        if (string.IsNullOrWhiteSpace(candidateTitle) || string.IsNullOrWhiteSpace(candidateAuthor))
            return false;

        if (TitleMatchScorer.NormalizeForMatch(wantedTitle).Count < 2)
            return false;

        return TitleMatchScorer.Coverage(wantedTitle, candidateTitle) >= 1.0 &&
            TitleMatchScorer.CandidateAuthorScore(wantedAuthors, SplitNames(candidateAuthor)) >= 0.80;
    }

    /// <summary>Audiobookshelf reports co-authors and co-narrators as one joined
    /// string; every caller needs them split the same way.</summary>
    public static string[] SplitNames(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split([',', ';', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
