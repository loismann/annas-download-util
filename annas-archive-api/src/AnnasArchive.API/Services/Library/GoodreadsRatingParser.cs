using System.Globalization;
using System.Text.RegularExpressions;

namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Pulling a star rating out of Goodreads markup. Goodreads publishes the same
/// number three different ways depending on the page, so this tries all three.
///
/// Split out of <see cref="LibraryWatcherService"/> — pure string in, number
/// out, with no filesystem, HTTP or database anywhere near it.
/// </summary>
public static class GoodreadsRatingParser
{
    /// <summary>
    /// Matches "4.15 avg rating", with or without the space.
    ///
    /// NOTE the single backslash. This was `@"...\\s*avg rating"` — inside a
    /// verbatim string `\\` is two literal backslashes, so the engine read it as
    /// "an escaped backslash, then zero or more letter s". It could only ever
    /// match input containing an actual backslash, which Goodreads never emits.
    /// Both users of the pattern therefore always returned null.
    /// </summary>
    private static readonly Regex AvgRatingRx =
        new(@"(?<rating>[0-9.]+)\s*avg rating", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsonLdRatingRx =
        new("\"ratingValue\"\\s*:\\s*\"(?<rating>[0-9.]+)\"", RegexOptions.Compiled);

    private static readonly Regex ItemPropRatingRx =
        new("itemprop=\"ratingValue\"[^>]*>(?<rating>[0-9.]+)<", RegexOptions.Compiled);

    /// <summary>A full book page: JSON-LD first, then the microdata attribute,
    /// then the human-readable "avg rating" text.</summary>
    public static double? FromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        foreach (var rx in new[] { JsonLdRatingRx, ItemPropRatingRx, AvgRatingRx })
        {
            var match = rx.Match(html);
            if (match.Success && TryParse(match.Groups["rating"].Value, out var rating))
                return rating;
        }

        return null;
    }

    /// <summary>One search-result row's text, e.g. "4.15 avg rating — 2,347 ratings".</summary>
    public static double? FromSearchResultText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = AvgRatingRx.Match(text);
        return match.Success && TryParse(match.Groups["rating"].Value, out var rating) ? rating : null;
    }

    /// <summary>Invariant culture on purpose: Goodreads writes "4.15" regardless
    /// of where the server thinks it is, and a container set to a comma-decimal
    /// locale would otherwise parse it as 415.</summary>
    private static bool TryParse(string value, out double rating) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out rating);
}
