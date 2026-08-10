using AnnasArchive.API.Reader2.Domain;
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
