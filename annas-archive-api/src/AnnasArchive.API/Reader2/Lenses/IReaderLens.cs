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
    /// Bumped whenever any prompt below changes.
    ///
    /// <para>This is the whole point of the versioning: an artifact records the
    /// version that produced it, so a prompt edit makes existing rows detectably
    /// stale instead of silently serving output from wording nobody has used for
    /// months.</para>
    /// </summary>
    int PromptVersion { get; }

    LensPrompts Prompts { get; }

    /// <summary>Whether this lens accumulates a story model.</summary>
    bool BuildsStoryModel { get; }

    /// <summary>Required when <see cref="BuildsStoryModel"/>, forbidden otherwise.</summary>
    StoryVocabulary? StoryVocabulary { get; }
}
