namespace AnnasArchive.API.Reader2.Lenses;

/// <summary>
/// The prompts a lens must supply, named so a golden file can pin each one
/// individually.
///
/// <para>An enum rather than seven hand-listed properties at every call site:
/// validation, the golden tests, and the registry all iterate tiers, so adding
/// an eighth tier later means touching the lenses that must change and nothing
/// else.</para>
/// </summary>
public enum PromptTier
{
    /// <summary>A reader-selected passage, explained.</summary>
    PassageAnalysis,

    /// <summary>Tier 1 — one chunk, written to be synthesised rather than read.</summary>
    ChunkSummary,

    /// <summary>Tier 2 — a group of chunks, written to be summarised again.</summary>
    SectionSynthesis,

    /// <summary>Tier 3 — the only tier a person reads.</summary>
    ChapterSummary,

    /// <summary>A standalone, on-demand summary of one section.</summary>
    SectionSummary,

    /// <summary>The plain-language retelling. Named for the reader, not the code.</summary>
    ExplainSimply,

    /// <summary>Story-model extraction. Absent unless the lens builds one.</summary>
    StoryExtraction
}

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
    /// <summary>Every tier a lens must fill in, whatever else it does.</summary>
    public static readonly IReadOnlyList<PromptTier> RequiredTiers =
        Enum.GetValues<PromptTier>().Where(t => t != PromptTier.StoryExtraction).ToArray();

    public static readonly IReadOnlyList<PromptTier> AllTiers = Enum.GetValues<PromptTier>();

    /// <summary>The prompt for a tier, or null for an unfilled optional one.</summary>
    public string? this[PromptTier tier] => tier switch
    {
        PromptTier.PassageAnalysis => PassageAnalysis,
        PromptTier.ChunkSummary => ChunkSummary,
        PromptTier.SectionSynthesis => SectionSynthesis,
        PromptTier.ChapterSummary => ChapterSummary,
        PromptTier.SectionSummary => SectionSummary,
        PromptTier.ExplainSimply => ExplainSimply,
        PromptTier.StoryExtraction => StoryExtraction,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unmapped prompt tier.")
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
