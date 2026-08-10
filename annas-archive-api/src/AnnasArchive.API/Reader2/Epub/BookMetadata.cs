namespace AnnasArchive.API.Reader2.Epub;

/// <summary>
/// A book's title and authors, read from the EPUB itself.
///
/// <para>Needed at enrolment, which happens before ingestion — the shelf has to
/// say something other than the filename the moment a book is added. Kept apart
/// from <see cref="ChapterIndexBuilder"/> because this is the cheap read: open
/// the package, take the metadata, close it, without extracting a word of text.</para>
/// </summary>
public sealed record BookMetadata(string Title, IReadOnlyList<string> Authors)
{
    /// <summary>
    /// Reads metadata, falling back to the file name for a book that declares no
    /// title. Plenty do, and a shelf entry called "Untitled" helps nobody.
    /// </summary>
    /// <exception cref="EpubException">The file is not a readable EPUB.</exception>
    public static BookMetadata Read(Stream epub, string fileName)
    {
        using var package = EpubPackage.Open(epub);

        var title = package.Title?.Trim();

        return new BookMetadata(
            string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(fileName) : title,
            package.Authors);
    }
}
