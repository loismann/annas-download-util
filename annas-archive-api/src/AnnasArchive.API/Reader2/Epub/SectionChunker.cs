namespace AnnasArchive.API.Reader2.Epub;

/// <summary>A slice of a chapter, in words.</summary>
/// <param name="Start">Index of the first word, inclusive.</param>
/// <param name="WordCount">How many words. <c>Start + WordCount</c> is the end, exclusive.</param>
public sealed record SectionBoundary(int Start, int WordCount)
{
    public int End => Start + WordCount;
}

/// <summary>
/// Splits a chapter into sections a reader can summarise one at a time.
///
/// <para>Pure arithmetic over paragraph breaks — no model call, no spend, and
/// identical for every book type, which is why the result is stored once with
/// <c>lens_key = 'none'</c> and survives a lens switch untouched.</para>
///
/// <para>Sections end on a paragraph boundary rather than a word count, because
/// a section summary that begins mid-sentence reads as a mistake even when the
/// summary itself is good.</para>
/// </summary>
public static class SectionChunker
{
    public const int DefaultTargetWords = 2000;

    /// <summary>
    /// Sections covering the whole chapter, in order, with no gaps or overlaps.
    /// Empty text yields no sections; text shorter than one target yields one.
    /// </summary>
    /// <param name="targetWords">
    /// Preferred size. A section runs slightly over rather than splitting a
    /// paragraph, so this is a target and not a maximum.
    /// </param>
    public static IReadOnlyList<SectionBoundary> Detect(string text, int targetWords = DefaultTargetWords)
    {
        if (targetWords <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWords), targetWords, "Must be positive.");

        var totalWords = EpubTextExtractor.CountWords(text);
        if (totalWords == 0) return [];

        var breaks = ParagraphWordOffsets(text, totalWords);
        var sections = new List<SectionBoundary>();
        var start = 0;

        while (start < totalWords)
        {
            var ideal = start + targetWords;
            if (ideal >= totalWords)
            {
                // Everything left fits in one section, so take it whole rather
                // than splitting again and leaving a stub at the end that nobody
                // wants a summary of.
                sections.Add(new SectionBoundary(start, totalWords - start));
                break;
            }

            var boundary = NearestBreakAfter(breaks, ideal, start);
            sections.Add(new SectionBoundary(start, boundary - start));
            start = boundary;
        }

        return sections;
    }

    /// <summary>
    /// Word index at which each paragraph starts, plus the end of the text —
    /// the only positions a section is allowed to begin or end at.
    /// </summary>
    private static List<int> ParagraphWordOffsets(string text, int totalWords)
    {
        var offsets = new List<int> { 0 };
        var wordsSoFar = 0;

        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            wordsSoFar += EpubTextExtractor.CountWords(paragraph);
            if (wordsSoFar > 0 && wordsSoFar < totalWords) offsets.Add(wordsSoFar);
        }

        offsets.Add(totalWords);
        return offsets;
    }

    /// <summary>
    /// The paragraph break closest to <paramref name="ideal"/>, in either
    /// direction, that still moves past <paramref name="start"/>.
    /// </summary>
    private static int NearestBreakAfter(List<int> breaks, int ideal, int start)
    {
        var best = -1;
        var bestDistance = int.MaxValue;

        foreach (var offset in breaks)
        {
            if (offset <= start) continue;

            var distance = Math.Abs(offset - ideal);
            if (distance >= bestDistance) continue;

            best = offset;
            bestDistance = distance;
        }

        // A single paragraph longer than the target has no break to land on, so
        // the section simply runs long. Splitting it would be the worse answer.
        return best > start ? best : breaks[^1];
    }
}
