using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Resolving one path inside an EPUB against another, and reading a chapter title
/// out of the HTML at the end of it.
///
/// An EPUB is a zip whose manifest points at entries by relative href, and the two
/// halves rarely agree exactly: hrefs arrive URL-encoded, with backslashes, with a
/// <c>#fragment</c>, cased differently to the entry they name, or relative to the OPF's
/// own directory rather than the archive root. Every one of those mismatches produces
/// the same symptom — a chapter that silently comes back empty — which is why this is
/// worth having somewhere it can be exercised directly.
///
/// These were private statics inside <see cref="EpubChapterCache"/>, a 840-line file
/// whose other half does file I/O, HTTP and zip repair. Nothing here touches any of
/// that: given a string, each returns a string.
/// </summary>
public static class EpubZipPaths
{
    /// <summary>
    /// A zip entry path in the one form the rest of the code compares against:
    /// forward slashes, no leading slash, no URL fragment.
    /// </summary>
    public static string NormalizeZipPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/').TrimStart('/');
        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
            normalized = normalized[..fragmentIndex];
        return normalized;
    }

    /// <summary>As <see cref="NormalizeZipPath"/>, with no trailing slash, so it can be
    /// concatenated with a child path without producing a double slash.</summary>
    public static string NormalizeZipDir(string path)
    {
        var normalized = NormalizeZipPath(path);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized.TrimEnd('/');
    }

    /// <summary>
    /// An href from the OPF manifest, resolved against the OPF's own directory.
    /// Hrefs are URL-encoded in the manifest — a chapter called "Part 1.xhtml" appears
    /// as "Part%201.xhtml" — so decoding first is what makes it match a real entry.
    /// </summary>
    public static string ResolveOpfHref(string opfDir, string href)
    {
        var decoded = Uri.UnescapeDataString(href);
        decoded = decoded.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(opfDir))
            return decoded;
        return $"{opfDir}/{decoded}";
    }

    /// <summary>
    /// The entry an href refers to, or null.
    ///
    /// Exact match first; failing that, any entry ending with the href. The suffix
    /// fallback is what makes a manifest that omits the container directory still
    /// resolve, and it is deliberately case-insensitive because zip entries and
    /// manifest hrefs disagree on case often enough to matter.
    /// </summary>
    public static string? FindEntry(Dictionary<string, byte[]> entries, string href)
    {
        var normalized = NormalizeZipPath(href);
        if (entries.ContainsKey(normalized))
            return normalized;

        return entries.Keys.FirstOrDefault(key =>
            key.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether an entry is a document rather than an image, font or stylesheet.</summary>
    public static bool IsHtmlEntry(string path) =>
        path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes entry bytes, honouring a byte-order mark when one is present.</summary>
    public static string ReadTextFromBytes(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A chapter's display title. <c>&lt;title&gt;</c> first because that is what the
    /// format is for, then the first heading — many EPUBs ship an empty or boilerplate
    /// title element and carry the real one in the body.
    /// </summary>
    public static string? ExtractTitleFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        foreach (var tag in new[] { "title", "h1", "h2" })
        {
            var match = Regex.Match(
                html, $@"<{tag}[^>]*>(?<t>.*?)</{tag}>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (match.Success)
            {
                var title = WebUtility.HtmlDecode(match.Groups["t"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
        }

        return null;
    }

    /// <summary>
    /// The entry path out of an EPUB reader's "file … was not found" exception.
    ///
    /// The message is the only place the missing path appears, so this drives the
    /// repair path that rebuilds the archive with the entry stubbed in. Quoting style
    /// varies, including curly quotes, hence more than one pattern; the OEBPS fallback
    /// catches messages that name a path without any quoting at all.
    /// </summary>
    public static string? ExtractMissingEpubPath(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        string[] patterns =
        [
            "file\\s+[\"“”'](?<path>[^\"“”']+)[\"“”']\\s+was not found",
            "file\\s+(?<path>[^\\s]+)\\s+was not found"
        ];

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["path"].Value;
        }

        var fallback = Regex.Match(message, @"(?<path>OEBPS/[^""\s]+)", RegexOptions.IgnoreCase);
        return fallback.Success ? fallback.Groups["path"].Value : null;
    }
}
