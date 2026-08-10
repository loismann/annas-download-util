using System.Text.RegularExpressions;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Story;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// What each lens's prompts actually say, as opposed to whether they changed.
///
/// <para><see cref="LensPromptGoldenTests"/> pins the wording, but a golden file
/// records whatever was written, including the wrong thing — re-record it and it
/// passes again. These tests assert the product decisions in §10 instead: the
/// headings each book type promises, in order, and the emphases that are the
/// reason the type exists at all. A rewrite that quietly drops "Decision points"
/// re-records cleanly and fails here.</para>
///
/// <para>Everything is matched against whitespace-flattened text. A prompt is a
/// wrapped raw string, so asserting on the literal would make re-wrapping a
/// paragraph a test failure — and a test that fails for reformatting is one that
/// gets loosened the first time it cries wolf.</para>
/// </summary>
public class LensSubstanceTests
{
    private static readonly IReadOnlyList<IReaderLens> Production =
        [new LiteraryLens(), new MilitaryLens(), new FictionLens()];

    public static TheoryData<string> AllLenses() => new() { "literary", "military", "fiction" };

    /// <summary>The two that accumulate a story model, and so have the extra prompt.</summary>
    public static TheoryData<string> StoryLenses() => new() { "military", "fiction" };

    private static IReaderLens Lens(string key) => Production.Single(l => l.Key == key);

    private static string Prompt(string key, CallKind kind) => Flat(Lens(key).Prompts[kind]!);

    /// <summary>Every run of whitespace becomes one space, so line wrapping is invisible.</summary>
    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>
    /// The bold headings of a chapter-summary prompt, in the order they appear,
    /// each paired with the instruction underneath it.
    /// </summary>
    private static (string Heading, string Body)[] Headings(string key) =>
        Regex.Matches(
                Lens(key).Prompts.ChapterSummary,
                @"\*\*(?<name>[^*]+)\*\*(?<body>.*?)(?=\n\n\*\*|\Z)",
                RegexOptions.Singleline)
            .Select(m => (m.Groups["name"].Value.Trim(), Flat(m.Groups["body"].Value)))
            .ToArray();

    private static (string Heading, string Body) Heading(string key, string name) =>
        Headings(key).Single(h => h.Heading == name);

    private static int Words(string flattened) =>
        flattened.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    // ─── the ladder every lens supplies whole ───────────────────────────

    /// <summary>
    /// The three tiers are one tuned ladder (§6): chunks written to be
    /// synthesised, sections to be summarised again, and only the last for a
    /// person. A lens that omits a length has left that rung to the model's mood,
    /// and the rung above it is then being fed something it was not tuned for.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLenses))]
    public void Every_lens_states_the_length_of_each_rung(string key)
    {
        Prompt(key, CallKind.ChunkSummary).Should().Contain("300-400 words");
        Prompt(key, CallKind.SectionSynthesis).Should().Contain("400-500 words");
        Prompt(key, CallKind.ChapterSummary).Should().Contain("700-900 words");
    }

    /// <summary>
    /// The lower two rungs are written for the next pass, not for a reader. Said
    /// plainly in each, because a chunk summary that starts addressing a reader is
    /// one that has started leaving out what the next pass needed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLenses))]
    public void The_lower_rungs_say_they_are_not_for_a_reader(string key)
    {
        Prompt(key, CallKind.ChunkSummary).Should().Contain("not for a reader");
        Prompt(key, CallKind.SectionSynthesis).Should().Contain("not a reader");
    }

    /// <summary>Every lens ends its passage analysis with the section readers came for.</summary>
    [Theory]
    [MemberData(nameof(AllLenses))]
    public void Every_lens_defines_its_hard_words_exhaustively(string key)
    {
        Prompt(key, CallKind.PassageAnalysis).Should()
            .Contain("**Definitions**")
            .And.Contain("exhaustive");
    }

    /// <summary>
    /// "I'm a Dummy" simplifies the telling and never the thing told. Each lens
    /// phrases it for its own subject, so this asserts the rule and not a
    /// sentence.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLenses))]
    public void Explain_simply_never_softens_what_it_explains(string key)
    {
        Prompt(key, CallKind.ExplainSimply).Should()
            .MatchRegex(@"Simplify the (explanation|telling), never");
    }

    // ─── military ───────────────────────────────────────────────────────

    [Fact]
    public void The_military_chapter_summary_has_the_eight_staff_headings_in_order()
    {
        Headings("military").Select(h => h.Heading).Should().Equal(
            "Situation", "Mission and intent", "Execution", "Decision points",
            "Command and human factors", "Doctrine", "Outcome and cost", "Lessons");
    }

    /// <summary>
    /// Decision points is the heart of the lens (§10.2), so it gets the most
    /// words. Asserted on the instruction's own length as well as on the sentence
    /// asking for it — a heading given one line cannot produce the longest
    /// section however firmly it is asked to.
    /// </summary>
    [Fact]
    public void Decision_points_is_given_more_than_any_other_military_heading()
    {
        var decisions = Heading("military", "Decision points");

        Words(decisions.Body).Should().BeGreaterThan(
            Headings("military").Where(h => h.Heading != "Decision points").Max(h => Words(h.Body)),
            "the heading the lens exists for cannot be the one given the least to work with");

        decisions.Body.Should().Contain("more words than any other");
    }

    /// <summary>
    /// The distinction the lens turns on: a decision is judged on what was known
    /// when it was taken. Without this the summary grades every choice by how it
    /// turned out, which is hindsight wearing analysis as a coat.
    /// </summary>
    [Fact]
    public void The_military_lens_judges_decisions_on_what_was_known_at_the_time()
    {
        Heading("military", "Decision points").Body.Should()
            .Contain("what they knew and did not know at that moment")
            .And.Contain("never on the outcome");

        Prompt("military", CallKind.PassageAnalysis).Should().Contain("what they knew");
    }

    [Fact]
    public void Military_definitions_cover_the_five_things_that_stop_a_civilian_reader()
    {
        Prompt("military", CallKind.PassageAnalysis).Should()
            .Contain("unit designations")
            .And.Contain("ranks and their equivalents across the armies")
            .And.Contain("equipment nomenclature")
            .And.Contain("staff abbreviations")
            .And.Contain("place names that carry strategic weight");
    }

    [Fact]
    public void The_military_lens_labels_its_story_model_for_a_campaign()
    {
        var lens = Lens("military");

        lens.BuildsStoryModel.Should().BeTrue();
        lens.StoryVocabulary.Should().Be(
            new StoryVocabulary("Commanders & Units", "Belligerents", "Operations"));
    }

    // ─── fiction ────────────────────────────────────────────────────────

    [Fact]
    public void The_fiction_chapter_summary_has_the_eight_headings_in_order()
    {
        Headings("fiction").Select(h => h.Heading).Should().Equal(
            "Where we are", "What happens", "Who appears", "Threads advanced",
            "Threads running in parallel", "Relationships and alliances",
            "Setups and payoffs", "Themes");
    }

    /// <summary>
    /// The one that earns the lens. A reader four hundred pages in has lost
    /// somebody who last appeared in chapter nine, and a cast list of only who is
    /// on the page is the summary they could have written themselves.
    /// </summary>
    [Fact]
    public void Who_appears_covers_characters_absent_for_many_chapters()
    {
        Heading("fiction", "Who appears").Body.Should()
            .Contain("absent for many chapters")
            .And.Contain("how long it has been");
    }

    /// <summary>
    /// Themes is the shortest heading here — the inverse of the literary lens,
    /// where it is the point. Checked by length as well as by instruction, for the
    /// same reason Decision points is.
    /// </summary>
    [Fact]
    public void Fiction_themes_are_kept_shorter_than_the_cast_and_the_plot()
    {
        var themes = Heading("fiction", "Themes");

        Words(themes.Body).Should().BeLessThan(Words(Heading("fiction", "Who appears").Body));
        themes.Body.Should().Contain("shortest heading");
    }

    /// <summary>
    /// Sections read by thread, not as one narrative (§10.3) — the fix for a
    /// chapter that cuts between Moscow and the front and comes back as one
    /// blurred paragraph.
    /// </summary>
    [Fact]
    public void Fiction_sections_are_organised_by_plot_thread()
    {
        Prompt("fiction", CallKind.SectionSummary).Should()
            .Contain("**by plot thread**")
            .And.Contain("never one blurred narrative");

        Prompt("fiction", CallKind.SectionSynthesis).Should().Contain("keep those strands separate");
    }

    /// <summary>
    /// A summary is read while the book is unfinished. Every fiction prompt whose
    /// output a reader sees has to say so; one that does not is a spoiler waiting
    /// for a chapter with a twist in it.
    /// </summary>
    [Fact]
    public void No_fiction_prompt_a_reader_reads_may_look_ahead()
    {
        Prompt("fiction", CallKind.ChapterSummary).Should().Contain("Do not reveal anything from later");
        Prompt("fiction", CallKind.SectionSummary).Should().Contain("do not give away anything after this section");
        Prompt("fiction", CallKind.PassageAnalysis).Should().Contain("Say nothing about what happens after this passage");
        Prompt("fiction", CallKind.ExplainSimply).Should().Contain("Do not tell the reader what happens next");
    }

    /// <summary>
    /// Threads running in parallel is the one heading fed by the story model
    /// rather than by the chapter in front of it, so it is the one that gets
    /// filled with invention when the model is empty.
    /// </summary>
    [Fact]
    public void Parallel_threads_come_from_the_material_or_are_left_empty()
    {
        Heading("fiction", "Threads running in parallel").Body.Should()
            .Contain("Use only what the material you were given establishes");
    }

    [Fact]
    public void The_fiction_lens_labels_its_story_model_for_a_novel()
    {
        var lens = Lens("fiction");

        lens.BuildsStoryModel.Should().BeTrue();
        lens.StoryVocabulary.Should().Be(
            new StoryVocabulary("Characters", "Factions", "Plot threads"));
    }

    // ─── both story-building lenses ─────────────────────────────────────

    /// <summary>
    /// The standing rule of §11.3, word for word in both. Extraction runs on the
    /// fast model over material that is already a summary; the failure that
    /// matters is it filling gaps, and this sentence is the whole defence.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoryLenses))]
    public void Story_extraction_may_not_go_beyond_the_summaries_it_is_given(string key)
    {
        Prompt(key, CallKind.StoryExtraction).Should()
            .Contain("only what appears in the provided summaries; infer nothing beyond them");
    }

    /// <summary>
    /// The model proposes and C# decides (§11.3). Silently fusing two characters
    /// is worse than showing two entries, so the prompt is forbidden from doing
    /// the merge itself — <c>StoryModelMerger</c> does that, and only for hints it
    /// finds unambiguous.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoryLenses))]
    public void Story_extraction_proposes_aliases_and_never_merges_them(string key)
    {
        Prompt(key, CallKind.StoryExtraction).Should()
            .Contain("aliasHints")
            .And.Contain("Do not merge it yourself")
            .And.Contain("do not remove anything");
    }

    /// <summary>
    /// Every part of the delta the merger will read is asked for by name.
    /// </summary>
    /// <remarks>
    /// Taken from <see cref="StoryDelta"/> itself rather than listed here, because
    /// a list written out is a fourth copy of the wire contract and would keep
    /// passing after a part was added that no prompt asks for. That failure is
    /// silent in production too: a prompt missing <c>"aliasHints"</c> does not
    /// error, it just stops proposing aliases, and the cast list grows duplicates.
    /// </remarks>
    [Theory]
    [MemberData(nameof(StoryLenses))]
    public void Story_extraction_asks_for_every_part_of_the_delta(string key)
    {
        var prompt = Prompt(key, CallKind.StoryExtraction);

        foreach (var part in DeltaParts)
            prompt.Should().Contain($"\"{part}\"", $"the merger reads {part}");
    }

    /// <summary>The delta's list properties, in the JSON spelling the parser uses.</summary>
    private static IEnumerable<string> DeltaParts =>
        typeof(StoryDelta).GetProperties()
            .Where(p => p.PropertyType.IsGenericType)
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]);

    /// <summary>Tiers go by part in the story, not by how often a name occurs.</summary>
    [Theory]
    [MemberData(nameof(StoryLenses))]
    public void Story_extraction_tiers_by_importance_rather_than_by_frequency(string key)
    {
        Prompt(key, CallKind.StoryExtraction).Should()
            .Contain("\"major\"").And.Contain("\"mentioned\"")
            .And.MatchRegex(@"not by how often (this chapter names them|the chapter names it)");
    }
}
