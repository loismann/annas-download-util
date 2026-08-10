using System.Text;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;
using Serilog;

namespace AnnasArchive.API.Reader2.Ai;

/// <summary>
/// Tidies a book's chapter titles with one fast-model call at ingestion.
///
/// <para>EPUB headings are frequently "Section0001.xhtml", a running head, or the
/// book's own title repeated forty times, and a contents list like that is
/// unusable. Reader I does the same thing; this is parity.</para>
///
/// <para><b>It is one of only two calls a reader does not click for</b>, so it is
/// disclosed rather than buried: once per book, on the explicit ingest request,
/// on the fast model, and switchable off with
/// <c>Reader2:ChapterLabels:Enabled</c>. Lens-independent, because tidying a
/// heading is the same job whatever the book is.</para>
/// </summary>
public sealed class ChapterLabeller(
    ArtifactGateway gateway,
    ChapterTextStore text,
    ModelCalls model,
    Reader2Options options)
{
    /// <summary>How much of a chapter the model needs to name it.</summary>
    private const int OpeningWords = 40;

    /// <summary>
    /// The index with tidied titles, or unchanged if labelling is off or fails.
    ///
    /// <para>Never throws. A book with awkward chapter names is still a readable
    /// book, and failing an ingest over cosmetics would be the wrong trade.</para>
    /// </summary>
    public async Task<ChapterIndex> ApplyAsync(
        ReaderContext ctx, ChapterIndex index, CancellationToken ct = default)
    {
        if (!options.ChapterLabelsEnabled || index.Chapters.Count == 0) return index;

        try
        {
            var labels = await gateway.GetOrGenerateAsync(
                ArtifactKey.ChapterLabels(ctx.Ref), ctx, SharedPrompts.Version,
                token => GenerateAsync(ctx, index, token), ct: ct);

            return Relabel(index, labels);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[reader2] Chapter labelling failed for {Book}; keeping the book's own titles", ctx.Ref);
            return index;
        }
    }

    /// <summary>
    /// The index with any labels already stored applied, generating nothing.
    ///
    /// <para>What the chapter list uses. Opening a book must never cost money, so
    /// this path has no way to reach a model at all — the generating path is
    /// <see cref="ApplyAsync"/>, and only the explicit ingest request calls it.</para>
    /// </summary>
    public async Task<ChapterIndex> StoredLabelsAsync(
        ReaderContext ctx, ChapterIndex index, CancellationToken ct = default)
    {
        var labels = await gateway.PeekAsync<ChapterLabels>(
            ArtifactKey.ChapterLabels(ctx.Ref), SharedPrompts.Version, ct);

        return labels is null ? index : Relabel(index, labels);
    }

    private Task<Produced<ChapterLabels>> GenerateAsync(
        ReaderContext ctx, ChapterIndex index, CancellationToken ct) =>
        model.AskSharedAsync(
            ctx, CallKind.ChapterLabels, SharedPrompts.ChapterLabels, Describe(ctx, index),
            answer => new ChapterLabels(Parse(answer, index.Chapters.Count)), ct);

    /// <summary>Each chapter's existing title and how it opens.</summary>
    private string Describe(ReaderContext ctx, ChapterIndex index)
    {
        var lines = new StringBuilder();

        foreach (var chapter in index.Chapters)
        {
            var opening = EpubTextExtractor.Slice(
                text.TryReadChapter(ctx.Ref, chapter.Id) ?? "", 0, OpeningWords)
                .Replace('\n', ' ');

            lines.AppendLine($"{chapter.Id + 1}. [{chapter.Title}] {opening}");
        }

        return lines.ToString();
    }

    /// <summary>
    /// Numbered lines back into titles.
    ///
    /// <para>A wrong count is rejected outright rather than partially applied:
    /// a model that dropped a line would otherwise shift every title after it by
    /// one, which is worse than the raw headings and much harder to notice.</para>
    /// </summary>
    private static IReadOnlyList<string> Parse(string answer, int expected)
    {
        var titles = answer
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.IndexOf('.') is var dot && dot > 0 && dot < 6
                ? line[(dot + 1)..].Trim()
                : line)
            .Where(title => title.Length > 0)
            .ToArray();

        if (titles.Length != expected)
            throw new ReaderAiException(
                $"Chapter labelling returned {titles.Length} titles for {expected} chapters.");

        return titles;
    }

    private static ChapterIndex Relabel(ChapterIndex index, ChapterLabels labels) =>
        labels.Titles.Count != index.Chapters.Count
            ? index
            : index with
            {
                Chapters = index.Chapters
                    .Select((c, i) => c with { Title = labels.Titles[i] })
                    .ToArray()
            };
}
