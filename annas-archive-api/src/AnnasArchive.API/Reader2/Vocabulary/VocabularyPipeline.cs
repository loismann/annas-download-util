using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.API.Reader2.Vocabulary;

/// <summary>
/// Definitions and deep dives — the facility every book type uses.
///
/// <para>Outside the lens contract because it is not a way of reading a book; a
/// reader wants "reification" explained whether the book is Adorno or a campaign
/// history. What the <i>lens</i> contributes is emphasis, passed as its public
/// name and description, not as prompt text.</para>
/// </summary>
public sealed class VocabularyPipeline(
    ArtifactGateway gateway,
    IReaderAiPipeline pipeline,
    ChapterTextStore text,
    VocabularyStore vocabulary,
    ModelCalls model)
{
    /// <summary>
    /// The hard words in one section, minus what this reader has already filed.
    /// </summary>
    /// <param name="force">Regenerate and overwrite rather than reading the cache.</param>
    public async Task<SectionVocabulary> ForSectionAsync(
        ReaderContext ctx, int chapter, int section, bool force = false, CancellationToken ct = default)
    {
        var stored = await gateway.GetOrGenerateAsync(
            ArtifactKey.SectionVocab(ctx.Ref, ctx.Lens.Key, chapter, section),
            ctx, SharedPrompts.Version,
            token => GenerateAsync(ctx, chapter, section, token),
            force, ct);

        // Filtered on read, not on write: the artifact is the household's, the
        // exclusion list is this reader's.
        return stored.Excluding(await vocabulary.FiledAsync(ctx.UserId, ct));
    }

    /// <summary>
    /// Every section of a chapter, in order.
    ///
    /// <para>Reuses the section path rather than adding a kind of its own, so a
    /// chapter's worth of vocabulary and a single section's are the same rows —
    /// asking for the chapter after a section costs only the sections that were
    /// missing.</para>
    /// </summary>
    public async Task<SectionVocabulary> ForChapterAsync(
        ReaderContext ctx, int chapter, IProgress<ProgressStep>? progress = null,
        bool force = false, CancellationToken ct = default)
    {
        var layout = await pipeline.LayoutAsync(ctx, chapter, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var terms = new List<Definition>();

        for (var i = 0; i < layout.Sections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressStep(
                "vocabulary", i + 1, layout.Sections.Count,
                $"Section {i + 1} of {layout.Sections.Count}…"));

            foreach (var term in (await ForSectionAsync(ctx, chapter, i, force, ct)).Terms)
                if (seen.Add(term.Norm)) terms.Add(term);
        }

        return new SectionVocabulary(terms);
    }

    /// <summary>
    /// The cached deep dive on one term.
    ///
    /// <para>Keyed by the normalised term and scoped to the lens, because what
    /// matters about a word differs by book type: intellectual history for the
    /// literary reading, doctrinal usage for the military one.</para>
    /// </summary>
    public Task<DeepDive> DeepDiveAsync(
        ReaderContext ctx, string term, string? passage = null,
        bool force = false, CancellationToken ct = default)
    {
        var norm = TermNorm.Of(term);
        if (norm.Length == 0) throw new ReaderAiException("Pick a word to look up.");

        return gateway.GetOrGenerateAsync(
            ArtifactKey.LearnMore(ctx.Ref, ctx.Lens.Key, norm),
            ctx, SharedPrompts.Version,
            token => model.AskSharedAsync(
                ctx, CallKind.LearnMore, SharedPrompts.LearnMore,
                Compose(ctx,
                    ("Term", term.Trim()),
                    ("Where it appears", passage?.Trim() ?? "(no passage given)")),
                html => new DeepDive(html), token),
            force, ct);
    }

    private async Task<Produced<SectionVocabulary>> GenerateAsync(
        ReaderContext ctx, int chapter, int section, CancellationToken ct)
    {
        var layout = await pipeline.LayoutAsync(ctx, chapter, ct);

        if (section < 0 || section >= layout.Sections.Count)
            throw new ReaderAiException($"This chapter has no section {section + 1}.");

        var bounds = layout.Sections[section];
        var body = EpubTextExtractor.Slice(
            await text.ReadChapterAsync(ctx.Ref, chapter, ct), bounds.Start, bounds.WordCount);

        // Known terms go into the input rather than being filtered out of the
        // output: the saving is the model's attention, not our post-processing.
        var known = await vocabulary.KnownAsync(ctx.UserId, ct);

        return await model.AskSharedAsync(
            ctx, CallKind.SectionVocab, SharedPrompts.SectionVocabulary,
            Compose(ctx,
                ("Already known", known.Count == 0 ? "(nothing yet)" : string.Join(", ", known)),
                ("Passage", body)),
            answer => new SectionVocabulary(Parse(answer)), ct);
    }

    /// <summary>
    /// Labelled blocks, with the book type named so the model knows what kind of
    /// word matters here.
    /// </summary>
    private static string Compose(ReaderContext ctx, params (string Label, string Body)[] blocks) =>
        string.Join("\n\n", new[]
            {
                $"## Reading this book as\n\n{ctx.Lens.DisplayName} — {ctx.Lens.Description}"
            }
            .Concat(blocks.Select(b => $"## {b.Label}\n\n{b.Body}")));

    /// <summary>
    /// <c>term — meaning</c>, one per line.
    ///
    /// <para>Lines that do not parse are dropped rather than kept as a term with
    /// no definition: a vocabulary list with a blank entry looks like a defect in
    /// the reader, where a slightly shorter list looks like nothing at all.</para>
    /// </summary>
    private static IReadOnlyList<Definition> Parse(string answer) =>
        answer
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(Separators, 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => new Definition(parts[0].TrimStart('-', '*', ' '), parts[1]))
            .DistinctBy(d => d.Norm, StringComparer.Ordinal)
            .ToArray();

    /// <summary>An em dash is asked for; the other two arrive anyway.</summary>
    private static readonly string[] Separators = [" — ", " – ", " - "];
}
