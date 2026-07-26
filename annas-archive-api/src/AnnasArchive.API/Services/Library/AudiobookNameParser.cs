using System.Text.RegularExpressions;

namespace AnnasArchive.API.Services.Library;

/// <summary>A best-effort guess at what a messy audiobook folder name is
/// actually describing — every field can be null. This is only a starting
/// hint for the metadata-provider search queries in AudiobookEnrichmentService,
/// not a reliable identification on its own (that's the whole reason a real
/// database lookup is needed instead of just cleaning the string).</summary>
public sealed record AudiobookCandidate(string? Title, string? Author, string? Narrator, int? Year);

/// <summary>
/// Strips known noise patterns out of messy audiobook folder/file names to
/// produce a search-query-ready title/author guess. Deliberately conservative
/// — prefers under-stripping over over-stripping, since a slightly noisy
/// candidate still gets Jaccard-scored against real provider results
/// downstream, while an over-aggressively-stripped title can silently lose
/// a real search term.
/// </summary>
public static class AudiobookNameParser
{
    private static readonly string[] AudioExtensions = { ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wav", ".aac", ".wma" };

    private static readonly Regex NoisePrefixRegex = new(
        @"^(audiobooks?_temp_input_|audiobooks_|_?temp_|_?input_)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeparatorRegex = new(@"[_\.\-]+", RegexOptions.Compiled);

    private static readonly Regex CamelCaseSplitRegex = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    private static readonly Regex ReadByRegex = new(
        @"\b(?:read|narrated)\s*by\s+([A-Z][a-zA-Z'\.]+(?:\s+[A-Z][a-zA-Z'\.]+){0,3})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AbridgedRegex = new(
        @"\(?\[?\s*(un)?abridged\s*\)?\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VolumePartRegex = new(
        @"\b(cd|disc|part|vol(?:ume)?|book)\.?\s*\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearRegex = new(@"\b(1[5-9]\d{2}|20\d{2})\b", RegexOptions.Compiled);

    private static readonly Regex NamePatternRegex = new(
        @"^([A-Z][a-zA-Z'\.]+(?:\s+[A-Z][a-zA-Z'\.]+){1,2})\b", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static AudiobookCandidate ParseCandidate(string folderName)
    {
        var working = folderName;

        // Strip a trailing audio extension if this was called on a file name
        // rather than a folder name (single-file books use the file name).
        foreach (var ext in AudioExtensions)
        {
            if (working.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                working = working[..^ext.Length];
                break;
            }
        }

        working = NoisePrefixRegex.Replace(working, "");
        working = SeparatorRegex.Replace(working, " ");
        working = CamelCaseSplitRegex.Replace(working, " ");
        working = WhitespaceRegex.Replace(working, " ").Trim();

        // Narrator hint — explicitly NOT treated as the author. Messy folder
        // names routinely conflate "ReadBy AntonLesser" with the book itself;
        // Anton Lesser is who's reading Charles Dickens, not who wrote it.
        string? narrator = null;
        var readByMatch = ReadByRegex.Match(working);
        if (readByMatch.Success)
        {
            narrator = readByMatch.Groups[1].Value.Trim();
            working = working.Remove(readByMatch.Index, readByMatch.Length);
        }

        int? year = null;
        var yearMatch = YearRegex.Match(working);
        if (yearMatch.Success)
        {
            year = int.Parse(yearMatch.Value);
            working = working.Remove(yearMatch.Index, yearMatch.Length);
        }

        working = AbridgedRegex.Replace(working, " ");
        working = VolumePartRegex.Replace(working, " ");
        working = WhitespaceRegex.Replace(working, " ").Trim();

        if (string.IsNullOrWhiteSpace(working))
            return new AudiobookCandidate(null, null, narrator, year);

        // Best-effort author guess: a capitalized 2-3 word name at the start
        // of what's left (e.g. "Charles Dickens A Tale of Two Cities" after
        // stripping). If nothing matches, leave Author null and let the
        // search step fall back to a title-only query — safer than guessing
        // wrong and poisoning the search with an incorrect author filter.
        string? author = null;
        var nameMatch = NamePatternRegex.Match(working);
        var title = working;
        if (nameMatch.Success)
        {
            author = nameMatch.Groups[1].Value.Trim();
            title = working[nameMatch.Length..].Trim();
            if (title.Length == 0)
            {
                // The whole string was just a name — more likely this is a
                // title-less folder (e.g. an author-only container) than a
                // book with no title at all. Don't fabricate an empty title.
                title = author;
                author = null;
            }
        }

        return new AudiobookCandidate(
            string.IsNullOrWhiteSpace(title) ? null : title,
            string.IsNullOrWhiteSpace(author) ? null : author,
            narrator,
            year);
    }
}
