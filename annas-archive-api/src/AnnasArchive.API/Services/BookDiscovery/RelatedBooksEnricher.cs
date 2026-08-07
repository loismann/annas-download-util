using System.Text.RegularExpressions;
using AnnasArchive.API.Configuration;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;
using AnnasArchive.Core.Telemetry;
using Serilog;

namespace AnnasArchive.API.Services.BookDiscovery;

public interface IRelatedBooksEnricher
{
    /// <summary>
    /// Widens a short series list using the library's own catalogue. Returns the
    /// original list unchanged when the search finds nothing better.
    /// </summary>
    Task<List<SeriesBook>> ExpandSameSeriesAsync(
        List<SeriesBook> sameSeries,
        RelatedBooksRequest request,
        RelatedBooksPayload payload);

    /// <summary>
    /// Fills in missing book descriptions from Wikipedia, falling back to the
    /// model. Both lists share one budget.
    /// </summary>
    /// <param name="billTo">Owner key charged for the description calls. This
    /// pass makes up to eight of them, and every one used to be free as far as
    /// the usage totals were concerned.</param>
    Task<(List<SeriesBook> SameSeries, List<AuthorSeries> OtherSeries)> FillDescriptionsAsync(
        List<SeriesBook> sameSeries,
        List<AuthorSeries> otherSeries,
        string author,
        string model,
        string? billTo);
}

/// <summary>
/// The two things the related-books endpoint does after the model answers, and
/// the reason that handler was 366 lines. Neither is plumbing: the first decides
/// whether to trust the model's series list over the catalogue's, the second
/// spends a rate-limit budget across two lists. Both were previously unreachable
/// from a test.
/// </summary>
public sealed class RelatedBooksEnricher(
    AnnasArchiveService annaArchive,
    IWikipediaService wikipedia,
    IAiChatCompletion chat) : IRelatedBooksEnricher
{
    /// <summary>Below this many titles the model's list is treated as probably
    /// incomplete and worth cross-checking against the catalogue.</summary>
    public const int ExpansionThreshold = 15;

    /// <summary>25, not 80 — Anna's Archive returns ~25 results per page, so a
    /// larger ask forced a second sequential page fetch (each one several
    /// seconds through Playwright) for marginal benefit. This is confirming and
    /// expanding series titles by author+series substring match, not an
    /// exhaustive search.</summary>
    private const int SearchResultLimit = 25;

    public async Task<List<SeriesBook>> ExpandSameSeriesAsync(
        List<SeriesBook> sameSeries,
        RelatedBooksRequest request,
        RelatedBooksPayload payload)
    {
        if (sameSeries.Count >= ExpansionThreshold) return sameSeries;

        var query = payload.SeriesSearchQuery ?? payload.SeriesName ?? $"{request.BookTitle} {request.Author}";

        try
        {
            var searchResults = await annaArchive.SearchAsync(query, SearchResultLimit, exact: false);
            var normalizedAuthor = Normalize(request.Author);
            var normalizedSeries = Normalize(payload.SeriesName ?? request.BookTitle);

            var matches = searchResults
                .Where(b => b.Authors.Any(a => Normalize(a).Contains(normalizedAuthor)))
                .Select(b => b.Title)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .Where(t => Normalize(t!).Contains(normalizedSeries))
                .Select((t, index) => new SeriesBook(t!, index + 1, "", null))
                .ToList();

            // Only when it genuinely found more. A search that matched fewer
            // titles than the model listed is evidence about the catalogue, not
            // about the series.
            if (matches.Count > sameSeries.Count)
            {
                Log.Information("✅ Series expanded via search: {MatchesCount} titles", matches.Count);
                return matches;
            }
        }
        catch (Exception ex)
        {
            // A failed catalogue search costs a longer series list, not the
            // answer — the model's own list is still worth returning.
            Log.Information("⚠️ Series expansion failed: {ExMessage}", ex.Message);
        }

        return sameSeries;
    }

    public async Task<(List<SeriesBook> SameSeries, List<AuthorSeries> OtherSeries)> FillDescriptionsAsync(
        List<SeriesBook> sameSeries,
        List<AuthorSeries> otherSeries,
        string author,
        string model,
        string? billTo)
    {
        // Wikipedia first, then the model. Google Books (quota exhausted) and
        // OpenLibrary (down) used to sit in front of both; every call to either
        // was a guaranteed dead end that only added latency.
        var budget = AiThrottlingConfiguration.MaxRelatedBookDescriptions;
        Log.Information("[Books API] Fetching descriptions for up to {Max} books (sameSeries: {Count})...",
            budget, sameSeries.Count);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var spent = 0;
        var filledSameSeries = new List<SeriesBook>(sameSeries);
        for (var i = 0; i < filledSameSeries.Count && spent < budget; i++)
        {
            var book = filledSameSeries[i];
            if (!NeedsDescription(book.Description)) continue;

            if (spent > 0) await AiThrottlingConfiguration.ThrottleAsync();
            filledSameSeries[i] = await DescribeAsync(book, author, model, billTo);
            spent++;
        }

        var sameSeriesSpent = spent;
        if (filledSameSeries.Count > budget)
        {
            Log.Information("[Books API] Skipped {Count} sameSeries books (over limit)",
                filledSameSeries.Count - budget);
        }

        // otherSeries shares what is left rather than getting its own budget:
        // the limit exists to protect one rate limit, and two independent
        // budgets would let a request spend twice it.
        Log.Information("[Books API] Remaining description quota for otherSeries: {Quota}",
            Math.Max(0, budget - spent));

        var filledOtherSeries = new List<AuthorSeries>(otherSeries);
        for (var i = 0; i < filledOtherSeries.Count && spent < budget; i++)
        {
            var series = filledOtherSeries[i];
            var books = new List<SeriesBook>(series.Books.Count);

            foreach (var book in series.Books)
            {
                if (spent >= budget || !NeedsDescription(book.Description))
                {
                    books.Add(book);
                    continue;
                }

                if (spent > 0) await AiThrottlingConfiguration.ThrottleAsync();
                books.Add(await DescribeAsync(book, author, model, billTo));
                spent++;
            }

            filledOtherSeries[i] = series with { Books = books };
        }

        var otherSeriesSpent = spent - sameSeriesSpent;
        Log.Information("[Books API] Fetched {Total} descriptions (sameSeries: {Same}, otherSeries: {Other})",
            spent, sameSeriesSpent, otherSeriesSpent);
        PerfLog.Record("RelatedBooks.DescriptionLoop", sw.Elapsed.TotalMilliseconds, true,
            ("TotalDescriptions", spent), ("SameSeries", sameSeriesSpent), ("OtherSeries", otherSeriesSpent));

        return (filledSameSeries, filledOtherSeries);
    }

    /// <summary>A description under ten characters is a stub the model emitted
    /// to satisfy the schema, not an answer.</summary>
    private static bool NeedsDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) || description.Length < 10;

    private async Task<SeriesBook> DescribeAsync(
        SeriesBook book,
        string author,
        string model,
        string? billTo)
    {
        var wikiDescription = await wikipedia.GetBookDescriptionAsync(book.Title, author);
        if (!string.IsNullOrWhiteSpace(wikiDescription))
        {
            Log.Information("[Wikipedia] ✓ Got description for '{BookTitle}'", book.Title);
            return book with { Description = wikiDescription, DescriptionSource = "wikipedia" };
        }

        var generated = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
            book.Title, author, model, chat, billTo);
        Log.Information("[GPT-4] ✓ Generated description for '{BookTitle}'", book.Title);
        return book with { Description = generated, DescriptionSource = "gpt" };
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
}
