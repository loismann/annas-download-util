using AnnasArchive.API.Reader2.Ai;

namespace AnnasArchive.API.Reader2.Epub;

/// <summary>
/// How one chapter divides up: chunks for the summary ladder's first tier, and
/// sections — groups of chunks — for the second tier and for the section the
/// reader can summarise on its own.
///
/// <para>Derived once and stored with <c>lens_key = 'none'</c>. It is pure
/// arithmetic over paragraph breaks with no model involved, so it is identical
/// for every book type and must survive a lens switch untouched — regenerating
/// it would also renumber sections under the reader, invalidating every section
/// summary and bookmark that names one.</para>
/// </summary>
public sealed record ChapterLayout(
    IReadOnlyList<SectionBoundary> Chunks,
    IReadOnlyList<SectionBoundary> Sections) : IVersionedArtifact<ChapterLayout>
{
    public static int SchemaVersion => 1;

    public static ChapterLayout For(string text, int chunkWords, int chunksPerSection)
    {
        if (chunksPerSection <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(chunksPerSection), chunksPerSection, "Must be positive.");

        var chunks = SectionChunker.Detect(text, chunkWords);

        var sections = chunks
            .Chunk(chunksPerSection)
            .Select(group => new SectionBoundary(group[0].Start, group[^1].End - group[0].Start))
            .ToArray();

        return new ChapterLayout(chunks, sections);
    }
}
