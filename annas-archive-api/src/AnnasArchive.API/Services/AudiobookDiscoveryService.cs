using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Library;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Turns AI-proposed works into real, requestable Audible editions.
///
/// The model is a recommendation layer and never an execution authority: no
/// suggestion becomes requestable until this service has matched it against
/// Listenarr's own catalog. A suggestion that matches nothing stays
/// "notFound", and several plausible editions stay "ambiguous" so a narrator,
/// language, or abridgement mismatch cannot be silently requested. Taking
/// results[0] because the provider ranked it first is exactly the shortcut
/// this class exists to avoid.
/// </summary>
public sealed class AudiobookDiscoveryService(
    IListenarrService listenarr,
    AudiobookAvailabilityService availability)
{
    /// <summary>Bounded fan-out: one metadata search per suggestion, three at
    /// a time, matching the concurrency cap every other multi-lookup path in
    /// this app uses.</summary>
    private const int ResolveConcurrency = 3;
    private const int MaxChoices = 5;

    /// <summary>Token-set similarity of identical normalized strings is 1.0;
    /// 0.99 is "exact after normalization" with room for rounding.</summary>
    private const double ExactThreshold = 0.99;
    private const double PlausibleTitleThreshold = 0.85;
    private const double PlausibleAuthorThreshold = 0.60;

    public async Task<AudiobookDiscoveryResponse> ResolveAsync(
        string? summary,
        IReadOnlyList<AudiobookDiscoveryCandidate> candidates,
        string? region,
        CancellationToken ct = default)
    {
        var resolvedRegion = availability.ResolveRegion(region);
        var context = await availability.LoadContextAsync(ct);

        var results = new AudiobookDiscoveryResult[candidates.Count];
        using var gate = new SemaphoreSlim(ResolveConcurrency, ResolveConcurrency);
        await Task.WhenAll(candidates.Select(async (candidate, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[index] = await ResolveOneAsync(candidate, resolvedRegion, context, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        return new AudiobookDiscoveryResponse(
            summary,
            resolvedRegion,
            results.Count(result => result.Resolution == "resolved"),
            results.Count(result => result.Resolution == "ambiguous"),
            results.Count(result => result.Resolution == "notFound"),
            results.Count(result => result.Match?.Availability == "owned"),
            results);
    }

    private async Task<AudiobookDiscoveryResult> ResolveOneAsync(
        AudiobookDiscoveryCandidate candidate,
        string region,
        AudiobookAvailabilityContext context,
        CancellationToken ct)
    {
        IReadOnlyList<ListenarrAudibleSearchResult> upstream;
        try
        {
            var response = await listenarr.SearchAudibleAsync(BuildQuery(candidate), region, null, ct);
            upstream = response.Results ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warning(ex, "[Listenarr] AI candidate resolution failed for one suggestion");
            return NotFound(candidate, "The catalog could not be searched for this suggestion. Try again.");
        }

        var scored = upstream
            .Where(edition => !string.IsNullOrWhiteSpace(edition.Asin) && !string.IsNullOrWhiteSpace(edition.Title))
            .GroupBy(edition => edition.Asin!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(edition => new ScoredEdition(edition, Score(candidate, edition)))
            .Where(scoredEdition => scoredEdition.Score.IsPlausible)
            .OrderByDescending(scoredEdition => scoredEdition.Score.IsExact)
            .ThenByDescending(scoredEdition => scoredEdition.Score.Bonus)
            .ThenByDescending(scoredEdition => scoredEdition.Score.Title)
            .ToList();

        if (scored.Count == 0)
            return NotFound(candidate, "No catalog edition matched this suggestion closely enough to request.");

        var exact = scored.Where(scoredEdition => scoredEdition.Score.IsExact).ToList();

        // A narrator the user named themselves is the one preference allowed
        // to decide between otherwise equal editions.
        if (!string.IsNullOrWhiteSpace(candidate.NarratorPreference))
        {
            var preferred = exact
                .Where(scoredEdition => MatchesNarrator(scoredEdition.Edition, candidate.NarratorPreference))
                .ToList();
            if (preferred.Count == 1)
            {
                return Resolved(
                    candidate,
                    availability.Annotate(preferred[0].Edition, context),
                    $"Matched the requested narrator {candidate.NarratorPreference}.");
            }
        }

        if (exact.Count == 1)
            return Resolved(candidate, availability.Annotate(exact[0].Edition, context), null);

        var choices = (exact.Count > 1 ? exact : scored)
            .Take(MaxChoices)
            .Select(scoredEdition => availability.Annotate(scoredEdition.Edition, context))
            .ToList();

        return new AudiobookDiscoveryResult(
            "ambiguous",
            candidate.Title,
            candidate.Author,
            candidate.Reason,
            Match: null,
            choices,
            exact.Count > 1
                ? "Several editions match. Choose the narrator and format you want."
                : "Only an approximate catalog match was found. Confirm the edition before requesting.",
            candidate.NarratorPreference);
    }

    /// <summary>Title plus author is the query Audible ranks best on. Series
    /// and year are used for ranking, not for narrowing the search, so a
    /// slightly wrong AI year cannot hide the real edition.</summary>
    private static string BuildQuery(AudiobookDiscoveryCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Author)
            ? candidate.Title
            : $"{candidate.Title} {candidate.Author}";

    private static EditionScore Score(
        AudiobookDiscoveryCandidate candidate,
        ListenarrAudibleSearchResult edition)
    {
        var title = TitleMatchScorer.TokenSimilarity(candidate.Title, edition.Title);
        var authorNames = Names(edition.Authors);
        var hasAuthor = !string.IsNullOrWhiteSpace(candidate.Author);
        var author = hasAuthor
            ? TitleMatchScorer.CandidateAuthorScore([candidate.Author!], authorNames)
            : 0;

        var bonus = 0;
        if (candidate.Year is { } year && EditionYear(edition) == year) bonus += 2;
        if (!string.IsNullOrWhiteSpace(candidate.Series) && edition.Series is { Count: > 0 } series)
        {
            if (series.Any(entry => TitleMatchScorer.TokenSimilarity(candidate.Series, entry.Name) >= ExactThreshold))
                bonus += 2;
            if (!string.IsNullOrWhiteSpace(candidate.SeriesNumber) &&
                series.Any(entry => string.Equals(
                    entry.Position?.Trim(), candidate.SeriesNumber.Trim(), StringComparison.OrdinalIgnoreCase)))
                bonus += 1;
        }
        if (MatchesNarrator(edition, candidate.NarratorPreference)) bonus += 3;

        // Without an author the app cannot tell two same-titled works apart,
        // so such a suggestion is never "exact" — it always goes to review.
        var isExact = hasAuthor && title >= ExactThreshold && author >= ExactThreshold;
        var isPlausible = hasAuthor
            ? title >= PlausibleTitleThreshold && author >= PlausibleAuthorThreshold
            : title >= ExactThreshold;

        return new EditionScore(title, author, isExact, isPlausible, bonus);
    }

    private static bool MatchesNarrator(ListenarrAudibleSearchResult edition, string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference)) return false;
        var narrators = Names(edition.Narrators);
        return narrators.Length > 0 &&
            TitleMatchScorer.CandidateAuthorScore([preference], narrators) >= ExactThreshold;
    }

    private static int? EditionYear(ListenarrAudibleSearchResult edition) =>
        DateTimeOffset.TryParse(edition.ReleaseDate, out var parsed) ? parsed.Year : null;

    private static string[] Names(IEnumerable<ListenarrAudibleAuthor>? values) => values?
        .Select(value => value.Name?.Trim())
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .ToArray() ?? [];

    private static string[] Names(IEnumerable<ListenarrAudibleNarrator>? values) => values?
        .Select(value => value.Name?.Trim())
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .ToArray() ?? [];

    private static AudiobookDiscoveryResult Resolved(
        AudiobookDiscoveryCandidate candidate, AudiobookSearchResult match, string? note) => new(
        "resolved", candidate.Title, candidate.Author, candidate.Reason, match, [], note,
        candidate.NarratorPreference);

    private static AudiobookDiscoveryResult NotFound(
        AudiobookDiscoveryCandidate candidate, string note) => new(
        "notFound", candidate.Title, candidate.Author, candidate.Reason, null, [], note,
        candidate.NarratorPreference);

    private sealed record ScoredEdition(ListenarrAudibleSearchResult Edition, EditionScore Score);

    private sealed record EditionScore(
        double Title, double Author, bool IsExact, bool IsPlausible, int Bonus);
}
