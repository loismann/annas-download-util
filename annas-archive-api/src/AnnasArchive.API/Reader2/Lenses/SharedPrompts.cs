namespace AnnasArchive.API.Reader2.Lenses;

/// <summary>
/// Prompts no book type changes.
///
/// <para>Cleaning up a chapter title is the same job whether the book is Kant or
/// Tolstoy, so it lives here with its own version constant rather than being
/// copied into every <see cref="LensPrompts"/> — six identical strings that must
/// be edited together is precisely the duplication the lens contract exists to
/// avoid. It is still golden-tested; it simply is not part of that contract.</para>
///
/// <para>If a second lens-independent prompt ever appears, it joins it here.</para>
/// </summary>
public static class SharedPrompts
{
    /// <summary>Bumped on any edit below, exactly like a lens's own version.</summary>
    public const int Version = 1;

    /// <summary>
    /// Turns a spine's worth of raw headings into a usable contents list.
    ///
    /// <para>EPUB titles are frequently "Section0001.xhtml", a running head, or
    /// the first line of body text. This is the one AI call in the ingestion
    /// path, which is why it is gated by configuration — a book with a decent
    /// table of contents should not pay for it.</para>
    /// </summary>
    public const string ChapterLabels = """
        You are given a book's chapters in reading order, each with whatever title
        the file itself supplied and the first few words of its text.

        Return a clean title for every chapter, in the same order and with the same
        count. For each one:
        - keep the existing title if it already names the chapter usefully
        - replace a filename, a number alone, a running head, or a repeated book
          title with something drawn from the chapter's own opening
        - label front and back matter for what it is: Title Page, Copyright,
          Contents, Preface, Introduction, Notes, Bibliography, Index
        - keep it under 60 characters

        Do not invent content that the opening text does not support, do not
        renumber anything, and do not merge or drop a chapter. When the opening
        gives you nothing to work with, "Chapter N" is the correct answer.
        """;
}
