namespace AnnasArchive.API.Reader2.Lenses;

/// <summary>
/// Campaign and battle history, read the way a staff college reads it.
///
/// <para>The organising question is never "what is the author arguing" but "what
/// was decided, by whom, under what constraints, and what followed". Everything
/// below follows from that: the ladder preserves times, places, and unit
/// designations that a literary summary would smooth away, because a decision
/// cannot be judged without the ground and the hour it was taken on.</para>
///
/// <para>Decision points get the most words of any heading. That is the heart of
/// the lens — a chapter that lists what happened without saying what else could
/// have been chosen is a chronicle, which is the thing this lens exists not to
/// produce.</para>
///
/// <para>Builds a story model: a campaign has a cast, and losing track of which
/// corps is under whom is exactly how a long history stops being followable.</para>
/// </summary>
public sealed class MilitaryLens : IReaderLens
{
    public string Key => "military";
    public string DisplayName => "Campaigns";
    public string Description =>
        "Military history and campaign studies. Follows decisions, command, "
        + "doctrine, and cost.";
    public string Icon => "military_tech";

    public int SortOrder => 1;

    /// <summary>
    /// Bump on any edit below. The golden tests refuse the edit otherwise, which
    /// is what stops artifacts outliving the wording that produced them.
    /// </summary>
    public int PromptVersion => 1;

    public bool BuildsStoryModel => true;

    /// <summary>
    /// The same story machinery the fiction lens uses, under the nouns a reader
    /// of campaign history expects to see above the table.
    /// </summary>
    public StoryVocabulary? StoryVocabulary =>
        new(Actors: "Commanders & Units", Groups: "Belligerents", Threads: "Operations");

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
        You are reading one part of a longer chapter of military history. Another
        pass will combine your output with the rest, so write for that pass and not
        for a reader.

        Record, in 300-400 words:
        - the ground, the weather, the time of day, and the date, wherever they are given
        - every formation and unit named, at the strength and under the commander stated
        - what was ordered, by whom, to whom, and when it arrived
        - what actually happened, in the order it happened
        - every point at which somebody chose between courses of action, and what was
          known to them at that moment
        - losses, ammunition, supply, and distance marched, with the figures as written
        - anything the author flags as disputed between sources

        Prose, not bullet points. Keep proper names, unit designations, and numbers
        exactly as the text gives them - a later pass cannot recover a figure you
        rounded. Do not judge the decisions here; record what was in front of the
        people taking them.
        """;

    private const string SectionSynthesisPrompt = """
        You are given several consecutive notes covering one stretch of a chapter of
        military history. Combine them into one account of 400-500 words that a
        final pass will summarise again.

        Follow the operation as it unfolded, in time order, rather than listing the
        notes in turn. Where two notes describe the same action from different
        formations, state it once and say who was on each side of it. Keep every
        unit designation, commander, place, time, and figure. Where the notes
        disagree or leave a gap in the sequence, say so rather than bridging it.

        Prose, no headings, no bullet points. Write for a further pass, not a reader.
        """;

    private const string ChapterSummaryPrompt = """
        You are a staff college instructor writing for a reader who wants to
        understand what was decided, by whom, under what constraints, and what
        followed.

        Write 700-900 words under exactly these eight headings:

        **Situation** - the belligerents, their strengths and dispositions, the
        ground, the weather, logistics, and the time available.

        **Mission and intent** - what each side was trying to achieve, and the intent
        behind it rather than only the task.

        **Execution** - the sequence, phase by phase.

        **Decision points** - each branch in turn: who decided, what they knew and did
        not know at that moment, which courses of action were genuinely open to them
        then, what was chosen, and what the plausible alternatives would likely have
        produced. Give this heading more words than any other; it is the reason this
        summary exists. Judge decisions on the information available at the time, never
        on the outcome.

        **Command and human factors** - the chain of command, rivalries between
        commanders, friction, morale, and the quality of information travelling up and
        down.

        **Doctrine** - which principles of war are illustrated or violated here, and
        the period doctrine the conduct reflects.

        **Outcome and cost** - the tactical result, the operational consequence, and
        the casualties and materiel expended.

        **Lessons** - what a staff officer takes away.

        Use the correct unit designations, ranks, and place names, and give figures as
        the text gives them. Where the sources conflict or the author says the record
        is unclear, say so rather than choosing. Do not invent numbers, dates, orders,
        or intentions - if the text does not establish something, leave it out.
        """;

    private const string SectionSummaryPrompt = """
        You are an expert military historian explaining one section of a campaign
        history to a reader who wants to follow it properly.

        Cover, in 2-5 paragraphs depending on the section's difficulty:
        - what happens here, in time order, and where on the ground it happens
        - every formation, commander, and rank named: who they are, what they command,
          and whose orders they are under
        - every decision taken in this section, what was known at the time, and what
          else was open to whoever took it
        - the technical vocabulary: unit designations, equipment nomenclature, staff
          abbreviations, and terms of art, defined as they are used here
        - what this section changes about the wider operation

        Assume a reader who is intelligent but has no military background. Explain
        the terminology rather than avoiding it. Do not pad, and do not restate the
        passage in slightly different words.
        """;

    private const string PassageAnalysisPrompt = """
        You are a military historian analysing one passage a reader has selected from
        a work of campaign or battle history.

        Address each of these in turn, briefly, and skip any that the passage gives
        you nothing for rather than inventing something to say:

        1. What happens - the action, the ground, the units, and the sequence.
        2. The decision in it - who chose, what they knew, and what else was open to them.
        3. Command and friction - what the passage shows about how orders and
           information were actually moving.
        4. How it sits in the operation - what it depends on and what it makes possible.
        5. Doctrine and technique - what the conduct here reflects about how these
           forces were trained to fight.
        6. Historiography - where historians have read this episode differently.

        Then a **Definitions** section covering every term a reader without a military
        background would not confidently define: unit designations and what size of
        formation each denotes, ranks and their equivalents across the armies
        involved, weapon and equipment nomenclature, staff abbreviations, and place
        names that carry strategic weight, with the weight explained. Be exhaustive
        here - this section is the one most readers came for. One line each.
        """;

    private const string ExplainSimplyPrompt = """
        You are a friendly teacher who makes a confusing battle feel clear. Write in a
        warm, conversational tone for a clever reader with no military background at
        all.

        Three to five short paragraphs. No headings, no bullet points, no numbered
        lists.

        Get across who was trying to do what, why it was hard, what the crucial
        decision was and why it looked reasonable to the person making it, and what it
        cost. Be direct and vivid. Use an everyday comparison where one genuinely fits
        and none where one does not.

        Simplify the explanation, never the events. Do not tidy a shambles into a plan,
        and do not make a decision look obvious that was not obvious at the time.
        """;

    // ─── the story model ─────────────────────────────────────────────────

    private static readonly string StoryExtractionPrompt =
        SharedPrompts.StoryExtraction(
            opening: """
                You are maintaining a running record of the commanders, formations, and
                operations in a campaign history. You are given a summary of one chapter and
                a compacted digest of what the record already holds.
                """,
            kinds: """
                - An actor is a named commander or an identifiable formation. Its tier is
                  "major", "secondary", "minor", or "mentioned", judged by its part in the
                  campaign and not by how often the chapter names it.
                - A group is a belligerent, a coalition, an army, or a service arm.
                - An edge is a relationship between two actors: "commands", "subordinate-to",
                  "liaison", "rival", "relieved-by", "opposes".
                - A thread is an operation, an offensive, a siege, or a campaign phase.
                - If an actor already in the digest appears under another designation - a
                  different transliteration, a title, a formation renumbered, a headquarters
                  named for its commander - report it under "aliasHints" with your confidence
                  and the id you believe it matches. Do not merge it yourself, and do not
                  create a new actor for it.
                - Report an actor you cannot match to the digest and cannot confidently place
                  as a new actor rather than guessing at an alias.
                """);
}
