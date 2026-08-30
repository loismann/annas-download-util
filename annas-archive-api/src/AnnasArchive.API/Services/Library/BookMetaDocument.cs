namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Builds the metadata dictionary an enrichment pass starts from, and decides
/// what survives from the copy already on disk.
///
/// The interesting decision is what an enrichment pass is *allowed to lose*.
/// Every pass rewrites the whole <c>.meta.json</c>, so any field not carried
/// forward here is silently deleted from the file — which is how a person's
/// favourite, their rating, or their cull review would disappear because a
/// background scanner looked up a cover. Nothing in the ladder ever writes
/// those fields, so nothing in the ladder would ever put them back.
/// </summary>
public static class BookMetaDocument
{
    /// <param name="existing">The file on disk, or null for a book seen for the first time.</param>
    /// <param name="parsedTitle">Title guessed from the filename.</param>
    /// <param name="parsedAuthors">Authors guessed from the filename.</param>
    public static Dictionary<string, object?> Seed(
        ExistingMeta? existing,
        string? parsedTitle,
        string[]? parsedAuthors,
        string filePath,
        long fileSizeBytes)
    {
        var extension = Path.GetExtension(filePath);
        var rawBaseName = Path.GetFileNameWithoutExtension(filePath);

        // A filename is better evidence than a stored title when the stored one
        // is the raw basename — that is a title nobody chose, just one nothing
        // has improved on yet.
        var title = LibraryMetadataRules.ShouldUseParsedTitle(existing?.Title, parsedTitle, rawBaseName)
            ? parsedTitle
            : existing?.Title;

        var authors = existing?.Authors is { Length: > 0 } storedAuthors
            ? storedAuthors
            : parsedAuthors;

        return new Dictionary<string, object?>
        {
            // Derived fresh from the file every pass: the file is the authority
            // on its own size, name and format.
            ["format"] = extension.TrimStart('.').ToUpperInvariant(),
            ["fileSize"] = LibraryMetadataRules.FormatFileSize(fileSizeBytes),
            ["fileName"] = Path.GetFileName(filePath),

            // Best guess so far. The ladder overwrites these when it finds
            // better; rawBaseName is the floor, never null.
            ["title"] = title ?? parsedTitle ?? rawBaseName,
            ["authors"] = authors ?? parsedAuthors ?? Array.Empty<string>(),

            // Enrichment output from previous passes, kept so a pass that fails
            // to reach a source does not erase what an earlier one found.
            ["coverUrl"] = existing?.CoverUrl,
            ["series"] = existing?.Series,
            ["publishedDate"] = existing?.PublishedDate,
            ["pages"] = existing?.Pages,
            ["description"] = existing?.Description,
            ["primaryGenre"] = existing?.PrimaryGenre,
            ["genres"] = existing?.Genres ?? Array.Empty<string>(),
            ["goodreadsRating"] = existing?.GoodreadsRating,
            ["openLibraryConfidence"] = existing?.OpenLibraryConfidence,
            ["aiEnrichedAt"] = existing?.AiEnrichedAt,
            ["enrichmentComplete"] = existing?.EnrichmentComplete ?? false,

            // Provenance, set once when the book arrived.
            ["source"] = existing?.Source ?? "library",
            ["md5"] = existing?.Md5,
            ["savedAt"] = existing?.SavedAt ?? DateTime.UtcNow.ToString("o"),

            // A person typed these. Carried forward here so this rewrite does
            // not drop them, and re-read from disk again immediately before the
            // write in case they were edited during the pass.
            ["tags"] = existing?.Tags ?? Array.Empty<string>(),
            ["personalRating"] = existing?.PersonalRating,
            ["favoritedBy"] = existing?.FavoritedBy ?? Array.Empty<string>(),
            ["cullReviewedAt"] = existing?.CullReviewedAt?.ToString("o")
        };
    }
}
