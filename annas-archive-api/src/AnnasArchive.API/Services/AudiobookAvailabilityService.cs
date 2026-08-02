using System.Text.Json.Nodes;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Library;

namespace AnnasArchive.API.Services;

/// <summary>
/// Cross-references Audible editions from Listenarr against both acquisition
/// state and the playable Audiobookshelf catalog. Matching is deliberately
/// conservative: an uncertain title-only match remains available instead of
/// falsely claiming that a specific narrator edition is already owned.
/// </summary>
public sealed class AudiobookAvailabilityService(
    IListenarrService listenarr,
    IAudiobookshelfService audiobookshelf,
    AudiobookRequestStore requests,
    IConfiguration configuration)
{
    private const int MaxResults = 25;

    /// <summary>Resolves the effective Audible region once, so catalog search
    /// and AI discovery cannot drift onto different defaults.</summary>
    public string ResolveRegion(string? region) => string.IsNullOrWhiteSpace(region)
        ? configuration["Listenarr:DefaultRegion"] ?? "us"
        : region.Trim().ToLowerInvariant();

    /// <summary>Loads the two cross-reference sets once. AI discovery resolves
    /// many candidates against the same snapshot instead of re-reading the
    /// Listenarr library and the Audiobookshelf catalog per candidate.</summary>
    public async Task<AudiobookAvailabilityContext> LoadContextAsync(CancellationToken ct = default)
    {
        var requestedTask = listenarr.GetLibraryAsync(ct);
        var ownedTask = audiobookshelf.GetLibraryItemsAsync(ct);
        await Task.WhenAll(requestedTask, ownedTask);
        return new AudiobookAvailabilityContext(await requestedTask, await ownedTask);
    }

    public async Task<AudiobookSearchResponse> SearchAsync(
        string query,
        string? region,
        string? language,
        CancellationToken ct = default)
    {
        var resolvedRegion = ResolveRegion(region);

        var searchTask = listenarr.SearchAudibleAsync(query, resolvedRegion, language, ct);
        var contextTask = LoadContextAsync(ct);

        await Task.WhenAll(searchTask, contextTask);

        var search = await searchTask;
        var context = await contextTask;
        var results = (search.Results ?? [])
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Asin) && !string.IsNullOrWhiteSpace(candidate.Title))
            .Take(MaxResults)
            .Select(candidate => Annotate(candidate, context))
            .ToList();

        return new AudiobookSearchResponse(
            query,
            resolvedRegion,
            string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
            search.TotalResults ?? results.Count,
            results);
    }

    /// <summary>Turns one Audible edition into an app-facing card annotated
    /// with ownership and acquisition state.</summary>
    public AudiobookSearchResult Annotate(
        ListenarrAudibleSearchResult candidate,
        AudiobookAvailabilityContext context)
    {
        var requested = context.Requested;
        var owned = context.Owned;

        var authors = candidate.Authors?.Select(author => author.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToArray() ?? [];
        var narrators = candidate.Narrators?.Select(narrator => narrator.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToArray() ?? [];

        var ownedMatch = FindOwned(candidate, authors, narrators, owned);
        var requestedMatch = requested.FirstOrDefault(item =>
            string.Equals(item.Asin, candidate.Asin, StringComparison.OrdinalIgnoreCase));

        var availability = ownedMatch is not null ? "owned"
            : requestedMatch is not null ? "requested"
            : "available";
        var reason = ownedMatch?.Reason
            ?? (requestedMatch is not null ? "This exact Audible edition is already in Listenarr." : null);

        var tracked = requests.GetByAsin(candidate.Asin!) is not null;
        return new AudiobookSearchResult(
            candidate.Asin!,
            candidate.Title!,
            candidate.Subtitle,
            authors,
            narrators,
            candidate.Publisher,
            candidate.ReleaseDate,
            candidate.Language,
            candidate.BookFormat,
            candidate.RuntimeLengthMin ?? candidate.LengthMinutes ?? candidate.RuntimeMinutes,
            candidate.ImageUrl,
            candidate.Genres?.Select(genre => genre.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToArray() ?? [],
            candidate.Series?.Select(series => new AudiobookSeriesMembership(series.Asin, series.Name, series.Position)).ToArray() ?? [],
            availability,
            reason,
            ownedMatch?.ItemId,
            requestedMatch?.Id,
            tracked);
    }

    private static OwnedMatch? FindOwned(
        ListenarrAudibleSearchResult candidate,
        string[] candidateAuthors,
        string[] candidateNarrators,
        JsonArray owned)
    {
        foreach (var node in owned)
        {
            if (node is not JsonObject item) continue;
            var metadata = item["media"]?["metadata"] as JsonObject;
            if (metadata is null) continue;

            var itemId = item["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(itemId)) continue;

            var asin = FirstString(metadata, "asin", "audibleAsin")
                ?? FirstString(item, "asin", "audibleAsin");
            if (!string.IsNullOrWhiteSpace(asin) &&
                string.Equals(asin, candidate.Asin, StringComparison.OrdinalIgnoreCase))
            {
                return new OwnedMatch(itemId, "Audiobookshelf has this exact Audible ASIN.");
            }

            var isbn = FirstString(metadata, "isbn") ?? FirstString(item, "isbn");
            if (!string.IsNullOrWhiteSpace(candidate.Isbn) && !string.IsNullOrWhiteSpace(isbn) &&
                NormalizeIdentifier(candidate.Isbn) == NormalizeIdentifier(isbn))
            {
                return new OwnedMatch(itemId, "Audiobookshelf has this exact ISBN edition.");
            }

            var title = metadata["title"]?.ToString();
            var author = metadata["authorName"]?.ToString();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author)) continue;

            var titleScore = TitleMatchScorer.TokenSimilarity(candidate.Title, title);
            var authorScore = TitleMatchScorer.CandidateAuthorScore(candidateAuthors, SplitNames(author));
            if (titleScore < 0.98 || authorScore < 0.80) continue;

            var narrator = metadata["narratorName"]?.ToString();
            if (!string.IsNullOrWhiteSpace(narrator) && candidateNarrators.Length > 0)
            {
                var narratorScore = TitleMatchScorer.CandidateAuthorScore(candidateNarrators, SplitNames(narrator));
                if (narratorScore < 0.75) continue;
                return new OwnedMatch(itemId, "Audiobookshelf has the same title, author, and narrator edition.");
            }

            return new OwnedMatch(itemId, "Audiobookshelf has an exact title and author match; narrator metadata is unavailable.");
        }

        return null;
    }

    private static string? FirstString(JsonObject item, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = item[key]?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string NormalizeIdentifier(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string[] SplitNames(string value) => value
        .Split([',', ';', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record OwnedMatch(string ItemId, string Reason);
}

/// <summary>One snapshot of everything already requested in Listenarr and
/// everything playable in Audiobookshelf.</summary>
public sealed record AudiobookAvailabilityContext(
    IReadOnlyList<ListenarrLibraryItem> Requested,
    JsonArray Owned);
