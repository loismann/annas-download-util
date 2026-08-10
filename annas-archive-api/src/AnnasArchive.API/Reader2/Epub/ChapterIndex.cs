namespace AnnasArchive.API.Reader2.Epub;

/// <summary>One chapter, as the reader sees it.</summary>
/// <param name="Id">Position in the spine. Stable for a given EPUB, and the
/// number every artifact key uses as its chapter.</param>
/// <param name="Level">TOC nesting depth; 0 for a top-level chapter.</param>
/// <param name="Source">In-archive path, kept so a re-extract needs no re-parse.</param>
public sealed record Chapter(
    int Id,
    string Title,
    int Level,
    int WordCount,
    string Source);

/// <summary>
/// A book's chapters in reading order. Stored as the <c>chapter-index</c>
/// artifact, which is lens-independent — the structure of a book does not
/// change with how it is being read.
/// </summary>
public sealed record ChapterIndex(string Title, IReadOnlyList<Chapter> Chapters)
{
    /// <summary>Bumped when this shape changes incompatibly (see <c>ArtifactProvenance</c>).</summary>
    public const int CurrentSchemaVersion = 1;

    public int TotalWords => Chapters.Sum(c => c.WordCount);

    public Chapter? Find(int chapterId) => Chapters.FirstOrDefault(c => c.Id == chapterId);
}
