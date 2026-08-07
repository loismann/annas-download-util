using AnnasArchive.API.Configuration;
using Serilog;

namespace AnnasArchive.API.Services.Library;

/// <summary>
/// The four outside sources the enrichment ladder consults, behind one seam.
///
/// They are grouped as a port rather than injected individually because the
/// pipeline's job is deciding <em>which</em> to call and <em>whose answer
/// wins</em> — that decision is the part worth testing, and it is untestable
/// while it is welded to four live HTTP clients.
/// </summary>
public interface IBookMetadataLookups
{
    Task<OpenLibraryData?> OpenLibraryAsync(string title, string[] authors, CancellationToken token);

    Task<AiValidationAndEnrichment?> ValidateWithAiAsync(
        string title, string[] authors, string fileName, OpenLibraryData? openLibrary, CancellationToken token);

    Task<string?> GoogleBooksCoverAsync(string title, string[] authors, CancellationToken token);

    Task<double?> GoodreadsRatingAsync(string title, string[] authors, CancellationToken token);
}

/// <summary>
/// The enrichment ladder: OpenLibrary → AI validation → OpenLibrary again →
/// Google Books → Goodreads, throttled between every step, all writing into one
/// metadata dictionary.
///
/// The shape is deliberate and each rung earns its place. OpenLibrary goes
/// first because it is free and usually right. The model is asked only when
/// OpenLibrary was unsure, and the interesting thing it returns is not
/// metadata but a <em>verdict</em>: either "the catalogue had it right" or "the
/// filename lied, here is the real title". The second answer is what makes the
/// second OpenLibrary call worth making — a title corrected from
/// <c>hp3_pa_scan_v2</c> to <c>Harry Potter and the Prisoner of Azkaban</c>
/// turns a failed lookup into a confident one. Google Books is asked for a
/// cover and nothing else, and Goodreads only for a rating.
///
/// Extracted from a 309-line method where all of this was interleaved with
/// file I/O, EPUB parsing and JSON serialisation.
/// </summary>
public sealed class BookEnrichmentPipeline(
    IBookMetadataLookups lookups,
    IEnrichmentStatsService stats,
    Func<CancellationToken, Task>? throttle = null)
{
    /// <summary>Above this, the catalogue's answer is taken as fact and the
    /// model is never asked.</summary>
    public const double TrustedConfidence = 0.75;

    /// <summary>Below this, the match is recorded as a miss for the stats. It
    /// is deliberately lower than <see cref="TrustedConfidence"/>: "not good
    /// enough to overwrite the filename" and "so bad it did not find the book"
    /// are different failures and are counted separately.</summary>
    public const double PlausibleConfidence = 0.5;

    private readonly Func<CancellationToken, Task> _throttle =
        throttle ?? AiThrottlingConfiguration.ThrottleAsync;

    /// <summary>
    /// Runs every rung against <paramref name="meta"/>, which is mutated in
    /// place. <paramref name="fileName"/> is passed to the model as a hint —
    /// it is frequently the only honest evidence about a book whose internal
    /// metadata is wrong.
    /// </summary>
    public async Task RunAsync(
        Dictionary<string, object?> meta,
        string fileName,
        CancellationToken token)
    {
        var openLibraryData = await RunOpenLibraryAsync(meta, token);
        var correction = await RunAiValidationAsync(meta, fileName, openLibraryData, token);
        await RunOpenLibraryRetryAsync(meta, correction, token);
        await RunGoogleBooksCoverAsync(meta, token);
        await RunGoodreadsAsync(meta, token);
    }

    // ─── Rung 1: OpenLibrary ─────────────────────────────────────────────

    private async Task<OpenLibraryData?> RunOpenLibraryAsync(
        Dictionary<string, object?> meta, CancellationToken token)
    {
        var (title, authors) = TitleAndAuthors(meta);
        if (!LibraryMetadataRules.IsMetadataReliable(title, authors))
            return null;

        var data = await lookups.OpenLibraryAsync(title!, authors, token);
        stats.RecordCall("OpenLibrary", data is { Confidence: >= PlausibleConfidence }, data?.Confidence);

        if (data is not null)
        {
            meta["openLibraryConfidence"] = data.Confidence;
            if (data.Confidence >= TrustedConfidence)
                ApplyCatalogueFacts(meta, data);

            ApplyCoverIfBetter(meta, data.CoverUrl);
            ApplySeriesIfMissing(meta, data.Series);
        }

        await _throttle(token);
        return data;
    }

    // ─── Rung 2: the model's verdict ─────────────────────────────────────

    /// <returns>
    /// The corrected title and authors when the model overrode the catalogue,
    /// otherwise null. Null covers three different outcomes — not asked, no
    /// answer, and "the catalogue was right" — and none of them earns a retry.
    /// </returns>
    private async Task<(string Title, string[] Authors)?> RunAiValidationAsync(
        Dictionary<string, object?> meta,
        string fileName,
        OpenLibraryData? openLibraryData,
        CancellationToken token)
    {
        var (title, authors) = TitleAndAuthors(meta);

        // Never re-ask about a book the model has already ruled on: its answer
        // was written into the file, so a second pass would pay for the same
        // verdict again.
        var alreadyAsked = !string.IsNullOrWhiteSpace(
            LibraryMetadataRules.TryGetMetaValue(meta, "aiEnrichedAt") as string);
        if (alreadyAsked
            || (openLibraryData?.Confidence ?? 0) >= TrustedConfidence
            || !LibraryMetadataRules.IsMetadataReliable(title, authors))
        {
            return null;
        }

        var result = await lookups.ValidateWithAiAsync(title!, authors, fileName, openLibraryData, token);
        stats.RecordCall("GPT4", result is not null);

        (string Title, string[] Authors)? correction = null;

        if (result is not null)
        {
            if (result.UseOpenLibrary && openLibraryData is not null)
            {
                // "You had it right." The catalogue's answer is promoted to
                // trusted, since the doubt about it is what the model resolved.
                ApplyCatalogueFacts(meta, openLibraryData, includePublishYear: false);
                ApplyCoverIfBetter(meta, openLibraryData.CoverUrl);
                meta["openLibraryConfidence"] = Math.Max(openLibraryData.Confidence, TrustedConfidence);
            }
            else if (!string.IsNullOrWhiteSpace(result.Title))
            {
                var correctedAuthors = result.Authors ?? [];

                meta["title"] = result.Title;
                meta["authors"] = correctedAuthors;
                meta["publishedDate"] = result.PublishedDate;
                meta["series"] = result.Series;
                ApplyCoverIfBetter(meta, result.CoverUrl);

                correction = (result.Title!, correctedAuthors);
            }

            meta["aiEnrichedAt"] = DateTime.UtcNow.ToString("o");
        }

        await _throttle(token);
        return correction;
    }

    // ─── Rung 2b: OpenLibrary, asked properly this time ──────────────────

    private async Task RunOpenLibraryRetryAsync(
        Dictionary<string, object?> meta,
        (string Title, string[] Authors)? correction,
        CancellationToken token)
    {
        if (correction is not { } corrected) return;

        Log.Information("[LibraryWatcher] Retrying OpenLibrary with AI-corrected metadata: {Title}", corrected.Title);

        var data = await lookups.OpenLibraryAsync(corrected.Title, corrected.Authors, token);
        stats.RecordCall("OpenLibrary_Retry", data is { Confidence: >= TrustedConfidence }, data?.Confidence);

        // Only a trusted answer is worth having here. A second vague match adds
        // nothing over the correction the model already supplied.
        if (data is { Confidence: >= TrustedConfidence })
        {
            Log.Information("[LibraryWatcher] OpenLibrary retry successful! Confidence: {Confidence}", data.Confidence);
            meta["openLibraryConfidence"] = data.Confidence;
            ApplyCatalogueFacts(meta, data);
            ApplyCoverIfBetter(meta, data.CoverUrl);
            ApplySeriesIfMissing(meta, data.Series);
        }

        await _throttle(token);
    }

    // ─── Rung 3: a cover, and only a cover ───────────────────────────────

    private async Task RunGoogleBooksCoverAsync(Dictionary<string, object?> meta, CancellationToken token)
    {
        if (!NeedsCover(meta)) return;

        var (title, authors) = TitleAndAuthors(meta);
        var cover = await lookups.GoogleBooksCoverAsync(title ?? "", authors, token);
        stats.RecordCall("GoogleBooks", !string.IsNullOrWhiteSpace(cover));

        if (!string.IsNullOrWhiteSpace(cover))
            meta["coverUrl"] = cover;

        await _throttle(token);
    }

    // ─── Rung 4: a rating ────────────────────────────────────────────────

    private async Task RunGoodreadsAsync(Dictionary<string, object?> meta, CancellationToken token)
    {
        var (title, authors) = TitleAndAuthors(meta);
        if (LibraryMetadataRules.TryGetMetaValue(meta, "goodreadsRating") is double
            || !LibraryMetadataRules.IsMetadataReliable(title, authors))
        {
            return;
        }

        var rating = await lookups.GoodreadsRatingAsync(title ?? "", authors, token);
        stats.RecordCall("Goodreads", rating.HasValue);

        if (rating.HasValue)
            meta["goodreadsRating"] = rating.Value;
        else
            meta["goodreadsRating"] ??= null;

        // No throttle: this is the last rung, and delaying after it only slows
        // the book behind this one down.
    }

    // ─── Shared rules ────────────────────────────────────────────────────

    /// <summary>
    /// A cover already on disk under <c>_covers/</c> was extracted from the
    /// book itself, so it is the right cover for this exact edition and no
    /// remote thumbnail beats it. This rule was previously written out by hand
    /// at four call sites, each reading a differently-stale local copy of the
    /// current value.
    /// </summary>
    public static bool ShouldReplaceCover(string? current, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate)
        && string.IsNullOrWhiteSpace(current);

    private static void ApplyCoverIfBetter(Dictionary<string, object?> meta, string? candidate)
    {
        var current = LibraryMetadataRules.TryGetMetaValue(meta, "coverUrl") as string;
        if (ShouldReplaceCover(current, candidate))
            meta["coverUrl"] = candidate;
    }

    private static bool NeedsCover(Dictionary<string, object?> meta)
    {
        var current = LibraryMetadataRules.TryGetMetaValue(meta, "coverUrl") as string;
        return !LibraryMetadataRules.IsLocalCover(current) && string.IsNullOrWhiteSpace(current);
    }

    /// <summary>Title, authors and year — the fields a trusted catalogue match
    /// is allowed to overwrite. Each is written only if the catalogue actually
    /// has it, so a sparse record never blanks a good local value.</summary>
    private static void ApplyCatalogueFacts(
        Dictionary<string, object?> meta, OpenLibraryData data, bool includePublishYear = true)
    {
        if (!string.IsNullOrWhiteSpace(data.Title))
            meta["title"] = data.Title;
        if (data.Authors.Length > 0)
            meta["authors"] = data.Authors;
        if (includePublishYear && data.FirstPublishYear is not null)
            meta["publishedDate"] = data.FirstPublishYear.ToString();
    }

    private static void ApplySeriesIfMissing(Dictionary<string, object?> meta, string? series)
    {
        var current = LibraryMetadataRules.TryGetMetaValue(meta, "series") as string;
        if (string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(series))
            meta["series"] = series;
    }

    private static (string? Title, string[] Authors) TitleAndAuthors(Dictionary<string, object?> meta) =>
        (LibraryMetadataRules.TryGetMetaValue(meta, "title") as string,
         LibraryMetadataRules.TryGetMetaArray(meta, "authors") ?? []);
}
