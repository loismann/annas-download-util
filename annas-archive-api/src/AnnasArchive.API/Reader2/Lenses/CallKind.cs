namespace AnnasArchive.API.Reader2.Lenses;

/// <summary>
/// Every distinct thing Reader II asks a model to do.
///
/// <para>One enum rather than two. A lens supplies the wording for most of these
/// and configuration supplies every one's budget, model, and temperature — and
/// if "which prompt" and "which budget" were separate enums sharing seven
/// members, adding a tier would mean editing both and a mismatch would be
/// silent.</para>
///
/// <para>The two at the end are not a lens's business: chapter labels are the
/// same job for every book type, and a vocabulary deep dive is a facility every
/// type uses. They still have budgets and models, which is why they are here.</para>
/// </summary>
public enum CallKind
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
    StoryExtraction,

    /// <summary>Tidying a book's chapter titles. Lens-independent.</summary>
    ChapterLabels,

    /// <summary>A vocabulary deep dive. Lens-flavoured but not part of the ladder.</summary>
    LearnMore,

    /// <summary>The hard words in one section, defined.</summary>
    SectionVocab
}

/// <summary>Which calls belong to whom, so no list of kinds is written twice.</summary>
public static class CallKinds
{
    public static readonly IReadOnlyList<CallKind> All = Enum.GetValues<CallKind>();

    /// <summary>The calls a lens supplies wording for.</summary>
    public static readonly IReadOnlyList<CallKind> Lens =
        All.Where(k => k is not (CallKind.ChapterLabels or CallKind.LearnMore or CallKind.SectionVocab))
           .ToArray();

    /// <summary>The calls every lens must supply — all of its own but the optional story one.</summary>
    public static readonly IReadOnlyList<CallKind> RequiredOfEveryLens =
        Lens.Where(k => k != CallKind.StoryExtraction).ToArray();
}
