using AnnasArchive.API.Services.Ai;

namespace AnnasArchive.Tests.Services.Ai;

/// <summary>
/// These four prompts were built inline inside an endpoint and a helper, so the
/// only way to reach any of them was a live OpenAI call. The one worth having a
/// test for most is the known-words exclusion: it is a product rule about
/// somebody's reading history, it has a branch, and it fails silently — a
/// summary that re-defines words the reader already marked as known looks
/// exactly like a summary that worked.
/// </summary>
public class ChapterSummaryPromptsTests
{
    // ─── Passage analysis: the known-words rule ──────────────────────────

    [Fact]
    public void TellsTheModelNotToDefineWordsTheReaderAlreadyKnows()
    {
        var call = PassageAnalysis(knownWords: ["palimpsest", "hermeneutic"]);

        call.SystemPrompt.Should().Contain("DO NOT define them");
        call.SystemPrompt.Should().Contain("palimpsest, hermeneutic");
    }

    [Fact]
    public void SaysNothingAboutExclusionsWhenTheReaderHasMarkedNoWords()
    {
        // Not cosmetic: an empty exclusion list reads to the model as "do not
        // define them: " with nothing after it, which is an instruction it can
        // act on in ways nobody intended.
        PassageAnalysis(knownWords: []).SystemPrompt.Should().NotContain("DO NOT define them");
        PassageAnalysis(knownWords: null).SystemPrompt.Should().NotContain("DO NOT define them");
    }

    [Fact]
    public void RaisesTheWordAllowanceWhicheverBranchIsTaken()
    {
        // The allowance is what makes the exclusion worth having — the model
        // spends the saved definitions elsewhere. Both branches carry it, and
        // the branch that forgets it is the easy mistake.
        PassageAnalysis(knownWords: ["palimpsest"]).SystemPrompt.Should().Contain("up to 600 words");
        PassageAnalysis(knownWords: null).SystemPrompt.Should().Contain("up to 600 words");
    }

    [Fact]
    public void SendsThePassageItWasAskedToAnalyse()
    {
        var call = PassageAnalysis(userPrompt: "Book context -> Title: Moby-Dick\n\nCall me Ishmael.");

        call.Input.Should().Contain("Call me Ishmael.");
        call.Input.Should().Contain("Title: Moby-Dick");
    }

    // ─── The three tiers ─────────────────────────────────────────────────

    [Fact]
    public void ChunkSummaryCarriesTheChapterContextAndTheChunk()
    {
        var call = ChapterSummaryPrompts.ChunkSummary(
            "gpt-4o", "Book: Moby-Dick | Chapter 1: Loomings", "Call me Ishmael.", 1000, "medium");

        call.Input.Should().Contain("Book: Moby-Dick | Chapter 1: Loomings");
        call.Input.Should().Contain("Call me Ishmael.");
    }

    [Fact]
    public void SectionSynthesisSeparatesTheSummariesItIsGiven()
    {
        // Without a separator the tier below reads several analyses as one run-on
        // passage, and the "trace the development" instruction has nothing to
        // trace between.
        var call = ChapterSummaryPrompts.SectionSynthesis(
            "gpt-4o", "Book: Moby-Dick", ["First analysis.", "Second analysis."], 1200, "medium");

        call.Input.Should().Contain("First analysis.\n\n---\n\nSecond analysis.");
    }

    [Fact]
    public void FinalSummarySeparatesSectionsAndJoinsContextWithPipes()
    {
        var call = ChapterSummaryPrompts.FinalSummary(
            "gpt-4o", ["Book: Moby-Dick", "Chapter 1: Loomings"], ["Section one.", "Section two."], 2500, "high");

        call.Input.Should().Contain("Book context: Book: Moby-Dick | Chapter 1: Loomings");
        call.Input.Should().Contain("Section one.\n\n---\n\nSection two.");
    }

    // ─── What the caller passes through untouched ────────────────────────

    [Fact]
    public void BudgetAndEffortReachTheCallUnchanged()
    {
        // These come from AI:MaxCompletionTokens:* and AI:ReasoningEffort:* per
        // tier, and they are the knobs that make a twenty-call summary
        // affordable. A prompt factory that quietly defaulted one would be
        // invisible until the bill arrived.
        var call = ChapterSummaryPrompts.ChunkSummary("gpt-4o", "ctx", "text", 1234, "low");

        call.MaxOutputTokens.Should().Be(1234);
        call.ReasoningEffort.Should().Be("low");
        call.Model.Should().Be("gpt-4o");
    }

    [Theory]
    [InlineData("summarize")]
    [InlineData("chunk-summary")]
    [InlineData("section-synthesis")]
    [InlineData("final-summary")]
    public void EndpointNamesAreStable(string expected)
    {
        // PerfLog groups timings by these, so renaming one silently splits a
        // metric's history rather than failing.
        var names = new[]
        {
            PassageAnalysis().Endpoint,
            ChapterSummaryPrompts.ChunkSummary("m", "c", "t", 1, null).Endpoint,
            ChapterSummaryPrompts.SectionSynthesis("m", "c", [], 1, null).Endpoint,
            ChapterSummaryPrompts.FinalSummary("m", [], [], 1, null).Endpoint
        };

        names.Should().Contain(expected);
    }

    [Fact]
    public void InstructionsGoInTheSystemPromptAndNeverInTheInput()
    {
        // The separation is the point: `Input` is the book, and a book is
        // arbitrary prose that can contain sentences shaped like instructions.
        // Concatenating the two — which these did — gives the model no way to
        // tell the standing instruction from the chapter quoting one.
        var calls = new[]
        {
            PassageAnalysis(userPrompt: "Some passage."),
            ChapterSummaryPrompts.ChunkSummary("m", "ctx", "Some passage.", 1, null),
            ChapterSummaryPrompts.SectionSynthesis("m", "ctx", ["Some passage."], 1, null),
            ChapterSummaryPrompts.FinalSummary("m", ["ctx"], ["Some passage."], 1, null)
        };

        calls.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.SystemPrompt));
        calls.Should().OnlyContain(c => c.Input.Contains("Some passage."));
    }

    [Fact]
    public void TheMaterialCarriesNoneOfTheInstructionText()
    {
        ChapterSummaryPrompts.ChunkSummary("m", "ctx", "text", 1, null)
            .Input.Should().NotContain("educational guide");
        ChapterSummaryPrompts.SectionSynthesis("m", "ctx", ["s"], 1, null)
            .Input.Should().NotContain("synthesizing multiple passage analyses");
        ChapterSummaryPrompts.FinalSummary("m", ["c"], ["s"], 1, null)
            .Input.Should().NotContain("700-900 word educational summary");
        PassageAnalysis().Input.Should().NotContain("literary analysis assistant");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static AiResponsesCall PassageAnalysis(
        string userPrompt = "Book context -> (not provided)\n\nSome passage.",
        IReadOnlyCollection<string>? knownWords = null) =>
        ChapterSummaryPrompts.PassageAnalysis(
            model: "gpt-4o",
            userPrompt: userPrompt,
            knownWords: knownWords,
            maxOutputTokens: 1000,
            reasoningEffort: "none",
            temperature: 0.3);
}
