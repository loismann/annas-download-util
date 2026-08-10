namespace AnnasArchive.API.Reader2.Epub;

/// <summary>
/// Path arithmetic for paths <i>inside</i> a zip, which are not filesystem paths:
/// always forward-slashed, always relative to the archive root, and never touched
/// by <c>Path.Combine</c>'s platform separators.
///
/// <para>Its own type because EPUB hrefs are relative to the package file rather
/// than the archive root, so "resolve this href" is a real operation that three
/// callers need and none should re-derive.</para>
/// </summary>
internal static class ZipPath
{
    /// <summary>Forward slashes, no leading slash, and <c>..</c> and <c>.</c> resolved.</summary>
    public static string Normalize(string path)
    {
        var segments = new List<string>();

        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>The directory part, or empty when the file sits at the archive root.</summary>
    public static string DirectoryOf(string path)
    {
        var normalized = Normalize(path);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized[..slash];
    }

    /// <summary>
    /// Resolves an href against a directory. A fragment (<c>#section-2</c>) is
    /// dropped — the TOC uses them to point inside a file, but the file is the
    /// unit we extract.
    /// </summary>
    public static string Combine(string directory, string href)
    {
        var withoutFragment = href.Split('#')[0];
        if (withoutFragment.Length == 0) return Normalize(directory);

        return Normalize(directory.Length == 0 ? withoutFragment : $"{directory}/{withoutFragment}");
    }
}
