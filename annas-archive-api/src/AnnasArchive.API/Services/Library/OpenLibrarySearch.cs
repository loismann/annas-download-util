using System.Text.Json;

namespace AnnasArchive.API.Services.Library;

/// <summary>
/// The shared OpenLibrary title/author search: build the query, run it, parse
/// the response, and pick the best-scoring result.
///
/// LibraryWatcherService and AudiobookEnrichmentService each had their own copy
/// of this, and the copies had drifted in two ways that mattered:
///
/// <list type="bullet">
/// <item>LibraryWatcher built its client with <c>CreateClient()</c> — the
/// unnamed one — so it silently opted out of the retry, exponential backoff and
/// circuit breaker that <c>AddStandardResilience("OpenLibrary")</c> configures.
/// A transient 503 simply lost the metadata for that book.</item>
/// <item>LibraryWatcher re-implemented the confidence formula inline
/// (<c>title * 0.7 + author * 0.3</c>) instead of calling
/// <see cref="TitleMatchScorer.Confidence"/>, so the two services could have
/// started scoring the same book differently.</item>
/// </list>
///
/// What deliberately stays with the callers is the projection: one needs covers,
/// subjects, series and ISBNs, the other only title/author/year. Sharing the
/// fetch-and-score step while leaving the mapping alone is what keeps this
/// useful without inventing a lowest-common-denominator result type.
/// </summary>
public static class OpenLibrarySearch
{
    /// <param name="RequestSucceeded">
    /// False when the call failed or threw. Distinct from a null
    /// <paramref name="BestDoc"/>, which means the request was fine but matched
    /// nothing — callers that trip a rate limiter need to tell those apart.
    /// </param>
    /// <param name="BestDoc">
    /// The highest-scoring <c>docs</c> entry, cloned so it outlives the
    /// JsonDocument it was parsed from.
    /// </param>
    public readonly record struct Result(bool RequestSucceeded, JsonElement? BestDoc, double Confidence);

    /// <summary>
    /// Searches OpenLibrary and returns the best match by
    /// <see cref="TitleMatchScorer.Confidence"/>.
    /// </summary>
    /// <param name="http">
    /// Should be the named "OpenLibrary" client, which carries the resilience
    /// policy and the base address these relative URLs assume.
    /// </param>
    public static async Task<Result> FindBestMatchAsync(
        HttpClient http,
        string title,
        string[] authors,
        CancellationToken token,
        int limit = 10)
    {
        try
        {
            var author = authors.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            var url = string.IsNullOrWhiteSpace(author)
                ? $"search.json?title={Uri.EscapeDataString(title)}&limit={limit}"
                : $"search.json?title={Uri.EscapeDataString(title)}&author={Uri.EscapeDataString(author)}&limit={limit}";

            using var resp = await http.GetAsync(url, token);
            if (!resp.IsSuccessStatusCode)
                return new Result(RequestSucceeded: false, null, 0);

            using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);

            if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                return new Result(RequestSucceeded: true, null, 0);

            JsonElement? best = null;
            var bestConfidence = 0.0;

            foreach (var item in docs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var candidateTitle = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                var candidateAuthors = ExtractStringArray(item, "author_name");
                var confidence = TitleMatchScorer.Confidence(title, candidateTitle, authors, candidateAuthors);

                if (best is null || confidence > bestConfidence)
                {
                    // Clone: the JsonDocument backing this element is disposed
                    // when we leave this method, and an un-cloned JsonElement
                    // would take its buffer with it.
                    best = item.Clone();
                    bestConfidence = confidence;
                }
            }

            return new Result(RequestSucceeded: true, best, bestConfidence);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutdown, not a service failure — must not trip a rate limiter.
            throw;
        }
        catch
        {
            return new Result(RequestSucceeded: false, null, 0);
        }
    }

    /// <summary>
    /// Reads a string array property, dropping blanks and duplicates. Shared
    /// because OpenLibrary returns several fields in this shape (author_name,
    /// subject, subject_facet, series, isbn).
    /// </summary>
    public static string[] ExtractStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return prop.EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Reads an int property, or null when absent/not a number.</summary>
    public static int? ExtractInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : null;
}
