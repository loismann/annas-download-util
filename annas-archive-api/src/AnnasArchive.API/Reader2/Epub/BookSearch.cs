namespace AnnasArchive.API.Reader2.Epub;

/// <summary>Where a search term was found, and enough context to recognise it.</summary>
public sealed record SearchHit(int ChapterId, string ChapterTitle, int MatchCount, string Snippet, int FirstWordOffset);

/// <summary>Why a query was refused, so the UI can say something useful.</summary>
public sealed record SearchRejected(string Reason);

/// <summary>
/// Whole-book text search. No AI, no index, no spend — just a scan of the
/// extracted chapters, which is fast enough for a single book and costs nothing.
///
/// <para>Reader I imposed a ten-character minimum with no explanation, which
/// blocks "Pierre", "Moscow", "Rostov", and most other character and place names
/// — the searches a reader of a long novel actually performs. Three is the
/// default here.</para>
/// </summary>
public static class BookSearch
{
    public const int DefaultMinQueryLength = 3;
    public const int DefaultMaxQueryLength = 500;
    public const int DefaultMaxHits = 100;
    private const int SnippetRadius = 60;

    /// <summary>
    /// Checks a query before any file is read. Returns null when it is usable.
    /// </summary>
    public static SearchRejected? Validate(
        string? query, int minLength = DefaultMinQueryLength, int maxLength = DefaultMaxQueryLength)
    {
        var trimmed = query?.Trim() ?? "";

        if (trimmed.Length < minLength)
            return new SearchRejected($"Search for at least {minLength} characters.");

        return trimmed.Length > maxLength
            ? new SearchRejected($"Search for at most {maxLength} characters.")
            : null;
    }

    /// <summary>
    /// Searches chapters already extracted. <paramref name="readChapter"/> is
    /// injected so this stays a pure function of its inputs and can be tested
    /// without a filesystem.
    /// </summary>
    public static IReadOnlyList<SearchHit> Run(
        string query,
        IEnumerable<Chapter> chapters,
        Func<Chapter, string?> readChapter,
        int maxHits = DefaultMaxHits)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return [];

        var hits = new List<SearchHit>();

        foreach (var chapter in chapters)
        {
            var text = readChapter(chapter);
            if (string.IsNullOrEmpty(text)) continue;

            var first = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (first < 0) continue;

            hits.Add(new SearchHit(
                chapter.Id,
                chapter.Title,
                CountOccurrences(text, needle),
                Snippet(text, first, needle.Length),
                WordOffsetOf(text, first)));

            if (hits.Count >= maxHits) break;
        }

        return hits;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var at = 0;

        while ((at = text.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    /// <summary>A window around the match, with ellipses where text was cut.</summary>
    private static string Snippet(string text, int at, int length)
    {
        var start = Math.Max(0, at - SnippetRadius);
        var end = Math.Min(text.Length, at + length + SnippetRadius);

        var snippet = text[start..end].Replace('\n', ' ').Trim();
        if (start > 0) snippet = "…" + snippet;
        if (end < text.Length) snippet += "…";

        return snippet;
    }

    /// <summary>
    /// Words before the match, so the reader can be paged straight to it —
    /// the same unit reading position and section boundaries use.
    /// </summary>
    private static int WordOffsetOf(string text, int characterIndex) =>
        EpubTextExtractor.CountWords(text[..characterIndex]);
}
