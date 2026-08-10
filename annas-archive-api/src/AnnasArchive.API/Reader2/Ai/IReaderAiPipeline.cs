using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;

namespace AnnasArchive.API.Reader2.Ai;

/// <summary>A passage the reader selected, located by where it starts.</summary>
/// <param name="WordOffset">
/// Words from the start of the chapter. The same unit as reading position,
/// bookmarks, and search hits, so all four can be compared without conversion.
/// </param>
public sealed record PassageRequest(int Chapter, int WordOffset, string Text);

/// <summary>
/// Everything Reader II asks a model for, parameterised by lens.
///
/// <para>There is no <c>switch</c> on a book type anywhere below this interface.
/// The lens supplies wording, configuration supplies budgets, and the ladder is
/// the same shape for every type — which is what makes a fourth book type free.</para>
/// </summary>
public interface IReaderAiPipeline
{
    /// <summary>
    /// Explains one selected passage, in the context of what has already been
    /// explained earlier in the same chapter.
    /// </summary>
    Task<Prose> AnalysePassageAsync(
        ReaderContext ctx, PassageRequest request, bool force = false, CancellationToken ct = default);

    /// <summary>Summarises one section on its own.</summary>
    Task<Prose> SummariseSectionAsync(
        ReaderContext ctx, int chapter, int section, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Summarises a whole chapter through the three-tier ladder, or in one call
    /// when the chapter is short enough that the ladder would cost more than it
    /// is worth.
    /// </summary>
    Task<Prose> SummariseChapterAsync(
        ReaderContext ctx, int chapter, IProgress<ProgressStep>? progress = null,
        bool force = false, CancellationToken ct = default);

    /// <summary>
    /// The plain-language retelling, written from the chapter summary rather than
    /// the chapter — so it is a re-explanation of an explanation, which is what
    /// makes it read as a person talking rather than a second summary.
    /// </summary>
    Task<Prose> ExplainSimplyAsync(
        ReaderContext ctx, int chapter, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// How a chapter divides into chunks and sections. Computed, never billed,
    /// and stored lens-independently.
    /// </summary>
    Task<ChapterLayout> LayoutAsync(ReaderContext ctx, int chapter, CancellationToken ct = default);
}
