using AnnasArchive.API.Reader2.Ai;

namespace AnnasArchive.API.Reader2.Vocabulary;

/// <summary>One term and what it means here.</summary>
public sealed record Definition(string Term, string Meaning)
{
    /// <summary>Normalised once so filtering and lookup never re-derive it.</summary>
    public string Norm => TermNorm.Of(Term);
}

/// <summary>
/// The hard words in one section.
///
/// <para>Stored whole and filtered on read rather than at generation time: the
/// artifact describes the passage and is shared across the household, while what
/// each reader already knows is theirs. Filtering before storage would bake one
/// person's vocabulary into everybody's copy.</para>
/// </summary>
public sealed record SectionVocabulary(IReadOnlyList<Definition> Terms)
    : IVersionedArtifact<SectionVocabulary>
{
    public static int SchemaVersion => 1;

    /// <summary>What this reader has not already dealt with.</summary>
    public SectionVocabulary Excluding(IReadOnlySet<string> filedNorms) =>
        new(Terms.Where(t => !filedNorms.Contains(t.Norm)).ToArray());
}

/// <summary>
/// A cached deep dive on one term.
///
/// <para>HTML because it renders straight into the reader's panel, and cached
/// because Reader I regenerates and bills for one every single time somebody
/// asks — so the second reader to wonder about <i>reification</i> pays nothing.
/// </para>
/// </summary>
public sealed record DeepDive(string Html) : IVersionedArtifact<DeepDive>
{
    public static int SchemaVersion => 1;
}

/// <summary>A card the reader saved for a book.</summary>
public sealed record Flashcard(string Term, string Definition, DateTime AddedAtUtc)
{
    public string Norm => TermNorm.Of(Term);
}

/// <summary>
/// Every card saved for one book.
///
/// <para>One artifact rather than a row per card: a book has tens of these, they
/// are always read together, and a single row makes "clear them all" a delete
/// rather than a query. Book-scoped and lens-independent — a term worth
/// remembering is worth remembering whichever way the book is being read.</para>
/// </summary>
public sealed record Flashcards(IReadOnlyList<Flashcard> Cards) : IVersionedArtifact<Flashcards>
{
    public static int SchemaVersion => 1;

    public static readonly Flashcards Empty = new([]);

    /// <summary>Adds a card, or replaces the one already there for that term.</summary>
    public Flashcards With(Flashcard card) =>
        new([.. Cards.Where(c => c.Norm != card.Norm), card]);

    public Flashcards Without(string term) =>
        new(Cards.Where(c => c.Norm != TermNorm.Of(term)).ToArray());
}
