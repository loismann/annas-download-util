using System.Text.RegularExpressions;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Splits a chapter into readable sections at paragraph boundaries.
///
/// This replaced a GPT-4o call made once per 500 words of every chapter, whose
/// entire job was to answer "which paragraph break is nearest the 500-word
/// mark". Two things made that indefensible rather than merely expensive:
///
/// <list type="number">
/// <item>The model was shown text with no paragraph breaks in it. The caller
/// split the chapter on <c>' '</c>, <c>'\n'</c>, <c>'\r'</c> and <c>'\t'</c>
/// and re-joined the window with single spaces, so every boundary the prompt
/// asked it to find had been destroyed one line earlier. It was answering an
/// impossible question, and its answer was then clamped into [400, len].</item>
/// <item>The non-AI fallback beneath it was inert for the same reason — it
/// searched the already-flattened words for <c>"\n\n"</c> and never found one,
/// so it could not have covered for the model either.</item>
/// </list>
///
/// The breaks are really there: <c>EpubChapterCache.HtmlToPlainText</c> emits
/// <c>\n\n</c> for every block element and collapses longer runs to exactly
/// that, so a chapter file on disk is paragraph-separated by construction. The
/// work is reading it before flattening it.
/// </summary>
public static class SectionChunker
{
    /// <summary>Words a section aims for. A section is closed as soon as it
    /// reaches this at a paragraph break.</summary>
    public const int TargetWords = 500;

    /// <summary>Words a section may not exceed by absorbing one more paragraph.
    /// A single paragraph longer than this is cut on count, because there is no
    /// boundary inside it to prefer.</summary>
    public const int MaxWords = 600;

    /// <summary>
    /// The same delimiters the readers use to index into a chapter. Boundaries
    /// are word offsets into <c>text.Split(WordSeparators, RemoveEmptyEntries)</c>,
    /// and callers slice that array with them — so this set has to match, and
    /// paragraph counts have to sum to the whole.
    /// </summary>
    private static readonly char[] WordSeparators = [' ', '\n', '\r', '\t'];

    /// <summary>One or more blank lines, which is what separates paragraphs.</summary>
    private static readonly Regex ParagraphBreak = new(@"\r?\n[ \t]*\r?\n", RegexOptions.Compiled);

    /// <summary>
    /// Sections covering the chapter end to end, in order, with no gaps — every
    /// word lands in exactly one section. Empty text yields no sections.
    /// </summary>
    public static List<ChunkBoundary> Detect(
        string chapterText,
        int targetWords = TargetWords,
        int maxWords = MaxWords)
    {
        var chunks = new List<ChunkBoundary>();
        var start = 0;
        var length = 0;

        void Close()
        {
            if (length == 0) return;
            chunks.Add(new ChunkBoundary(start, start + length, length));
            start += length;
            length = 0;
        }

        foreach (var paragraph in ParagraphWordCounts(chapterText))
        {
            var remaining = paragraph;

            // Would overflow the open section: close it first, so the seam lands
            // on this paragraph's break rather than inside it.
            if (length > 0 && length + remaining > maxWords) Close();

            // Too long to be a section even on its own. Nothing inside it is
            // worth preferring, so it is cut on count.
            while (remaining > maxWords)
            {
                length = targetWords;
                remaining -= targetWords;
                Close();
            }

            length += remaining;
            if (length >= targetWords) Close();
        }

        Close();
        return chunks;
    }

    /// <summary>
    /// Words per paragraph. Paragraphs that are entirely whitespace contribute
    /// nothing and are skipped rather than emitted as zero-length sections.
    /// </summary>
    private static IEnumerable<int> ParagraphWordCounts(string chapterText)
    {
        if (string.IsNullOrWhiteSpace(chapterText)) yield break;

        foreach (var paragraph in ParagraphBreak.Split(chapterText))
        {
            var words = paragraph.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > 0) yield return words;
        }
    }
}
