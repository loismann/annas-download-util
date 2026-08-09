using AnnasArchive.API.Reader2.Domain;

namespace AnnasArchive.API.Reader2.Storage;

/// <summary>
/// Identifies one stored artifact, and is the only way to build one.
///
/// <para>The constructor is private and every kind has a named factory below, so
/// the shape of a key — which kinds are lens-scoped, which carry a chapter, what
/// <c>Ordinal</c> means for each — is stated exactly once. In Reader I the same
/// knowledge was spread across twelve hand-rolled <c>Path.Combine</c> calls and
/// could only be recovered by reading all of them; two of them disagreed.</para>
///
/// <para><c>-1</c> and <c>""</c> are sentinels rather than nulls because SQLite
/// treats NULLs as distinct in a UNIQUE constraint, so a nullable chapter would
/// let the same book-scoped artifact be inserted twice.</para>
/// </summary>
public sealed record ArtifactKey
{
    public const int NoChapter = -1;
    public const int NoOrdinal = -1;
    public const string NoSubkey = "";

    public BookRef Book { get; }
    public string LensKey { get; }
    public ArtifactKind Kind { get; }
    public int Chapter { get; }
    public int Ordinal { get; }
    public string Subkey { get; }

    private ArtifactKey(
        BookRef book, string lensKey, ArtifactKind kind, int chapter, int ordinal, string subkey)
    {
        Book = book;
        LensKey = lensKey;
        Kind = kind;
        Chapter = chapter;
        Ordinal = ordinal;
        Subkey = subkey;
    }

    // ─── Lens-independent: no book type changes these ────────────────────

    public static ArtifactKey ChapterIndex(BookRef book) =>
        BookScoped(book, ArtifactKinds.NoLens, ArtifactKind.ChapterIndex);

    public static ArtifactKey ChapterLabels(BookRef book) =>
        BookScoped(book, ArtifactKinds.NoLens, ArtifactKind.ChapterLabels);

    public static ArtifactKey Flashcards(BookRef book) =>
        BookScoped(book, ArtifactKinds.NoLens, ArtifactKind.Flashcards);

    /// <summary>
    /// Section boundaries are paragraph arithmetic, identical for every book
    /// type — so they are stored once and survive a lens switch untouched.
    /// </summary>
    public static ArtifactKey ChunkBoundaries(BookRef book, int chapter) =>
        ChapterScoped(book, ArtifactKinds.NoLens, ArtifactKind.ChunkBoundaries, chapter);

    // ─── Lens-scoped, chapter-level ──────────────────────────────────────

    public static ArtifactKey ChapterSummary(BookRef book, string lensKey, int chapter) =>
        ChapterScoped(book, Lens(lensKey), ArtifactKind.ChapterSummary, chapter);

    public static ArtifactKey ExplainSimply(BookRef book, string lensKey, int chapter) =>
        ChapterScoped(book, Lens(lensKey), ArtifactKind.ExplainSimply, chapter);

    // ─── Lens-scoped, within a chapter ───────────────────────────────────

    public static ArtifactKey SectionSummary(BookRef book, string lensKey, int chapter, int section) =>
        Ordered(book, Lens(lensKey), ArtifactKind.SectionSummary, chapter, section, nameof(section));

    public static ArtifactKey SectionVocab(BookRef book, string lensKey, int chapter, int section) =>
        Ordered(book, Lens(lensKey), ArtifactKind.SectionVocab, chapter, section, nameof(section));

    /// <summary><paramref name="wordOffset"/> is the position in the chapter, which is
    /// what makes two analyses of different passages distinct rows.</summary>
    public static ArtifactKey PassageAnalysis(BookRef book, string lensKey, int chapter, int wordOffset) =>
        Ordered(book, Lens(lensKey), ArtifactKind.PassageAnalysis, chapter, wordOffset, nameof(wordOffset));

    // ─── Lens-scoped, book-level ─────────────────────────────────────────

    public static ArtifactKey StoryModel(BookRef book, string lensKey) =>
        BookScoped(book, Lens(lensKey), ArtifactKind.StoryModel);

    /// <summary>
    /// Keyed by the normalised term, so the same word asked twice is one row.
    /// Lens-scoped on purpose: what matters about a term differs by book type.
    /// </summary>
    public static ArtifactKey LearnMore(BookRef book, string lensKey, string termNorm)
    {
        if (string.IsNullOrWhiteSpace(termNorm))
            throw new ArgumentException("Term is required.", nameof(termNorm));

        return new ArtifactKey(book, Lens(lensKey), ArtifactKind.LearnMore, NoChapter, NoOrdinal, termNorm);
    }

    /// <summary>
    /// Rebuilds a key from stored columns. For the store's read path only —
    /// application code goes through the factories so it cannot invent a shape.
    /// </summary>
    internal static ArtifactKey FromRow(
        BookRef book, string lensKey, ArtifactKind kind, int chapter, int ordinal, string subkey) =>
        new(book, lensKey, kind, chapter, ordinal, subkey);

    // ─── Shapes ──────────────────────────────────────────────────────────

    private static ArtifactKey BookScoped(BookRef book, string lensKey, ArtifactKind kind) =>
        new(book, lensKey, kind, NoChapter, NoOrdinal, NoSubkey);

    private static ArtifactKey ChapterScoped(BookRef book, string lensKey, ArtifactKind kind, int chapter) =>
        new(book, lensKey, kind, RequireNonNegative(chapter, nameof(chapter)), NoOrdinal, NoSubkey);

    private static ArtifactKey Ordered(
        BookRef book, string lensKey, ArtifactKind kind, int chapter, int ordinal, string ordinalName) =>
        new(book, lensKey, kind,
            RequireNonNegative(chapter, "chapter"),
            RequireNonNegative(ordinal, ordinalName),
            NoSubkey);

    private static string Lens(string lensKey)
    {
        if (string.IsNullOrWhiteSpace(lensKey))
            throw new ArgumentException("Lens key is required.", nameof(lensKey));

        // A lens-scoped artifact stored under "none" would collide with the
        // lens-independent artifact of the same kind, which is precisely the
        // kind of silent overwrite this type exists to prevent.
        if (lensKey == ArtifactKinds.NoLens)
            throw new ArgumentException(
                $"'{ArtifactKinds.NoLens}' is reserved for lens-independent artifacts.", nameof(lensKey));

        return lensKey;
    }

    private static int RequireNonNegative(int value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name, value, "Must be zero or positive.");
}
