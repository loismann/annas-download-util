namespace AnnasArchive.API.Reader2.Lenses;

/// <summary>
/// One lens's complete prompt ladder.
///
/// <para>The three summary tiers are tuned as one: chunks are written to be
/// synthesised (300–400 words), sections to be summarised again (400–500), and
/// only the last tier for a person (700–900). A lens supplies the whole ladder —
/// it is never a fragment appended to a shared base, because a base plus
/// overrides is how the two pathways Reader II exists to avoid get built.</para>
/// </summary>
public sealed record LensPrompts(
    string PassageAnalysis,
    string ChunkSummary,
    string SectionSynthesis,
    string ChapterSummary,
    string SectionSummary,
    string ExplainSimply,
    string? StoryExtraction = null)
{
    /// <summary>
    /// The wording for one call, or null — for the optional story prompt, and
    /// for the two kinds no lens owns (see <see cref="CallKinds.Lens"/>).
    /// </summary>
    public string? this[CallKind kind] => kind switch
    {
        CallKind.PassageAnalysis => PassageAnalysis,
        CallKind.ChunkSummary => ChunkSummary,
        CallKind.SectionSynthesis => SectionSynthesis,
        CallKind.ChapterSummary => ChapterSummary,
        CallKind.SectionSummary => SectionSummary,
        CallKind.ExplainSimply => ExplainSimply,
        CallKind.StoryExtraction => StoryExtraction,
        CallKind.ChapterLabels or CallKind.LearnMore or CallKind.SectionVocab => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped call kind.")
    };
}

/// <summary>
/// One version per prompt, rather than one for the whole lens.
///
/// <para><b>Why this exists.</b> A lens used to carry a single
/// <c>PromptVersion</c> gating every artifact it produced, so a one-line edit to
/// the story-extraction prompt marked every chapter summary in every book as
/// written under old wording. Six of this lens's seven prompts had been
/// byte-identical since version 1 and had still been dragged to version 4 —
/// which was not a versioning scheme, it was one number wearing seven hats.</para>
///
/// <para>Mirrors <see cref="LensPrompts"/> field for field on purpose: the thing
/// being versioned and the version travel in the same shape, so a new prompt
/// cannot be added without somewhere obvious to say what version it is at.</para>
///
/// <para><b>They start at 4, not 1.</b> Stored artifacts record the whole-lens
/// version that wrote them, and the newest of those is 4. Starting a prompt at
/// its "true" version — 1, for the six that never changed — would leave every
/// stored row reading as <i>newer</i> than current, and the next three edits
/// would be undetectable. Aligning with what is on disk is what makes the first
/// bump after this change mean something.</para>
/// </summary>
public sealed record PromptVersions(
    int PassageAnalysis = 1,
    int ChunkSummary = 1,
    int SectionSynthesis = 1,
    int ChapterSummary = 1,
    int SectionSummary = 1,
    int ExplainSimply = 1,
    int StoryExtraction = 1)
{
    /// <summary>Every prompt in this lens at one version — the state before the split.</summary>
    public static PromptVersions All(int version) =>
        new(version, version, version, version, version, version, version);

    public int this[CallKind kind] => kind switch
    {
        CallKind.PassageAnalysis => PassageAnalysis,
        CallKind.ChunkSummary => ChunkSummary,
        CallKind.SectionSynthesis => SectionSynthesis,
        CallKind.ChapterSummary => ChapterSummary,
        CallKind.SectionSummary => SectionSummary,
        CallKind.ExplainSimply => ExplainSimply,
        CallKind.StoryExtraction => StoryExtraction,

        // The three no lens owns carry SharedPrompts.Version instead; asking a
        // lens for one is a mistake worth failing on rather than answering 0.
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "This call's wording belongs to SharedPrompts, not to a lens.")
    };
}

/// <summary>
/// What a story model's parts are called for this subject matter.
///
/// <para>Fiction and military reading need the same machinery with different
/// nouns — characters and factions, or commanders and belligerents. One
/// implementation, labelled by the lens, rather than two that drift.</para>
/// </summary>
public sealed record StoryVocabulary(string Actors, string Groups, string Threads);

/// <summary>
/// A way of reading a book. Adding one is a class, a DI registration, and its
/// tests — no schema change, no endpoint change, no frontend change. The
/// contract test in the test project is what keeps that true.
/// </summary>
public interface IReaderLens
{
    /// <summary>Stable, lowercase, kebab-case. Stored in every artifact row.</summary>
    string Key { get; }

    /// <summary>What the picker shows.</summary>
    string DisplayName { get; }

    /// <summary>Tooltip text, served to the UI.</summary>
    string Description { get; }

    /// <summary>Material icon name.</summary>
    string Icon { get; }

    /// <summary>Picker order. Also decides the default lens — the lowest wins.</summary>
    int SortOrder { get; }

    /// <summary>
    /// One version per prompt, bumped when <i>that</i> prompt changes.
    ///
    /// <para>This is the whole point of the versioning: an artifact records the
    /// version that produced it, so a prompt edit makes existing rows detectably
    /// stale instead of silently serving output from wording nobody has used for
    /// months. Per prompt rather than per lens, because a shared number made an
    /// edit to one prompt say something false about the other six — see
    /// <see cref="PromptVersions"/>.</para>
    /// </summary>
    PromptVersions Versions { get; }

    LensPrompts Prompts { get; }

    /// <summary>Whether this lens accumulates a story model.</summary>
    bool BuildsStoryModel { get; }

    /// <summary>Required when <see cref="BuildsStoryModel"/>, forbidden otherwise.</summary>
    StoryVocabulary? StoryVocabulary { get; }
}
