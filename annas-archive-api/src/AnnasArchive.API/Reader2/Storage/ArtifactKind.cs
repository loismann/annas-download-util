namespace AnnasArchive.API.Reader2.Storage;

/// <summary>
/// Every kind of thing Reader II stores about a book.
///
/// <para>Stored in <c>r2_artifact.kind</c> as the kebab-case wire name, never as
/// the integer: the integer would make the column unreadable and would silently
/// repoint every row if anyone reordered the enum.</para>
/// </summary>
public enum ArtifactKind
{
    ChapterIndex,
    ChunkBoundaries,
    ChapterLabels,
    PassageAnalysis,
    SectionSummary,
    ChapterSummary,
    ExplainSimply,
    SectionVocab,
    LearnMore,
    StoryModel,
    CastOverrides,
    Flashcards
}

/// <summary>
/// The one place the wire names live, so a kind cannot be spelled two ways.
/// </summary>
public static class ArtifactKinds
{
    /// <summary>The <c>lens_key</c> for artifacts no book type can change.</summary>
    public const string NoLens = "none";

    private static readonly IReadOnlyDictionary<ArtifactKind, string> ToWireName =
        new Dictionary<ArtifactKind, string>
        {
            [ArtifactKind.ChapterIndex] = "chapter-index",
            [ArtifactKind.ChunkBoundaries] = "chunk-boundaries",
            [ArtifactKind.ChapterLabels] = "chapter-labels",
            [ArtifactKind.PassageAnalysis] = "passage-analysis",
            [ArtifactKind.SectionSummary] = "section-summary",
            [ArtifactKind.ChapterSummary] = "chapter-summary",
            [ArtifactKind.ExplainSimply] = "explain-simply",
            [ArtifactKind.SectionVocab] = "section-vocab",
            [ArtifactKind.LearnMore] = "learn-more",
            [ArtifactKind.StoryModel] = "story-model",
            [ArtifactKind.CastOverrides] = "cast-overrides",
            [ArtifactKind.Flashcards] = "flashcards"
        };

    private static readonly IReadOnlyDictionary<string, ArtifactKind> FromWireName =
        ToWireName.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public static string Wire(this ArtifactKind kind) =>
        ToWireName.TryGetValue(kind, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped artifact kind.");

    public static ArtifactKind Parse(string wire) =>
        FromWireName.TryGetValue(wire, out var kind)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(wire), wire, "Unknown artifact kind.");

    public static bool TryParse(string? wire, out ArtifactKind kind)
    {
        if (wire is not null && FromWireName.TryGetValue(wire, out kind)) return true;
        kind = default;
        return false;
    }

    /// <summary>Every kind, for tests that must cover the whole set.</summary>
    public static readonly IReadOnlyList<ArtifactKind> All = ToWireName.Keys.ToArray();
}
