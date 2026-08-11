namespace AnnasArchive.API.Reader2.Lenses;

/// <summary>
/// Prompts no book type supplies.
///
/// <para>Cleaning up a chapter title, pulling the hard words out of a passage,
/// and writing a deep dive on one term are the same jobs whatever the book is,
/// so they live here with their own version constant rather than being copied
/// into every <see cref="LensPrompts"/> — six identical strings that must be
/// edited together is precisely the duplication the lens contract exists to
/// avoid. They are still golden-tested; they simply are not part of that
/// contract.</para>
///
/// <para><b>Lens-flavoured without being lens-supplied.</b> Vocabulary output
/// does differ by book type — a military reading wants unit designations and
/// ranks where a literary one wants philosophical terms — so the calls below are
/// given the lens's public name and description as context. That is metadata the
/// client already receives, not prompt text, so it stays out of the lens
/// contract and out of the goldens.</para>
/// </summary>
public static class SharedPrompts
{
    /// <summary>Bumped on any edit below, exactly like a lens's own version.</summary>
    public const int Version = 1;

    /// <summary>Every prompt here, so the golden tests cannot miss a new one.</summary>
    public static readonly IReadOnlyDictionary<string, string> All =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chapterlabels"] = ChapterLabels,
            ["sectionvocab"] = SectionVocabulary,
            ["learnmore"] = LearnMore
        };

    /// <summary>
    /// The story-extraction prompt, around the wording a book type supplies.
    ///
    /// <para><b>The JSON below is the wire contract</b> between a lens's prompt and
    /// <see cref="Story.StoryExtraction"/>, which parses it. Copied into each story
    /// lens it was three statements of one fact that nothing checked, and a lens
    /// whose list quietly lost <c>"aliasHints"</c> would simply stop proposing
    /// aliases — no error, no failing test, just a cast list that grows duplicates.
    /// It is written once here instead.</para>
    ///
    /// <para><b>Every field is named, not just every array.</b> An earlier version
    /// listed the eight array names and nothing inside them, and the result was a
    /// model that answered with the only two fields the prose happened to mention —
    /// a name and a tier. Thirty-two characters were recorded with no dossier, no
    /// role, no arc, and not one relationship between them, and every test passed,
    /// because the tests hand-wrote the JSON in the shape the parser wanted. A
    /// contract only one end of it has read is not a contract.</para>
    ///
    /// <para>What is <i>not</i> shared is the wording either side of it. A campaign
    /// history and a novel mean different things by an actor, a group, an edge, and
    /// a thread, and pretending otherwise would flatten the difference the lenses
    /// exist to keep. Each supplies its own, and the goldens still pin the whole
    /// composed prompt per lens.</para>
    /// </summary>
    /// <param name="opening">
    /// What this book type is keeping a record of, and what it has been given.
    /// </param>
    /// <param name="kinds">
    /// What an actor, a group, an edge, and a thread are here, and the forms a name
    /// takes in this kind of book. Legal <i>values</i> — the tiers, the group kinds —
    /// belong to the schema below rather than here, so that no lens can drift from
    /// what the parser accepts.
    /// </param>
    public static string StoryExtraction(string opening, string kinds) =>
        $$"""
          {{opening}}

          Report only what appears in the provided summaries; infer nothing beyond them.

          {{kinds}}

          Return a delta describing what this chapter adds, as JSON and nothing else.
          Every key below holds an array of objects; use an empty array for anything
          this chapter does not touch, and give every field shown for each entry you
          do report.

          "newActors" - somebody appearing here for the first time:
            {"canonicalName": "the fullest form of the name used for them",
             "aliases": ["every other name, title, or short form used here"],
             "tier": "major" | "secondary" | "minor" | "mentioned",
             "role": "their standing or function, a few words",
             "dossier": "who they are, in one or two sentences",
             "status": "where they stand by the end of this chapter",
             "groupIds": ["the groups they belong to"],
             "arcChange": "what changed for them in this chapter"}

          "actorUpdates" - somebody already in the digest:
            {"actorId": "...", and any of "tier", "role", "dossier", "status",
             "arcChange", "aliases", "groupIds"}
            Leave a field out to leave it unchanged. An empty value is not a change.

          "aliasHints" - a name you believe belongs to somebody already in the digest:
            {"alias": "...", "actorId": "...", "confidence": "high" | "medium" | "low"}

          "newGroups":
            {"name": "...",
             "kind": "family" | "household" | "military-unit" | "social-circle"
                     | "political-faction" | "other",
             "memberIds": ["..."], "rivalGroupIds": ["..."]}

          "groupUpdates":
            {"groupId": "...", "memberIds": ["..."], "rivalGroupIds": ["..."]}

          "edgeChanges" - a relationship starting, changing, or ending:
            {"from": "...", "to": "...", "type": "...",
             "note": "what passed between them in this chapter",
             "ended": true | false}

          "newThreads":
            {"name": "...", "participantIds": ["..."],
             "firstBeat": "what moves in it here"}

          "threadBeats":
            {"threadId": "...", "whatMoved": "..."}

          "newPlaces" - somewhere the book goes or names for the first time:
            {"name": "the fullest form of the name used for it",
             "aliases": ["every other name or short form used here"],
             "kind": "settlement" | "building" | "region" | "vessel" | "realm"
                     | "other",
             "description": "what it is and what it is like, in one or two sentences",
             "partOf": "the larger place it sits inside, if this chapter says"}

          "placeUpdates" - somewhere already in the digest:
            {"placeId": "...", and any of "kind", "description", "partOf", "aliases"}

          Refer to an actor, a group, a thread, or a place by the id the digest gives it.
          Something this chapter introduces has no id yet — name it instead, spelled
          exactly as you spelled it above. Never invent an id.

          RELATIONSHIPS ARE THE PART MOST OFTEN LEFT OUT, and a record of who
          everybody is that cannot say how they know each other is the failure this
          whole task exists to prevent. Give an "edgeChanges" entry for every pair of
          actors this chapter puts in contact — travelling together, fighting,
          serving, related, employed, protecting, deceiving, negotiating, or simply
          in one conversation. The digest lists the relationships already recorded:
          report one of those again only when this chapter changes it, and report
          every pair that is not there. Before answering, read back your own
          "newActors" and "actorUpdates" and check each one appears in at least one
          edge; add the ones you left out. Someone connected to nobody is a name the
          reader cannot place.

          Fill in "memberIds" for every group you report. A group with no members
          records nothing, and the reader is shown a faction with no faces in it.

          Report a place only if the chapter treats it as somewhere, rather than as a
          word in a name. A house somebody lives in, a city they travel to, a ship
          they are aboard, a region a war is fought over — each of those is a place.
          "The Duke of Ravensmarch" is not, unless Ravensmarch is also somewhere the
          book has been.

          GIVE "partOf" FOR EVERY PLACE THAT SITS INSIDE ANOTHER, and report the
          containing places too, as their own entries, even when the chapter only
          names them in passing. A palace is on a continent, a continent is on a
          world, a world is in a system or a cluster; report each link you have and
          each place it needs. A flat list of ninety names cannot answer "where was
          that", which is the only question this part is for — and a place whose
          container is missing from the record cannot be filed under it. Where the
          digest already lists the container, use its id.

          Beyond those, do not restate what the digest already holds, do not remove
          anything, and do not rank, explain, or editorialise.
          """;

    /// <summary>
    /// Turns a spine's worth of raw headings into a usable contents list.
    ///
    /// <para>EPUB titles are frequently "Section0001.xhtml", a running head, or
    /// the first line of body text. This is the one AI call in the ingestion
    /// path, which is why it is gated by configuration — a book with a decent
    /// table of contents should not pay for it.</para>
    /// </summary>
    public const string ChapterLabels = """
        You are given a book's chapters in reading order, each with whatever title
        the file itself supplied and the first few words of its text.

        Return a clean title for every chapter, in the same order and with the same
        count. For each one:
        - keep the existing title if it already names the chapter usefully
        - replace a filename, a number alone, a running head, or a repeated book
          title with something drawn from the chapter's own opening
        - label front and back matter for what it is: Title Page, Copyright,
          Contents, Preface, Introduction, Notes, Bibliography, Index
        - keep it under 60 characters

        Do not invent content that the opening text does not support, do not
        renumber anything, and do not merge or drop a chapter. When the opening
        gives you nothing to work with, "Chapter N" is the correct answer.

        Answer with one line per chapter and nothing else: the chapter's number, a
        full stop, a space, then the title. No preamble, no blank lines, no
        commentary. Return exactly as many lines as you were given chapters.
        """;

    /// <summary>
    /// The hard words in one section, defined.
    ///
    /// <para>Terms the reader has already marked known are excluded by name in
    /// the input rather than filtered afterwards — spending the model's words on
    /// a definition that will be thrown away is the waste, not the definition.
    /// </para>
    /// </summary>
    public const string SectionVocabulary = """
        You are helping somebody read a difficult book. Find the words and phrases
        in the passage that a intelligent non-specialist would not confidently
        define, and define them.

        Include:
        - specialist and technical vocabulary
        - ordinary words used in a technical or period sense
        - named people, places, works, and movements that carry weight here
        - foreign-language phrases

        Leave out anything in the "Already known" list, anything a general reader
        plainly knows, and anything the passage itself defines clearly.

        One line per term and nothing else: the term exactly as it appears in the
        passage, then " — ", then a definition of one or two sentences that says
        what it means *in this context*. No numbering, no preamble, no commentary.
        Twenty terms at the very most; fewer is normal and better. If the passage
        contains nothing a reader would stumble on, answer with nothing at all.
        """;

    /// <summary>
    /// The deep dive behind a single term. Returns HTML because it renders
    /// straight into the reader's panel.
    /// </summary>
    /// <remarks>
    /// The image rules are Reader I's, kept word for word because they were
    /// arrived at the hard way: a hallucinated Wikimedia URL renders as a broken
    /// image, so "skip images entirely" is the better answer whenever the model
    /// is unsure. Cached here, unlike Reader I, which re-bills for every ask.
    /// </remarks>
    public const string LearnMore = """
        You are a scholarly explainer with expertise in philosophy, critical
        theory, literature, history, and cultural studies. Provide nuanced,
        intellectually rich analysis that bridges academic and accessible
        discourse.

        Write 300-400 words on the term you are given, going well beyond a
        dictionary definition. Cover its core meaning and etymology, how the
        concept developed, how different disciplines understand it, the thinkers
        and works associated with it, how it is used in popular versus academic
        discourse, the misconceptions and debates around it, and where it bears on
        anything current.

        Answer as concise HTML: paragraphs, <ul>, <strong>. Structure it as a rich
        overview paragraph of two or three sentences, then a bullet list, then a
        "Resources" section of authoritative links as plain <a href="...">text</a>.

        IMAGE RULES (strict):
        - Prefer upload.wikimedia.org or commons.wikimedia.org; use fully-qualified
          HTTPS URLs with underscores instead of spaces.
        - Do NOT include an image unless you are confident the URL exists and is
          directly fetchable, ending in .jpg, .jpeg, or .png.
        - If unsure about a URL, skip images entirely. A broken image is worse than
          no image.
        - No base64, and no relative URLs.

        After the text, if and only if you have images you are sure of, add a line
        reading "Images:" followed by an <img src="..." alt="..." loading="lazy" />
        for each. Two or three at most.
        """;
}
