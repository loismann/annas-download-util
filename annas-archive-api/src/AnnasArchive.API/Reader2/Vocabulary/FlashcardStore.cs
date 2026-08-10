using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.API.Reader2.Vocabulary;

/// <summary>
/// The cards a reader saved for one book.
///
/// <para>Reader I has this and readers use it, so parity requires reproducing
/// it. Verified as reader-only before rebuilding: the quiz feature keeps its own
/// cards through <c>IQuizStorageService</c> and does not touch these, so there
/// is no shared state to preserve between the two.</para>
///
/// <para>One artifact holding every card rather than a row each: a book has tens
/// of them, they are always read together, and "clear them all" becomes a single
/// write. Book-scoped and lens-independent — a term worth remembering is worth
/// remembering whichever way the book is being read.</para>
/// </summary>
public sealed class FlashcardStore(IArtifactStore artifacts)
{
    private static readonly ArtifactVersions Versions =
        ArtifactVersions.Computed(Flashcards.SchemaVersion);

    public async Task<Flashcards> ListAsync(BookRef book, CancellationToken ct = default) =>
        (await artifacts.GetAsync<Flashcards>(ArtifactKey.Flashcards(book), Versions, ct))?.Content
        ?? Flashcards.Empty;

    /// <summary>Saves a card. Saving a term twice replaces it rather than duplicating it.</summary>
    public async Task<Flashcards> AddAsync(
        BookRef book, string term, string definition, CancellationToken ct = default)
    {
        if (TermNorm.Of(term).Length == 0)
            throw new ArgumentException("A term is required.", nameof(term));

        return await SaveAsync(
            book,
            (await ListAsync(book, ct)).With(
                new Flashcard(term.Trim(), definition.Trim(), DateTime.UtcNow)),
            ct);
    }

    public async Task<Flashcards> RemoveAsync(BookRef book, string term, CancellationToken ct = default) =>
        await SaveAsync(book, (await ListAsync(book, ct)).Without(term), ct);

    public Task<Flashcards> ClearAsync(BookRef book, CancellationToken ct = default) =>
        SaveAsync(book, Flashcards.Empty, ct);

    private async Task<Flashcards> SaveAsync(BookRef book, Flashcards cards, CancellationToken ct)
    {
        await artifacts.PutAsync(
            ArtifactKey.Flashcards(book), cards,
            ArtifactProvenance.Computed(Flashcards.SchemaVersion), ct);

        return cards;
    }
}
