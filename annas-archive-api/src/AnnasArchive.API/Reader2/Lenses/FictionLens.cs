namespace AnnasArchive.API.Reader2.Lenses;

/// <summary>
/// Long, character-heavy, multi-plot novels - the type Reader I fails hardest at.
///
/// <para>The failure it is written against is specific. A chapter of a big novel
/// cuts between three places, and a summary that reads them as one continuous
/// narrative produces a paragraph in which nobody can tell which thread moved.
/// So sections are organised <i>by plot thread</i>, and the chapter summary
/// opens by saying where this thread was left rather than assuming the reader
/// remembers.</para>
///
/// <para>Themes are deliberately the shortest heading here - the exact inverse of
/// the literary lens. A reader four hundred pages into a novel has lost track of
/// who somebody is, not of what the book is about.</para>
/// </summary>
public sealed class FictionLens : IReaderLens
{
    public string Key => "fiction";
    public string DisplayName => "Stories";
    public string Description =>
        "Novels with large casts and many plot threads. Tracks who is who, what "
        + "moved, and what was set up.";
    public string Icon => "auto_stories";

    public int SortOrder => 2;

    /// <summary>
    /// Bump on any edit below. The golden tests refuse the edit otherwise, which
    /// is what stops artifacts outliving the wording that produced them.
    /// </summary>
    public int PromptVersion => 1;

    public bool BuildsStoryModel => true;

    public StoryVocabulary? StoryVocabulary =>
        new(Actors: "Characters", Groups: "Factions", Threads: "Plot threads");

    public LensPrompts Prompts { get; } = new(
        PassageAnalysis: PassageAnalysisPrompt,
        ChunkSummary: ChunkSummaryPrompt,
        SectionSynthesis: SectionSynthesisPrompt,
        ChapterSummary: ChapterSummaryPrompt,
        SectionSummary: SectionSummaryPrompt,
        ExplainSimply: ExplainSimplyPrompt,
        StoryExtraction: StoryExtractionPrompt);

    // ─── the ladder ──────────────────────────────────────────────────────

    private const string ChunkSummaryPrompt = """
        You are reading one part of a longer chapter of a novel. Another pass will
        combine your output with the rest, so write for that pass and not for a
        reader.

        Record, in 300-400 words:
        - every character who appears or is spoken of, by the name the text uses,
          including any other name, title, or diminutive they are given here
        - where this takes place, and when relative to what came before
        - what happens, in the order it happens
        - what changes between people: alliances, estrangements, promises, debts
        - anything planted that is plainly meant to matter later, and anything paid
          off that was planted earlier
        - the point at which this part hands over to the next

        Prose, not bullet points. Keep the names as the text gives them, including
        forms you suspect belong to somebody already named - the later pass resolves
        those, and cannot recover a name you dropped. Do not interpret, and do not
        say what a scene means.
        """;

    private const string SectionSynthesisPrompt = """
        You are given several consecutive notes covering one stretch of a chapter of a
        novel. Combine them into one account of 400-500 words that a final pass will
        summarise again.

        Where the notes follow more than one set of characters, keep those strands
        separate rather than interleaving them - say whose strand each part is. Keep
        every character named, in every form of their name that appeared. Keep what
        was planted and what was paid off. Where two notes cover the same scene from
        different sides, state it once.

        Prose, no headings, no bullet points. Write for a further pass, not a reader.
        """;

    private const string ChapterSummaryPrompt = """
        You are writing for somebody reading a long novel with a large cast, who put
        it down a fortnight ago and has lost the thread.

        Write 700-900 words under exactly these eight headings:

        **Where we are** - which thread this chapter belongs to, and where it was left.
        Reorient the reader before anything else.

        **What happens** - the events, in order.

        **Who appears** - every character in the chapter, each with a one-line reminder
        of who they are and what they last did that mattered. Include the ones who have
        been absent for many chapters, and say how long it has been - this is exactly
        where readers of long novels lose the thread, and it is the most useful part of
        this summary.

        **Threads advanced** - what moved, and what it changed.

        **Threads running in parallel** - what is happening elsewhere at this point in
        the story's chronology. Use only what the material you were given establishes;
        if it tells you nothing about the other threads, say so and write nothing more
        under this heading.

        **Relationships and alliances** - what changed between people.

        **Setups and payoffs** - what was planted here, and what planted earlier was
        paid off.

        **Themes** - two or three sentences at most. Keep this the shortest heading;
        the reader came for the cast and the plot.

        Use the names the book uses. Do not reveal anything from later in the book, do
        not invent a connection the text has not made, and do not tell the reader what
        to feel about a character.
        """;

    private const string SectionSummaryPrompt = """
        You are explaining one section of a novel to a reader who wants to follow it
        properly.

        Organise what you write **by plot thread**. If this section cuts between
        different sets of characters or places, give each its own short labelled
        movement, named for whose thread it is - never one blurred narrative that runs
        them together. If it follows one thread throughout, say so and write one.

        Within each movement cover: who is present, what happens, what changes between
        them, and anything planted or paid off. Then define any period vocabulary,
        foreign phrase, form of address, or social convention that a reader today would
        not confidently follow.

        Two to five paragraphs in total. Do not pad, do not restate the passage in
        slightly different words, and do not give away anything after this section.
        """;

    private const string PassageAnalysisPrompt = """
        You are an expert reader analysing one passage a reader has selected from a
        novel.

        Address each of these in turn, briefly, and skip any that the passage gives
        you nothing for rather than inventing something to say:

        1. What happens - who is present, and what is going on between them.
        2. What is being said without being said - subtext, evasion, and what a
           character will not admit.
        3. Craft - point of view, voice, and how the passage is built.
        4. How it sits in the whole - what it depends on and what it sets up.
        5. Character - what this shows about the people in it that was not clear before.
        6. Context - the period, social, or literary conventions the passage assumes.

        Then a **Definitions** section: every term in the passage a reader today would
        not confidently define - period vocabulary, foreign phrases, forms of address
        and what they signal about rank or intimacy, customs, currencies, and named
        places, people, and works. Be exhaustive here - this section is the one most
        readers came for. One line each.

        Say nothing about what happens after this passage.
        """;

    private const string ExplainSimplyPrompt = """
        You are a friendly reader catching a friend up on a novel they are lost in.
        Write in a warm, conversational tone.

        Three to five short paragraphs. No headings, no bullet points, no numbered
        lists.

        Get across who these people are and how they are connected, what just happened,
        why it matters to them, and what it changes. Untangle the names - if one person
        is called three different things, say so plainly. Be direct and vivid.

        Simplify the telling, never the story. If a character behaves badly or a scene
        is bleak, leave it bad and bleak. Do not tell the reader what happens next.
        """;

    // ─── the story model ─────────────────────────────────────────────────

    private static readonly string StoryExtractionPrompt =
        SharedPrompts.StoryExtraction(
            opening: """
                You are maintaining a running record of the characters and plot threads in a
                novel. You are given a summary of one chapter and a compacted digest of what
                the record already holds.
                """,
            kinds: """
                - An actor is a named or clearly identifiable character. Its tier is "major",
                  "secondary", "minor", or "mentioned", judged by their part in the story and
                  not by how often this chapter names them.
                - A group is a family, a household, a social circle, or a faction.
                - An edge is a relationship between two characters: "family", "married",
                  "allied", "rival", "employs", "loves", "betrays".
                - A thread is a strand of plot that runs across chapters.
                - If a character already in the digest appears under another name - a given
                  name, a patronymic, a title, a nickname, a married name, a different
                  transliteration - report it under "aliasHints" with your confidence and the
                  id you believe it matches. Do not merge it yourself, and do not create a new
                  actor for it.
                - Report a character you cannot match to the digest and cannot confidently
                  place as a new actor rather than guessing at an alias.
                """);
}
