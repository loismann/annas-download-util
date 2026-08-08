using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// The section split used to be a GPT-4o call per 500 words, which is why none
/// of this was assertable — every case below needed a live OpenAI request and a
/// non-deterministic answer. The invariant worth guarding hardest is coverage:
/// a section boundary that skips words silently drops part of the book out of
/// every summary generated from it, and nothing downstream would notice.
/// </summary>
public class SectionChunkerTests
{
    private const string Break = "\n\n";

    // ─── Coverage ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(499)]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(2500)]
    [InlineData(5000)]
    public void EveryWordLandsInExactlyOneSection(int totalWords)
    {
        var text = Paragraphs(totalWords, paragraphWords: 80);

        var chunks = SectionChunker.Detect(text);

        AssertCoversExactly(chunks, WordCount(text));
    }

    [Fact]
    public void SectionsAreContiguousAndInOrder()
    {
        var chunks = SectionChunker.Detect(Paragraphs(3000, paragraphWords: 120));

        for (var i = 1; i < chunks.Count; i++)
        {
            chunks[i].Start.Should().Be(chunks[i - 1].End);
        }
    }

    [Fact]
    public void WordCountAlwaysMatchesTheSpanItDescribes()
    {
        var chunks = SectionChunker.Detect(Paragraphs(3000, paragraphWords: 77));

        chunks.Should().OnlyContain(c => c.WordCount == c.End - c.Start);
    }

    // ─── Where the breaks land ───────────────────────────────────────────

    [Fact]
    public void BreaksOnParagraphBoundaries()
    {
        // 100-word paragraphs: a section can only end on a multiple of 100 if
        // the split is respecting the breaks rather than counting to 500.
        var chunks = SectionChunker.Detect(Paragraphs(3000, paragraphWords: 100));

        chunks.Should().OnlyContain(c => c.End % 100 == 0);
    }

    [Fact]
    public void DoesNotSplitAParagraphThatStillFits()
    {
        // 450 + 140 = 590, under the 600 ceiling, so the second paragraph is
        // taken whole rather than cut at 500.
        var text = Words(450) + Break + Words(140);

        var chunks = SectionChunker.Detect(text);

        chunks.Should().ContainSingle();
        chunks[0].WordCount.Should().Be(590);
    }

    [Fact]
    public void ClosesEarlyRatherThanOverflowTheCeiling()
    {
        // 450 + 300 = 750, past the ceiling, so the seam goes on the break.
        var text = Words(450) + Break + Words(300);

        var chunks = SectionChunker.Detect(text);

        chunks.Should().HaveCount(2);
        chunks[0].WordCount.Should().Be(450);
        chunks[1].WordCount.Should().Be(300);
    }

    [Fact]
    public void NoSectionExceedsTheCeilingWhenParagraphsAreSmall()
    {
        var chunks = SectionChunker.Detect(Paragraphs(6000, paragraphWords: 60));

        chunks.Should().OnlyContain(c => c.WordCount <= SectionChunker.MaxWords);
    }

    // ─── The paragraph that is too big to break on ───────────────────────

    [Fact]
    public void CutsAParagraphThatIsLongerThanASectionOnItsOwn()
    {
        // A 1,300-word paragraph has no usable break inside it. Cutting on
        // count is the honest answer; dropping the words is not.
        var chunks = SectionChunker.Detect(Words(1300));

        AssertCoversExactly(chunks, 1300);
        chunks.Should().OnlyContain(c => c.WordCount <= SectionChunker.MaxWords);
    }

    [Fact]
    public void AChapterWithNoParagraphBreaksAtAllIsStillCovered()
    {
        var chunks = SectionChunker.Detect(Words(4321));

        AssertCoversExactly(chunks, 4321);
    }

    // ─── Degenerate input ────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n\n")]
    [InlineData("  \n \n  \t \n\n ")]
    public void EmptyOrBlankTextYieldsNoSections(string text)
    {
        SectionChunker.Detect(text).Should().BeEmpty();
    }

    [Fact]
    public void BlankParagraphsDoNotBecomeEmptySections()
    {
        var text = Words(200) + "\n\n   \n\n" + Words(200);

        var chunks = SectionChunker.Detect(text);

        chunks.Should().OnlyContain(c => c.WordCount > 0);
        AssertCoversExactly(chunks, 400);
    }

    [Fact]
    public void HandlesWindowsLineEndings()
    {
        var text = Words(300) + "\r\n\r\n" + Words(300);

        var chunks = SectionChunker.Detect(text);

        AssertCoversExactly(chunks, 600);
        chunks.Should().ContainSingle("300 + 300 fits under the 600 ceiling");
    }

    [Fact]
    public void RunsOfBlankLinesSeparateOneParagraph()
    {
        var text = Words(450) + "\n\n\n\n" + Words(300);

        var chunks = SectionChunker.Detect(text);

        chunks.Should().HaveCount(2);
        AssertCoversExactly(chunks, 750);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// The property that matters: the sections start at word 0, touch end to
    /// end, and finish on the last word of the chapter.
    /// </summary>
    private static void AssertCoversExactly(IReadOnlyList<ChunkBoundary> chunks, int totalWords)
    {
        if (totalWords == 0)
        {
            chunks.Should().BeEmpty();
            return;
        }

        chunks.Should().NotBeEmpty();
        chunks[0].Start.Should().Be(0);
        chunks[^1].End.Should().Be(totalWords);
        chunks.Sum(c => c.WordCount).Should().Be(totalWords);

        for (var i = 1; i < chunks.Count; i++)
        {
            chunks[i].Start.Should().Be(chunks[i - 1].End, "sections must not overlap or skip words");
        }
    }

    /// <summary>Splits the way the readers do, so the assertions are about the
    /// same word array the callers slice.</summary>
    private static int WordCount(string text) =>
        text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Words(int count) =>
        string.Join(" ", Enumerable.Range(0, count).Select(i => $"w{i}"));

    private static string Paragraphs(int totalWords, int paragraphWords)
    {
        var parts = new List<string>();
        for (var written = 0; written < totalWords; written += paragraphWords)
        {
            parts.Add(Words(Math.Min(paragraphWords, totalWords - written)));
        }

        return string.Join(Break, parts);
    }
}
