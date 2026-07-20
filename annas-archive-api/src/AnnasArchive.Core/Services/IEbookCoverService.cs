using System.IO;
using System.Threading.Tasks;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for replacing cover images in ebook files (EPUB, MOBI, AZW3, etc.)
/// </summary>
public interface IEbookCoverService
{
    /// <summary>
    /// Replaces the cover image in an ebook file with a new cover from a URL.
    /// </summary>
    /// <param name="ebookStream">The original ebook file stream</param>
    /// <param name="coverUrl">URL of the new cover image</param>
    /// <param name="format">The ebook format (e.g., "epub", "mobi", "pdf")</param>
    /// <returns>A new stream with the updated cover, or the original stream if cover replacement is not supported/failed</returns>
    Task<Stream> ReplaceCoverAsync(Stream ebookStream, string coverUrl, string format);

    /// <summary>
    /// Checks if cover replacement is supported for the given format.
    /// </summary>
    /// <param name="format">The ebook format (e.g., "epub", "mobi", "pdf")</param>
    /// <returns>True if cover replacement is supported, false otherwise</returns>
    bool IsFormatSupported(string format);
}
