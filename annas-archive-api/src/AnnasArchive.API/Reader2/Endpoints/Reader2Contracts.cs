using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>
/// A book type, as the picker sees it.
///
/// <para>Note what is absent: <c>PromptVersion</c> and the prompts themselves.
/// Prompts are the product and never leave the server, and a version the client
/// can read is a version the client will eventually branch on. A test asserts
/// no prompt text appears in this response.</para>
/// </summary>
public sealed record LensResponse(
    string Key,
    string DisplayName,
    string Description,
    string Icon,
    int SortOrder,
    bool IsDefault,
    bool BuildsStoryModel,
    StoryVocabulary? StoryVocabulary)
{
    public static LensResponse From(IReaderLens lens, bool isDefault) => new(
        lens.Key, lens.DisplayName, lens.Description, lens.Icon, lens.SortOrder,
        isDefault, lens.BuildsStoryModel, lens.StoryVocabulary);
}

/// <summary>One shelf entry.</summary>
public sealed record BookResponse(
    string BookId,
    string FileName,
    string Title,
    IReadOnlyList<string> Authors,
    string LensKey,
    DateTime AddedAtUtc,
    DateTime? LastOpenedAtUtc,
    bool IsAvailable)
{
    public static BookResponse From(EnrolledBook book) => new(
        book.Book.Value, book.FileName, book.Title, book.Authors, book.LensKey,
        book.AddedAtUtc, book.LastOpenedAtUtc, book.IsAvailable);
}

/// <summary>Enrol a library book. <c>LensKey</c> omitted means the default type.</summary>
public sealed record EnrolBookRequest(string? FileName, string? LensKey);

/// <summary>Change a book's type.</summary>
public sealed record SetLensRequest(string? LensKey);

/// <summary>One chapter as the reader's navigation sees it.</summary>
/// <param name="HasSummary">
/// Whether a summary for this chapter under this lens is already stored.
///
/// <para>Served because the client cannot work it out: an artifact is keyed by
/// lens and prompt version, so "have I paid for this already" is a question only
/// the store can answer. Without it the chapter list has to either show nothing
/// or ask per chapter, and a reader with no way to see what is already bought
/// buys it twice.</para>
/// </param>
public sealed record ChapterInfo(int Id, string Title, int Level, int WordCount, bool HasSummary = false)
{
    public static ChapterInfo From(Chapter chapter, bool hasSummary = false) =>
        new(chapter.Id, chapter.Title, chapter.Level, chapter.WordCount, hasSummary);

    /// <summary>
    /// A whole contents list, told which chapters are already summarised.
    ///
    /// <para>One query for the book rather than one per chapter — the chapter
    /// list is the most-hit route in the reader, and a per-chapter lookup would
    /// make opening a three-hundred-chapter novel three hundred reads.</para>
    /// </summary>
    public static IReadOnlyList<ChapterInfo> ForList(
        IReadOnlyList<Chapter> chapters, IReadOnlySet<int> summarised) =>
        [.. chapters.Select(c => From(c, summarised.Contains(c.Id)))];
}

/// <summary>
/// A book's contents. Carries the lens key so the client cannot render a
/// chapter list under one book type and its summaries under another.
/// </summary>
public sealed record ChapterListResponse(string Title, string LensKey, IReadOnlyList<ChapterInfo> Chapters);

/// <summary>One chapter, with its text.</summary>
public sealed record ChapterResponse(ChapterInfo Chapter, string Text);

/// <summary>Where one summarisable section of a chapter begins and ends, in words.</summary>
public sealed record SectionInfo(int Index, int StartWord, int WordCount);

/// <summary>Save where the reader has got to.</summary>
public sealed record SetPositionRequest(int Chapter, int WordOffset);

/// <summary>Mark a place. A second save at the same place re-labels the mark already there.</summary>
public sealed record SaveBookmarkRequest(int Chapter, int WordOffset, string? Label);

/// <summary>Explain a passage the reader selected.</summary>
public sealed record PassageAnalysisRequest(int Chapter, int WordOffset, string? Text);

/// <summary>File a term, or move one between known and studying.</summary>
public sealed record SaveTermRequest(string? Term, string? State, string? Definition, string? BookId);

/// <summary>Ask for the deep dive behind a term.</summary>
/// <param name="Context">The passage it appeared in, when there is one.</param>
public sealed record LearnMoreRequest(string? Term, string? Context);

/// <summary>Save a card for this book.</summary>
public sealed record FlashcardRequest(string? Term, string? Definition);

/// <summary>Fold one chapter into the story model.</summary>
public sealed record IngestChapterRequest(int Chapter);

/// <summary>Answer one of the merger's questions. <c>Accept</c> false means "leave them apart".</summary>
public sealed record ResolveMergeRequest(bool Accept);

/// <summary>
/// The story model, as far as the reader has read.
/// </summary>
/// <param name="Vocabulary">
/// What this book type calls the three parts — Characters or Commanders &amp;
/// Units, Factions or Belligerents. The client labels its columns from this
/// rather than holding a table of its own, which is what keeps a fourth book
/// type free of frontend changes.
/// </param>
/// <param name="OpenQuestions">
/// Candidate merges the reader has not answered. Declined ones are kept in
/// storage so the same ambiguity is not raised twice, and are never served.
/// </param>
/// <param name="ThroughChapter">
/// The horizon this response was filtered to, echoed back so a client cannot
/// display a model against the wrong reading position.
/// </param>
public sealed record StoryModelResponse(
    IReadOnlyList<Story.Actor> Actors,
    IReadOnlyList<Story.Group> Groups,
    IReadOnlyList<Story.Edge> Edges,
    IReadOnlyList<Story.StoryThread> Threads,
    IReadOnlyList<Story.CandidateMerge> OpenQuestions,
    IReadOnlyList<int> ChaptersIngested,
    Lenses.StoryVocabulary Vocabulary,
    int ThroughChapter)
{
    public static StoryModelResponse From(Story.StoryModel model, Lenses.IReaderLens lens, int throughChapter) =>
        new(model.Actors, model.Groups, model.Edges, model.Threads,
            [.. model.CandidateMerges.Where(m => !m.Declined)],
            model.ChaptersIngested,
            lens.StoryVocabulary ?? new Lenses.StoryVocabulary("Actors", "Groups", "Threads"),
            throughChapter);
}
