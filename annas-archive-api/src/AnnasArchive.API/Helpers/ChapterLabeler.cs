using System.Text;
using System.Text.RegularExpressions;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper class for labeling chapters in EPUB books.
/// Identifies main content chapters vs front/back matter and generates display labels.
/// </summary>
public static class ChapterLabeler
{
    private static readonly string[] FrontMatterKeywords =
    {
        "table of contents",
        "contents",
        "toc",
        "preface",
        "foreword",
        "introduction",
        "acknowledgments",
        "acknowledgements",
        "about the author",
        "author's note",
        "authors' note",
        "notes",
        "endnotes",
        "footnotes",
        "bibliography",
        "references",
        "index",
        "glossary",
        "appendix",
        "appendices",
        "maps",
        "map",
        "list of illustrations",
        "list of figures",
        "list of tables",
        "illustrations",
        "figures",
        "tables",
        "dedication",
        "copyright",
        "imprint",
        "credits",
        "colophon",
        "prologue",
        "epilogue",
        "afterword"
    };

    private static readonly string[] StructuralKeywords =
    {
        "part",
        "book",
        "section",
        "volume",
        "act",
        "interlude"
    };

    /// <summary>
    /// Labels a list of chapters, identifying main content vs front/back matter.
    /// </summary>
    public static List<LabeledChapter> LabelChapters(IReadOnlyList<FlatChapter> chapters)
    {
        var labeled = new List<LabeledChapter>(chapters.Count);
        var mainIndex = 0;
        var nonIndex = 0;

        foreach (var chapter in chapters)
        {
            var title = chapter.Title?.Trim() ?? string.Empty;
            var normalized = NormalizeTitle(title);
            var isFrontMatter = IsFrontMatterTitle(normalized) || IsLikelyTableOfContents(chapter.PlainText);
            var isStructural = IsStructuralHeading(normalized);
            var isExplicitChapter = HasChapterIndicator(title);
            var isMain = !isFrontMatter && (isExplicitChapter || (!isStructural && IsLikelyMainContent(chapter)));

            string displayLabel;
            if (isMain)
            {
                mainIndex++;
                displayLabel = BuildMainLabel(mainIndex, title);
            }
            else
            {
                nonIndex++;
                displayLabel = BuildNonMainLabel(nonIndex, title);
            }

            labeled.Add(new LabeledChapter(chapter, displayLabel, isMain));
        }

        return labeled;
    }

    /// <summary>
    /// Checks if a normalized title matches front matter keywords.
    /// </summary>
    public static bool IsFrontMatterTitle(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return FrontMatterKeywords.Any(keyword => normalizedTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a normalized title is a structural heading (part, book, section, etc.).
    /// </summary>
    public static bool IsStructuralHeading(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return StructuralKeywords.Any(keyword =>
            Regex.IsMatch(normalizedTitle, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Checks if a title has explicit chapter indicators (Chapter, numbers, roman numerals).
    /// </summary>
    public static bool HasChapterIndicator(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        if (Regex.IsMatch(title, @"\bchapter\b", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(title, @"^\s*\d+[\.\:\-\s]", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(title, @"^\s*[ivxlcdm]+[\.\:\-\s]", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Determines if a chapter is likely main content based on word count.
    /// </summary>
    public static bool IsLikelyMainContent(FlatChapter chapter)
    {
        if (chapter.WordCount >= 350)
            return true;

        return chapter.Level == 0 && chapter.WordCount >= 200;
    }

    /// <summary>
    /// Detects if content looks like a table of contents.
    /// </summary>
    public static bool IsLikelyTableOfContents(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        if (Regex.IsMatch(content, @"\b(table of contents|contents)\b", RegexOptions.IgnoreCase))
            return true;

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 2)
            .ToList();

        if (lines.Count < 5)
            return false;

        var dotLeaderLines = lines.Count(l => l.Contains("...") || Regex.IsMatch(l, @"\.{2,}\s*\d+$"));
        var chapterLines = lines.Count(l => Regex.IsMatch(l, @"\bchapter\b", RegexOptions.IgnoreCase));
        var numericLines = lines.Count(l => Regex.IsMatch(l, @"\b\d+\b"));
        var shortLines = lines.Count(l => l.Length <= 60);

        var score = 0;
        if (dotLeaderLines >= Math.Max(3, lines.Count / 4)) score++;
        if (chapterLines >= 3) score++;
        if (numericLines >= lines.Count / 2) score++;
        if (shortLines >= (int)(lines.Count * 0.7)) score++;

        return score >= 2;
    }

    /// <summary>
    /// Builds a display label for a main chapter.
    /// </summary>
    public static string BuildMainLabel(int chapterNumber, string title)
    {
        var cleaned = CleanChapterTitle(title);
        if (string.IsNullOrWhiteSpace(cleaned))
            return $"Chapter {chapterNumber}";

        return $"Chapter {chapterNumber}: {cleaned}";
    }

    /// <summary>
    /// Builds a display label for non-main content (front/back matter).
    /// </summary>
    public static string BuildNonMainLabel(int index, string title)
    {
        var cleaned = CleanNonChapterTitle(title);
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Front matter";

        return $"{ToRoman(index).ToLowerInvariant()}. {cleaned}";
    }

    /// <summary>
    /// Cleans a chapter title by removing chapter prefixes and numbers.
    /// </summary>
    public static string CleanChapterTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var cleaned = title.Trim();
        cleaned = Regex.Replace(cleaned, @"^(chapter|chap\.?)\s+\d+[\.\:\-\s]*", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"^(chapter|chap\.?)\s+[ivxlcdm]+[\.\:\-\s]*", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"^\d+[\.\:\-\s]+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"^[ivxlcdm]+[\.\:\-\s]+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"^(book|part|section)\s+[ivxlcdm\d]+[\.\:\-\s]*", "", RegexOptions.IgnoreCase).Trim();
        return cleaned;
    }

    /// <summary>
    /// Cleans a non-chapter title by normalizing whitespace.
    /// </summary>
    public static string CleanNonChapterTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        return Regex.Replace(title.Trim(), @"\s+", " ");
    }

    /// <summary>
    /// Normalizes a title to lowercase with single spaces.
    /// </summary>
    public static string NormalizeTitle(string title) =>
        Regex.Replace(title.Trim().ToLowerInvariant(), @"\s+", " ");

    /// <summary>
    /// Converts an integer to a Roman numeral string.
    /// </summary>
    public static string ToRoman(int number)
    {
        if (number <= 0)
            return "i";

        var map = new[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        };

        var result = new StringBuilder();
        var remaining = number;

        foreach (var (value, symbol) in map)
        {
            while (remaining >= value)
            {
                result.Append(symbol);
                remaining -= value;
            }
        }

        return result.ToString();
    }
}
