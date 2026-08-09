using System.Text.Json;
using System.Text.RegularExpressions;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Services.BookDiscovery;

/// <summary>The pieces of a related-books answer that are not books.</summary>
public sealed record RelatedBooksPayload(
    List<SeriesBook> SameSeries,
    List<AuthorSeries> OtherSeries,
    string? SeriesSummary,
    string? SeriesName,
    string? SeriesSearchQuery)
{
    public static RelatedBooksPayload Empty => new([], [], null, null, null);
}

/// <summary>What the model said about a book-search query.</summary>
/// <param name="IsBookQuery">False when the model judged the query not to be about books at all.</param>
/// <param name="Message">The model's explanation, only when <paramref name="IsBookQuery"/> is false.</param>
public sealed record BookSearchPayload(
    bool IsBookQuery,
    string? Message,
    string? Summary,
    List<AiBookSearchItem> Books);

/// <summary>
/// Turns model output into the response records.
///
/// Every method here treats malformed output as an empty result, never as an
/// exception. That is deliberate and it is the whole reason this is worth
/// separating: a language model asked for JSON returns *nearly* JSON often
/// enough that "throws on surprise" would mean an intermittent 500 on a feature
/// that could have degraded to "no suggestions" instead. These are pure
/// functions over a string, so the surprising shapes can be pinned by a test
/// rather than discovered in production.
/// </summary>
public static class BookDiscoveryResponses
{
    /// <summary>
    /// Parses <c>[{author, confidence}]</c>. Tolerates the model wrapping the
    /// array in prose, which it does despite being told not to — the array is
    /// extracted by regex before parsing.
    /// </summary>
    public static List<AuthorSuggestion> AuthorSuggestions(string? rawText, IAiResponseParser parser)
    {
        var authors = new List<AuthorSuggestion>();
        if (string.IsNullOrWhiteSpace(rawText)) return authors;

        try
        {
            var cleanedText = parser.StripCodeFences(rawText);
            var arrayMatch = Regex.Match(cleanedText, @"\[[\s\S]*\]");
            var jsonPayload = arrayMatch.Success ? arrayMatch.Value : cleanedText;

            using var doc = JsonDocument.Parse(jsonPayload);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return authors;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("author", out _) && item.TryGetProperty("confidence", out _))
                {
                    var confidence = RequiredString(item, "confidence");
                    authors.Add(new AuthorSuggestion(
                        RequiredString(item, "author"),
                        confidence.Length > 0 ? confidence : "low"));
                }
            }
        }
        catch (JsonException ex)
        {
            LogParseFailure("author suggestions", ex, rawText);
        }

        return authors;
    }

    public static RelatedBooksPayload RelatedBooks(string? rawText, IAiResponseParser parser)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return RelatedBooksPayload.Empty;

        try
        {
            using var doc = JsonDocument.Parse(parser.StripCodeFences(rawText));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return RelatedBooksPayload.Empty;

            return new RelatedBooksPayload(
                SameSeries: SeriesBooks(root, "sameSeries"),
                OtherSeries: OtherSeries(root),
                SeriesSummary: OptionalString(root, "seriesSummary"),
                SeriesName: OptionalString(root, "seriesName"),
                SeriesSearchQuery: OptionalString(root, "seriesSearchQuery"));
        }
        catch (JsonException ex)
        {
            LogParseFailure("related books", ex, rawText);
            return RelatedBooksPayload.Empty;
        }
    }

    public static BookSearchPayload? BookSearch(string? rawText, IAiResponseParser parser)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        var cleaned = parser.StripCodeFences(rawText);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(cleaned);
        }
        catch (Exception ex)
        {
            // Both previews are logged because the difference between them is
            // the only way to tell a model that answered badly from a fence
            // strip that ate part of a good answer.
            Log.Warning(ex, "❌ AI book-search JSON parse failed");
            Log.Warning("❌ AI book-search raw preview: {RawPreview}", Preview(rawText));
            Log.Warning("❌ AI book-search cleaned preview: {CleanPreview}", Preview(cleaned));
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var isBookQuery = root.TryGetProperty("isBookQuery", out var flag) && flag.ValueKind == JsonValueKind.True;

            return new BookSearchPayload(
                isBookQuery,
                OptionalString(root, "message"),
                OptionalString(root, "summary"),
                BookSearchItems(root));
        }
    }

    public static List<SeriesBookMatch> SeriesMatches(string? rawText, IAiResponseParser parser)
    {
        var matches = new List<SeriesBookMatch>();
        if (string.IsNullOrWhiteSpace(rawText)) return matches;

        try
        {
            using var doc = JsonDocument.Parse(parser.StripCodeFences(rawText));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return matches;
            if (!doc.RootElement.TryGetProperty("matches", out var array) || array.ValueKind != JsonValueKind.Array)
                return matches;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                matches.Add(new SeriesBookMatch(
                    RequiredString(item, "bookTitle"),
                    Order(item),
                    RequiredString(item, "status"),
                    OptionalString(item, "selectedMd5"),
                    OptionalString(item, "selectedTitle"),
                    RequiredString(item, "confidence"),
                    RequiredString(item, "reason")));
            }
        }
        catch (JsonException ex)
        {
            LogParseFailure("series match", ex, rawText);
        }

        return matches;
    }

    /// <summary>
    /// Parses the model's <c>{"groups":[[...]]}</c> into validated index groups,
    /// defensively covering every index 0..count-1 exactly once — any index the
    /// model omitted becomes its own singleton group, and any index it
    /// duplicated across groups is dropped on the later occurrence. A parsing
    /// hiccup therefore degrades to "no grouping" for the affected books rather
    /// than silently dropping them from the search results.
    /// </summary>
    public static List<List<int>> GroupIndices(string? rawText, int count, IAiResponseParser parser)
    {
        var groups = new List<List<int>>();
        var seen = new HashSet<int>();

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            try
            {
                using var doc = JsonDocument.Parse(parser.StripCodeFences(rawText));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("groups", out var groupsArray) &&
                    groupsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var groupEl in groupsArray.EnumerateArray())
                    {
                        if (groupEl.ValueKind != JsonValueKind.Array) continue;

                        var indices = new List<int>();
                        foreach (var idxEl in groupEl.EnumerateArray())
                        {
                            if (idxEl.ValueKind != JsonValueKind.Number) continue;
                            var idx = idxEl.GetInt32();
                            if (idx < 0 || idx >= count) continue;   // out-of-range, ignore
                            if (!seen.Add(idx)) continue;            // claimed by an earlier group
                            indices.Add(idx);
                        }

                        if (indices.Count > 0) groups.Add(indices);
                    }
                }
            }
            catch (JsonException ex)
            {
                LogParseFailure("group-search-results", ex, rawText);
            }
        }

        // Any index the model never mentioned (parse failure, omission, etc.)
        // still needs to show up — as its own singleton group.
        for (var i = 0; i < count; i++)
        {
            if (seen.Add(i)) groups.Add([i]);
        }

        return groups;
    }

    private static List<AiBookSearchItem> BookSearchItems(JsonElement root)
    {
        var books = new List<AiBookSearchItem>();
        if (!root.TryGetProperty("books", out var array) || array.ValueKind != JsonValueKind.Array)
            return books;

        // Deliberately no per-book cover or description lookup: the single
        // completion already produced a usable summary for every book, and
        // re-fetching from Google Books (quota-exhausted) and OpenLibrary
        // (currently down) one book at a time added seconds of dead-end HTTP
        // PER BOOK. Covers are fetched lazily by the frontend after the list
        // renders, the same as for ordinary search results.
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var title = RequiredString(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            books.Add(new AiBookSearchItem(
                title,
                RequiredString(item, "author"),
                RequiredString(item, "summary"),
                RequiredString(item, "importance"),
                null,
                "gpt"));
        }

        return books;
    }

    private static List<SeriesBook> SeriesBooks(JsonElement parent, string property)
    {
        var books = new List<SeriesBook>();
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return books;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("title", out _)) continue;

            books.Add(new SeriesBook(
                RequiredString(item, "title"),
                Order(item),
                RequiredString(item, "description"),
                null));   // CoverUrl is populated later
        }

        return books;
    }

    private static List<AuthorSeries> OtherSeries(JsonElement root)
    {
        var series = new List<AuthorSeries>();
        if (!root.TryGetProperty("otherSeries", out var array) || array.ValueKind != JsonValueKind.Array)
            return series;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("seriesName", out _)) continue;

            var books = SeriesBooks(item, "books");
            series.Add(new AuthorSeries(
                RequiredString(item, "seriesName"),
                item.TryGetProperty("bookCount", out var count) && count.ValueKind == JsonValueKind.Number
                    ? count.GetInt32()
                    : books.Count,
                books,
                RequiredString(item, "description"),
                RequiredString(item, "summary")));
        }

        return series;
    }

    /// <summary>Order is advisory — a model that answers "one" instead of 1
    /// should cost that book its position, not the whole series.</summary>
    private static int Order(JsonElement item) =>
        item.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Number
            ? order.GetInt32()
            : 0;

    /// <summary>
    /// The kind check is not redundant: <c>GetString</c> throws on a number or
    /// an object, and that throw is an <see cref="InvalidOperationException"/>,
    /// which slips past every <c>catch (JsonException)</c> around these parsers
    /// and turns one oddly-typed field into a 500 for the whole request.
    /// </summary>
    private static string RequiredString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? OptionalString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Preview(string text) =>
        text.Length > 2000 ? text[..2000] + "…" : text;

    private static void LogParseFailure(string what, JsonException ex, string rawText)
    {
        Log.Warning(ex, "⚠️ Failed to parse {What} JSON", what);
        Log.Information("Raw text: {RawText}", rawText);
    }
}
