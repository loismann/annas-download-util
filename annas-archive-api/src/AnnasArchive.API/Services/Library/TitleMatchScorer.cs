namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Jaccard token-set similarity scoring for matching a messy candidate
/// title/author against a metadata-provider search result — shared between
/// LibraryWatcherService (ebooks) and AudiobookEnrichmentService (audiobooks)
/// so the two don't drift with duplicate copies of the same logic.
/// </summary>
public static class TitleMatchScorer
{
    /// <summary>Weighted confidence combining title and author similarity —
    /// same threshold convention used by both callers: >= 0.75 = trust it,
    /// < 0.75 = fall through to the next matching source.</summary>
    public static double Confidence(string? title, string? candidateTitle, string[] authors, string[] candidateAuthors)
    {
        var titleScore = TokenSimilarity(title, candidateTitle);
        var authorScore = CandidateAuthorScore(authors, candidateAuthors);
        return Math.Round((titleScore * 0.7) + (authorScore * 0.3), 3);
    }

    public static double CandidateAuthorScore(string[] inputAuthors, string[] candidateAuthors)
    {
        if (candidateAuthors.Length == 0 || inputAuthors.Length == 0)
            return 0;

        var best = 0.0;
        foreach (var input in inputAuthors)
        {
            foreach (var candidate in candidateAuthors)
            {
                best = Math.Max(best, TokenSimilarity(input, candidate));
            }
        }

        return best;
    }

    public static double TokenSimilarity(string? left, string? right)
    {
        var leftTokens = NormalizeForMatch(left);
        var rightTokens = NormalizeForMatch(right);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0;

        var intersect = leftTokens.Intersect(rightTokens).Count();
        var union = leftTokens.Union(rightTokens).Count();
        return union == 0 ? 0 : (double)intersect / union;
    }

    public static List<string> NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();

        var cleaned = new string(value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray());

        return cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToList();
    }
}
