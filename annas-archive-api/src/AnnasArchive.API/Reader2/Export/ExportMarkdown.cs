using System.Text;
using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.API.Reader2.Export;

/// <summary>
/// A book's generated work as one Markdown document.
///
/// <para>Reads through <see cref="IArtifactStore.ListAsync"/> rather than
/// per-chapter lookups: three queries for a whole book instead of three per
/// chapter, and the store already applies the version gates, so an export can
/// never contain output from wording that has since been replaced.</para>
/// </summary>
public static class ExportMarkdown
{
    public static async Task<string> BuildAsync(
        ReaderContext ctx, ChapterIndex index, IArtifactStore artifacts, CancellationToken ct)
    {
        var summaries = await ByChapterAsync(ArtifactKind.ChapterSummary, CallKind.ChapterSummary);
        var explanations = await ByChapterAsync(ArtifactKind.ExplainSimply, CallKind.ExplainSimply);
        var sections = await ByChapterAsync(
            ArtifactKind.SectionSummary, CallKind.SectionSummary, keepAll: true);

        var doc = new StringBuilder()
            .AppendLine($"# {index.Title}")
            .AppendLine()
            .AppendLine($"*{string.Join(", ", ctx.Book.Authors)}*")
            .AppendLine()
            .AppendLine($"Read as **{ctx.Lens.DisplayName}**. Exported {DateTime.UtcNow:yyyy-MM-dd}.")
            .AppendLine();

        foreach (var chapter in index.Chapters)
        {
            var parts = Parts(chapter.Id).ToArray();
            if (parts.Length == 0) continue;

            doc.AppendLine().AppendLine($"## {chapter.Title}").AppendLine();
            foreach (var (heading, body) in parts)
                doc.AppendLine($"### {heading}").AppendLine().AppendLine(body).AppendLine();
        }

        return doc.ToString();

        IEnumerable<(string Heading, string Body)> Parts(int chapterId)
        {
            if (summaries.TryGetValue(chapterId, out var summary))
                yield return ("Summary", summary[0]);

            if (explanations.TryGetValue(chapterId, out var plain))
                yield return ("In plain language", plain[0]);

            if (sections.TryGetValue(chapterId, out var perSection))
                for (var i = 0; i < perSection.Count; i++)
                    yield return ($"Section {i + 1}", perSection[i]);
        }

        // The version is per prompt, so each kind is read under its own rather than
        // under one shared number — which is what used to make an edit to any prompt
        // drop every other kind out of the export.
        async Task<Dictionary<int, List<string>>> ByChapterAsync(
            ArtifactKind kind, CallKind wording, bool keepAll = false)
        {
            var rows = await artifacts.ListAsync<Prose>(
                new ArtifactQuery(ctx.Ref, ctx.Lens.Key, kind),
                new ArtifactVersions(Prose.SchemaVersion, ctx.Lens.Versions[wording]), ct);

            var byChapter = new Dictionary<int, List<string>>();

            foreach (var row in rows)
            {
                if (!byChapter.TryGetValue(row.Key.Chapter, out var list))
                    byChapter[row.Key.Chapter] = list = [];

                if (keepAll || list.Count == 0) list.Add(row.Content.Markdown);
            }

            return byChapter;
        }
    }
}

/// <summary>Turning a book title into something a filesystem will accept.</summary>
public static class FileNames
{
    /// <summary>
    /// An allowlist, deliberately, rather than <c>Path.GetInvalidFileNameChars</c>.
    ///
    /// <para>That set is platform-dependent — on macOS it is <c>/</c> and NUL and
    /// nothing else — so a name built here would pass on the dev machine and
    /// produce <c>A/B: Test?.md</c> for a reader downloading it onto Windows.
    /// This filename crosses machines by definition; the rule has to be the same
    /// everywhere the code runs.</para>
    /// </summary>
    public static string Sanitize(string title)
    {
        var cleaned = new string(title
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray())
            .Trim('-');

        return cleaned.Length == 0 ? "book" : cleaned[..Math.Min(60, cleaned.Length)];
    }
}
