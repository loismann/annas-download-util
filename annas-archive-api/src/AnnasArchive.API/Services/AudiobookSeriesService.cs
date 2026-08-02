using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Capped, previewed multi-book requests for one series.
///
/// Every safeguard here exists because this is the only path that can create
/// many Listenarr entries from one click: the user sees exactly what will
/// happen before confirming, the server decides which members are even
/// eligible, the confirmation may only be a subset of that set, the ceiling
/// is enforced server-side, and each book is added through the same
/// idempotent single-book path so a partial failure neither rolls back the
/// successes nor duplicates them.
/// </summary>
public sealed class AudiobookSeriesService(
    IListenarrService listenarr,
    AudiobookAvailabilityService availability,
    AudiobookRequestService requests,
    AudiobookRequestTokenStore tokens)
{
    /// <summary>Ordinary household ceiling per confirmation. Above this an
    /// administrator must confirm a second time.</summary>
    public const int RequestCeiling = 25;

    /// <summary>Hard stop even for administrators — a preview the user cannot
    /// realistically read is not informed consent.</summary>
    public const int AdminCeiling = 100;

    /// <summary>Bulk execution stays deliberately slower than AI resolution:
    /// each item is a mutation against Listenarr, not a cached read.</summary>
    private const int ConfirmConcurrency = 2;

    public async Task<AudiobookSeriesPreviewResponse> PreviewAsync(
        string ownerKey, string seriesAsin, string? region, CancellationToken ct)
    {
        var resolvedRegion = availability.ResolveRegion(region);
        var booksTask = listenarr.GetSeriesBooksAsync(seriesAsin, resolvedRegion, ct);
        var contextTask = availability.LoadContextAsync(ct);
        await Task.WhenAll(booksTask, contextTask);

        var books = await booksTask;
        if (books.Count == 0)
            throw new AudiobookRequestValidationException("That series has no known books in the catalog.");

        var context = await contextTask;
        var members = books
            .OrderBy(book => SortKey(book, seriesAsin))
            .Select(book => Classify(book, context))
            .ToList();

        var requestable = members
            .Where(member => member.Classification == "requestable" && member.Asin is not null)
            .Select(member => member.Asin!)
            .ToList();

        var token = tokens.CreateSeries(ownerKey, seriesAsin, resolvedRegion, requestable);
        return new AudiobookSeriesPreviewResponse(
            token.Token,
            token.ExpiresAt,
            seriesAsin,
            SeriesName(books, seriesAsin),
            resolvedRegion,
            members.Count(member => member.Classification == "owned"),
            members.Count(member => member.Classification == "requested"),
            requestable.Count,
            members.Count(member => member.Classification is "unavailable" or "ambiguous"),
            RequestCeiling,
            requestable.Count > RequestCeiling,
            members);
    }

    public async Task<AudiobookSeriesConfirmResponse> ConfirmAsync(
        string ownerKey,
        string ownerLabel,
        bool isAdmin,
        string previewToken,
        IReadOnlyList<string> asins,
        bool confirmLarge,
        CancellationToken ct)
    {
        var preview = tokens.ConsumeSeries(ownerKey, previewToken)
            ?? throw new AudiobookRequestValidationException(
                "That series preview expired or belongs to another user. Preview the series again.");

        var allowed = preview.Asins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = asins
            .Select(asin => asin?.Trim().ToUpperInvariant())
            .Where(asin => !string.IsNullOrWhiteSpace(asin))
            .Select(asin => asin!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
            throw new AudiobookRequestValidationException("Select at least one book to request.");
        if (selected.Any(asin => !allowed.Contains(asin)))
            throw new AudiobookRequestValidationException(
                "That selection includes a book the preview did not offer. Preview the series again.");
        if (selected.Count > RequestCeiling && !isAdmin)
            throw new AudiobookRequestValidationException(
                $"A single confirmation may request at most {RequestCeiling} books. An administrator can confirm a larger batch.");
        if (selected.Count > RequestCeiling && !confirmLarge)
            throw new AudiobookRequestValidationException(
                $"This batch is larger than {RequestCeiling} books and needs a second confirmation.");
        if (selected.Count > AdminCeiling)
            throw new AudiobookRequestValidationException(
                $"A single confirmation may request at most {AdminCeiling} books.");

        var outcomes = new AudiobookSeriesRequestOutcome[selected.Count];
        using var gate = new SemaphoreSlim(ConfirmConcurrency, ConfirmConcurrency);
        await Task.WhenAll(selected.Select(async (asin, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                outcomes[index] = await RequestOneAsync(ownerKey, ownerLabel, asin, preview.Region, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        var response = new AudiobookSeriesConfirmResponse(
            preview.SeriesAsin,
            outcomes.Count(outcome => outcome.Outcome == "requested"),
            outcomes.Count(outcome => outcome.Outcome == "alreadyRequested"),
            outcomes.Count(outcome => outcome.Outcome == "failed"),
            outcomes);

        Log.Information(
            "[Listenarr] series {SeriesAsin} confirmed by user {UserId}: {Selected} selected, {Requested} added, {Existing} already present, {Failed} failed",
            preview.SeriesAsin, AudiobookRequestTokenStore.StableUserId(ownerKey)[..12],
            selected.Count, response.RequestedCount, response.AlreadyExistedCount, response.FailedCount);

        return response;
    }

    /// <summary>One book's failure is reported against that book only — the
    /// batch never rolls back work that already succeeded upstream.</summary>
    private async Task<AudiobookSeriesRequestOutcome> RequestOneAsync(
        string ownerKey, string ownerLabel, string asin, string region, CancellationToken ct)
    {
        try
        {
            // Series members are added review-only. Auto-search is a
            // single-book decision made against a specific edition's format
            // and stated preferences; nothing in a bulk confirmation proves
            // those for 25 different books at once.
            var result = await requests.AddRequestAsync(
                ownerKey, ownerLabel, asin, region, autoSearch: false, ct);
            return new AudiobookSeriesRequestOutcome(
                asin,
                result.Title,
                result.AlreadyExisted ? "alreadyRequested" : "requested",
                result.ListenarrId,
                null);
        }
        catch (AudiobookRequestValidationException ex)
        {
            return new AudiobookSeriesRequestOutcome(asin, asin, "failed", null, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Log.Warning("[Listenarr] series member {Asin} could not be requested: {Message}", asin, ex.Message);
            return new AudiobookSeriesRequestOutcome(
                asin, asin, "failed", null, "Listenarr could not add this book. Try it on its own.");
        }
    }

    private AudiobookSeriesMember Classify(
        ListenarrAudibleSearchResult book, AudiobookAvailabilityContext context)
    {
        var position = book.Series?.FirstOrDefault()?.Position;
        var title = book.Title?.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            return new AudiobookSeriesMember(
                "unavailable", position, book.Asin ?? "Unknown book", book.Asin, null,
                "The catalog has no usable title for this entry.");
        }

        if (string.IsNullOrWhiteSpace(book.Asin))
        {
            return new AudiobookSeriesMember(
                "ambiguous", position, title, null, null,
                "The catalog did not return one specific edition. Search for this book on its own.");
        }

        var edition = availability.Annotate(book, context);
        var classification = edition.Availability switch
        {
            "owned" => "owned",
            "requested" => "requested",
            _ => "requestable"
        };

        return new AudiobookSeriesMember(
            classification, position, title, book.Asin, edition, edition.AvailabilityReason);
    }

    /// <summary>Reading order where the catalog gives a numeric position, then
    /// release date, so a preview is never an arbitrary list.</summary>
    private static (double Position, string Date) SortKey(
        ListenarrAudibleSearchResult book, string seriesAsin)
    {
        var membership = book.Series?.FirstOrDefault(entry =>
            string.Equals(entry.Asin, seriesAsin, StringComparison.OrdinalIgnoreCase))
            ?? book.Series?.FirstOrDefault();
        var position = double.TryParse(membership?.Position, out var parsed) ? parsed : double.MaxValue;
        return (position, book.ReleaseDate ?? string.Empty);
    }

    private static string? SeriesName(
        IReadOnlyList<ListenarrAudibleSearchResult> books, string seriesAsin) => books
        .SelectMany(book => book.Series ?? [])
        .FirstOrDefault(entry => string.Equals(entry.Asin, seriesAsin, StringComparison.OrdinalIgnoreCase))
        ?.Name
        ?? books.SelectMany(book => book.Series ?? []).FirstOrDefault()?.Name;
}
