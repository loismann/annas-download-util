using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Service for extracting metadata from ebook files (EPUB, etc).
/// </summary>
public class MetadataExtractionService : IMetadataExtractionService
{
    /// <inheritdoc />
    public async Task<EpubExtractedMetadata?> ExtractEpubMetadataAsync(
        string filePath,
        string libraryRoot,
        bool skipCoverIfLocalExists,
        CancellationToken token)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var containerEntry = zip.GetEntry("META-INF/container.xml");
            if (containerEntry == null)
                return null;

            using var containerStream = containerEntry.Open();
            var containerDoc = await XDocument.LoadAsync(containerStream, LoadOptions.None, token);
            var rootfile = containerDoc
                .Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "rootfile")
                ?.Attribute("full-path")
                ?.Value;

            if (string.IsNullOrWhiteSpace(rootfile))
                return null;

            var opfEntry = zip.GetEntry(rootfile);
            if (opfEntry == null)
                return null;

            using var opfStream = opfEntry.Open();
            var opfDoc = await XDocument.LoadAsync(opfStream, LoadOptions.None, token);

            var metadata = opfDoc.Descendants().FirstOrDefault(x => x.Name.LocalName == "metadata");
            if (metadata == null)
                return null;

            // Extract title
            var title = metadata.Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "title")?.Value?.Trim();

            // Extract authors
            var authors = metadata.Descendants()
                .Where(x => x.Name.LocalName == "creator")
                .Select(x => x.Value.Trim())
                .Where(x => x.Length > 0)
                .Distinct()
                .ToArray();

            // Extract date
            var date = metadata.Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "date")?.Value?.Trim();

            // Extract page count (Calibre metadata)
            var pageCount = metadata.Descendants()
                .Where(x => x.Name.LocalName == "meta")
                .FirstOrDefault(x => string.Equals(x.Attribute("name")?.Value, "calibre:page_count", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("content")
                ?.Value;

            // Extract cover
            string? coverUrl = null;
            if (!skipCoverIfLocalExists)
            {
                coverUrl = await ExtractEpubCoverAsync(zip, opfDoc, metadata, rootfile, filePath, libraryRoot, token);
            }

            return new EpubExtractedMetadata(
                title,
                authors.Length > 0 ? authors : null,
                date,
                pageCount,
                coverUrl);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ExtractEpubCoverAsync(
        ZipArchive zip,
        XDocument opfDoc,
        XElement metadata,
        string rootfile,
        string filePath,
        string libraryRoot,
        CancellationToken token)
    {
        // Find cover ID from metadata
        var coverId = metadata.Descendants()
            .Where(x => x.Name.LocalName == "meta")
            .FirstOrDefault(x => string.Equals(x.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("content")
            ?.Value;

        var manifest = opfDoc.Descendants().FirstOrDefault(x => x.Name.LocalName == "manifest");
        var opfDir = Path.GetDirectoryName(rootfile)?.Replace('\\', '/') ?? "";

        string? href = null;
        if (!string.IsNullOrWhiteSpace(coverId))
        {
            href = manifest?
                .Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "item" &&
                    string.Equals(x.Attribute("id")?.Value, coverId, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("href")
                ?.Value;
        }

        if (string.IsNullOrWhiteSpace(href))
        {
            href = manifest?
                .Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "item" &&
                    (x.Attribute("properties")?.Value?.Contains("cover-image", StringComparison.OrdinalIgnoreCase) ?? false))
                ?.Attribute("href")
                ?.Value;
        }

        if (!string.IsNullOrWhiteSpace(href))
        {
            return await TryExtractCoverAsync(zip, opfDir, href, filePath, libraryRoot, token);
        }
        else
        {
            return await TryExtractLargestImageAsync(zip, filePath, libraryRoot, token);
        }
    }

    private async Task<string?> TryExtractCoverAsync(
        ZipArchive zip,
        string opfDir,
        string href,
        string filePath,
        string libraryRoot,
        CancellationToken token)
    {
        var coverPath = string.IsNullOrEmpty(opfDir) ? href : $"{opfDir}/{href}";
        var coverEntry = zip.GetEntry(coverPath);
        if (coverEntry == null)
            return null;

        var coverExt = Path.GetExtension(href);
        if (string.IsNullOrWhiteSpace(coverExt))
            coverExt = ".jpg";

        var coverFileName = $"{Path.GetFileName(filePath)}.cover{coverExt}";
        var coverDiskPath = Path.Combine(libraryRoot, "_covers", coverFileName);

        Directory.CreateDirectory(Path.GetDirectoryName(coverDiskPath)!);
        await using var coverStream = coverEntry.Open();
        await using var outStream = File.Create(coverDiskPath);
        await coverStream.CopyToAsync(outStream, token);

        return $"_covers/{coverFileName}";
    }

    private async Task<string?> TryExtractLargestImageAsync(
        ZipArchive zip,
        string filePath,
        string libraryRoot,
        CancellationToken token)
    {
        var imageEntries = zip.Entries
            .Where(e =>
                e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .ToList();

        var largest = imageEntries.FirstOrDefault();
        if (largest == null)
            return null;

        var coverExt = Path.GetExtension(largest.FullName);
        if (string.IsNullOrWhiteSpace(coverExt))
            coverExt = ".jpg";

        var coverFileName = $"{Path.GetFileName(filePath)}.cover{coverExt}";
        var coverDiskPath = Path.Combine(libraryRoot, "_covers", coverFileName);

        Directory.CreateDirectory(Path.GetDirectoryName(coverDiskPath)!);
        await using var coverStream = largest.Open();
        await using var outStream = File.Create(coverDiskPath);
        await coverStream.CopyToAsync(outStream, token);

        return $"_covers/{coverFileName}";
    }

    /// <inheritdoc />
    public async Task<string?> ExtractSdrCoverAsync(string filePath, string libraryRoot, CancellationToken token)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath) ?? "";
            var sdrPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(filePath) + ".sdr");
            if (!Directory.Exists(sdrPath))
                return null;

            var candidates = Directory.GetFiles(sdrPath, "*.*", SearchOption.AllDirectories)
                .Where(path =>
                {
                    var ext = Path.GetExtension(path);
                    return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (candidates.Count == 0)
                return null;

            var best = PickBestCover(candidates);
            var coverExt = Path.GetExtension(best);
            if (string.IsNullOrWhiteSpace(coverExt))
                coverExt = ".jpg";

            var coverFileName = $"{Path.GetFileName(filePath)}.cover{coverExt}";
            var coverDiskPath = Path.Combine(libraryRoot, "_covers", coverFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(coverDiskPath)!);

            await using var source = File.OpenRead(best);
            await using var dest = File.Create(coverDiskPath);
            await source.CopyToAsync(dest, token);

            return $"_covers/{coverFileName}";
        }
        catch
        {
            return null;
        }
    }

    private static string PickBestCover(IEnumerable<string> files)
    {
        var fileList = files.ToList();
        var coverMatch = fileList.FirstOrDefault(path =>
            Path.GetFileName(path).Contains("cover", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(coverMatch))
            return coverMatch;

        return fileList
            .OrderByDescending(path => new FileInfo(path).Length)
            .First();
    }

    /// <inheritdoc />
    public (string? Title, string[]? Authors) ParseTitleAuthorFromFileName(string filePath)
    {
        var raw = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(raw))
            return (null, null);

        // Strip bracketed content (e.g., [homes_pferrer], [retail], [Wings of Fire 10])
        var cleaned = Regex.Replace(raw, @"\[[^\]]*\]", "").Trim();
        // Strip parenthesized prefixes at start (e.g., "(epub)" but not "(Unabridged)" in title)
        cleaned = Regex.Replace(cleaned, @"^\([^)]*\)\s*", "").Trim();
        // Strip leading numbers with separators (e.g., "1 - ", "002 - ", "10.")
        cleaned = Regex.Replace(cleaned, @"^\d{1,3}\s*[-–—.]\s*", "").Trim();
        // Strip leading dash separators left after bracket removal (e.g., "- Title" from "[tag] - Title")
        cleaned = Regex.Replace(cleaned, @"^[-–—]\s*", "").Trim();

        cleaned = Regex.Replace(cleaned, @"_(?:sample|preview)$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\.(tmp|tmp\d+)_\w+$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"_[A-Z0-9]{8,}$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\bB0[A-Z0-9]{8,}\b$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = cleaned.Replace('_', ' ').Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");

        var separators = new[] { " - ", " – ", " — " };
        foreach (var sep in separators)
        {
            var idx = cleaned.IndexOf(sep, StringComparison.Ordinal);
            if (idx <= 0 || idx >= cleaned.Length - sep.Length)
                continue;

            var left = cleaned[..idx].Trim();
            var right = cleaned[(idx + sep.Length)..].Trim();
            if (left.Length < 3 || right.Length < 3)
                continue;

            var looksLikeAuthor = left.Contains(',') || left.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
            if (looksLikeAuthor)
                return (right, new[] { left });
        }

        return (cleaned, null);
    }
}
