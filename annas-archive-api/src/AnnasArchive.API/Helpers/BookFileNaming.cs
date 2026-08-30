using AnnasArchive.Core.Helpers;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// What a downloaded book is called on disk.
///
/// <para>Anna's and LibGen had this same rule written out twice — sanitise the
/// title, take the extension from the URL, fall back to the content type — and
/// the two copies had already drifted: Anna's inlined a four-arm switch while
/// LibGen's had five arms and handled a null download URL. A book fetched from
/// the fallback source would otherwise have been named by whichever copy the
/// call happened to land in.</para>
///
/// <para>The title is untrusted: it comes from a third-party catalogue and ends
/// up as a filename, so it goes through <see cref="SafeFileName.ForUserInput"/>
/// with the md5 as the fallback.</para>
/// </summary>
public static class BookFileNaming
{
    /// <summary>
    /// The extension to save under. The URL wins when it carries one, because it
    /// reflects the actual file; content type is the fallback for the many mirror
    /// links that end in an opaque id.
    /// </summary>
    public static string Extension(string? downloadUrl, HttpResponseMessage response)
    {
        var fromUrl = !string.IsNullOrEmpty(downloadUrl) && Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath)
            : string.Empty;

        if (!string.IsNullOrEmpty(fromUrl))
            return fromUrl;

        return response.Content.Headers.ContentType?.MediaType switch
        {
            "application/pdf" => ".pdf",
            "application/epub+zip" => ".epub",
            "application/x-mobipocket-ebook" => ".mobi",
            "application/vnd.amazon.ebook" => ".azw3",
            _ => ".bin"
        };
    }

    /// <summary>The sanitised title, the extension, and the two joined.</summary>
    public static (string SafeTitle, string Ext, string FileName) For(
        string? title, string md5, string? downloadUrl, HttpResponseMessage response)
    {
        var rawTitle = !string.IsNullOrWhiteSpace(title) ? title : md5;
        var safeTitle = SafeFileName.ForUserInput(rawTitle, fallback: md5);
        var ext = Extension(downloadUrl, response);

        return (safeTitle, ext, $"{safeTitle}{ext}");
    }
}
