using System.Net;
using System.Text.RegularExpressions;

namespace AnnasArchive.API.Reader2.Epub;

/// <summary>
/// Turns one XHTML chapter into plain text with its paragraph breaks intact.
///
/// <para>Paragraph structure is not cosmetic here: <see cref="SectionChunker"/>
/// splits on blank lines, so losing them would leave every chapter a single
/// undifferentiated block and force section boundaries into the middle of
/// sentences.</para>
///
/// <para>Regex rather than an XML parser, because chapter files are HTML in
/// practice — unclosed <c>&lt;br&gt;</c>, stray <c>&amp;nbsp;</c>, mismatched
/// tags — and a strict parser throws on books that read fine.</para>
/// </summary>
public static partial class EpubTextExtractor
{
    /// <summary>Content that is markup rather than prose, removed whole.</summary>
    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NonProse();

    /// <summary>Tags that end a paragraph — everything else is inline.</summary>
    [GeneratedRegex(@"</?\s*(p|div|br|hr|h[1-6]|li|tr|blockquote|section|article|figure|figcaption|table|pre)\b[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex BlockBoundary();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    /// <summary>Three or more newlines collapse to a single blank line.</summary>
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex ExcessSpaces();

    public static string ToPlainText(string xhtml)
    {
        if (string.IsNullOrWhiteSpace(xhtml)) return "";

        var text = Comment().Replace(xhtml, "");
        text = NonProse().Replace(text, "");

        // Mark block boundaries before stripping tags, or the paragraph structure
        // vanishes with them.
        text = BlockBoundary().Replace(text, "\n\n");
        text = AnyTag().Replace(text, "");

        text = WebUtility.HtmlDecode(text);

        // Written as escapes, not literals: a bare U+2028 in a source file is a
        // line break to the C# lexer itself, which is a memorable way to break the
        // build. U+00A0 reads as a space to a person and must split words for the
        // chunker, but char.IsWhiteSpace does not treat it as whitespace.
        text = text
            .Replace('\u00A0', ' ')     // non-breaking space
            .Replace('\u2028', '\n')    // line separator
            .Replace('\u2029', '\n')    // paragraph separator
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        var lines = text.Split('\n').Select(l => ExcessSpaces().Replace(l, " ").Trim());
        text = string.Join('\n', lines);

        return ExcessBlankLines().Replace(text, "\n\n").Trim();
    }

    /// <summary>
    /// Words in a piece of extracted text — whitespace-separated, which is the
    /// definition the whole pipeline uses. One definition, shared by the index,
    /// the chunker, and search, so all three agree on how long a chapter is and
    /// a word offset means the same thing everywhere.
    /// </summary>
    public static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// A short label for a chapter with no TOC title, taken from its opening
    /// words — better than "Chapter 7" when the TOC is missing entirely.
    /// </summary>
    public static string? FirstLineTitle(string text, int maxLength = 60)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.Trim().Length > 0)?.Trim();

        if (string.IsNullOrEmpty(line)) return null;
        if (line.Length <= maxLength) return line;

        // Prefer to cut at a word break, but not so early that the label says
        // nothing — a one-word title is worse than a truncated sentence.
        var wordBreak = line.LastIndexOf(' ', maxLength);
        var cut = wordBreak > maxLength / 3 ? wordBreak : maxLength;

        return line[..cut] + '…';
    }
}
