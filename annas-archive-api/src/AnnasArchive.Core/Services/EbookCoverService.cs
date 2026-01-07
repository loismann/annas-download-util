using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for replacing cover images in ebook files using SharpZipLib for full ZIP metadata control
/// </summary>
public class EbookCoverService : IEbookCoverService
{
    private readonly HttpClient _httpClient;
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "epub", ".epub"
    };

    public EbookCoverService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public bool IsFormatSupported(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return false;

        var normalizedFormat = format.TrimStart('.').ToLowerInvariant();
        return SupportedFormats.Contains(normalizedFormat);
    }

    /// <inheritdoc/>
    public async Task<Stream> ReplaceCoverAsync(Stream ebookStream, string coverUrl, string format)
    {
        if (!IsFormatSupported(format))
        {
            Console.WriteLine($"[EbookCoverService] Format '{format}' not supported for cover replacement");
            return ebookStream;
        }

        var normalizedFormat = format.TrimStart('.').ToLowerInvariant();

        try
        {
            if (!ebookStream.CanSeek)
            {
                var buffered = new MemoryStream();
                await ebookStream.CopyToAsync(buffered);
                buffered.Position = 0;
                ebookStream = buffered;
            }

            if (normalizedFormat == "epub")
            {
                return await ReplaceEpubCoverAsync(ebookStream, coverUrl);
            }

            Console.WriteLine($"[EbookCoverService] Format '{format}' not implemented");
            return ebookStream;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EbookCoverService] Failed to replace cover: {ex.Message}");

            // Reset stream position if possible
            if (ebookStream.CanSeek)
            {
                ebookStream.Position = 0;
            }

            return ebookStream;
        }
    }

    private async Task<Stream> ReplaceEpubCoverAsync(Stream ebookStream, string coverUrl)
    {
        Console.WriteLine($"[EbookCoverService] Starting EPUB cover replacement from {coverUrl}");

        // Download cover image
        byte[] coverImageData;
        string coverExtension;
        try
        {
            coverImageData = await _httpClient.GetByteArrayAsync(coverUrl);
            coverExtension = DetermineImageExtension(coverUrl, coverImageData);
            Console.WriteLine($"[EbookCoverService] Downloaded cover: {coverImageData.Length} bytes, extension: {coverExtension}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EbookCoverService] Failed to download cover: {ex.Message}");
            return ebookStream;
        }

        try
        {
            // Reset stream position
            if (ebookStream.CanSeek)
            {
                ebookStream.Position = 0;
            }

            // Create a memory stream for the modified EPUB
            var outputStream = new MemoryStream();

            // Open the EPUB as a ZIP archive using SharpZipLib
            using (var inputZipFile = new ZipFile(ebookStream))
            {
                inputZipFile.IsStreamOwner = false; // Don't close the input stream

                // Find the cover image entry
                ZipEntry? existingCoverEntry = null;
                string? coverEntryPath = null;

                foreach (ZipEntry entry in inputZipFile)
                {
                    if (!entry.IsFile) continue;

                    var entryNameLower = entry.Name.ToLowerInvariant();
                    if (entryNameLower.Contains("cover") &&
                        (entryNameLower.EndsWith(".jpg") || entryNameLower.EndsWith(".jpeg") ||
                         entryNameLower.EndsWith(".png") || entryNameLower.EndsWith(".gif")))
                    {
                        existingCoverEntry = entry;
                        coverEntryPath = entry.Name;
                        Console.WriteLine($"[EbookCoverService] Found existing cover at: {coverEntryPath}");
                        break;
                    }
                }

                // Determine cover path
                if (coverEntryPath == null)
                {
                    coverEntryPath = $"cover{coverExtension}";
                    Console.WriteLine($"[EbookCoverService] No existing cover found, using default path: {coverEntryPath}");
                }
                else
                {
                    // Update the path with the new extension if different
                    var directory = Path.GetDirectoryName(coverEntryPath)?.Replace("\\", "/");
                    coverEntryPath = string.IsNullOrEmpty(directory)
                        ? $"cover{coverExtension}"
                        : $"{directory}/cover{coverExtension}";
                }

                // Get reference metadata from existing cover or another entry
                ZipEntry referenceEntry = existingCoverEntry ?? inputZipFile.Cast<ZipEntry>().FirstOrDefault(e => e.IsFile && e.Size > 0);

                // Extract metadata to replicate
                int hostSystem = referenceEntry?.HostSystem ?? (int)HostSystemID.Msdos;
                DateTime timestamp = referenceEntry?.DateTime ?? DateTime.Now;
                int externalFileAttributes = referenceEntry?.ExternalFileAttributes ?? 0;

                Console.WriteLine($"[EbookCoverService] Using metadata - HostSystem: {hostSystem}, Timestamp: {timestamp:yyyy-MM-dd HH:mm:ss}");

                // Create output ZIP with SharpZipLib
                using (var outputZipStream = new ZipOutputStream(outputStream))
                {
                    outputZipStream.IsStreamOwner = false; // Don't close the output stream

                    // Set compression level (6 is default/optimal)
                    outputZipStream.SetLevel(6);

                    // Copy all entries except the old cover
                    int copiedCount = 0;
                    foreach (ZipEntry entry in inputZipFile)
                    {
                        if (!entry.IsFile) continue;

                        // Skip the old cover entry
                        if (existingCoverEntry != null && entry.Name == existingCoverEntry.Name)
                        {
                            Console.WriteLine($"[EbookCoverService] Skipping old cover: {entry.Name}");
                            continue;
                        }

                        // Create new entry with preserved metadata
                        var newEntry = new ZipEntry(entry.Name)
                        {
                            DateTime = entry.DateTime,
                            HostSystem = entry.HostSystem,
                            ExternalFileAttributes = entry.ExternalFileAttributes,
                            CompressionMethod = entry.CompressionMethod,
                        };

                        // Special handling for mimetype (MUST be stored uncompressed and first)
                        if (entry.Name == "mimetype")
                        {
                            newEntry.CompressionMethod = CompressionMethod.Stored;
                        }

                        outputZipStream.PutNextEntry(newEntry);

                        // Copy data
                        using (var inputStream = inputZipFile.GetInputStream(entry))
                        {
                            await inputStream.CopyToAsync(outputZipStream);
                        }

                        outputZipStream.CloseEntry();
                        copiedCount++;
                    }

                    Console.WriteLine($"[EbookCoverService] Copied {copiedCount} entries");

                    // Add the new cover image with metadata matching the original archive
                    var coverEntry = new ZipEntry(coverEntryPath)
                    {
                        DateTime = timestamp,
                        HostSystem = hostSystem,
                        ExternalFileAttributes = externalFileAttributes,
                        CompressionMethod = CompressionMethod.Deflated
                    };

                    Console.WriteLine($"[EbookCoverService] Adding new cover at: {coverEntryPath}");
                    Console.WriteLine($"[EbookCoverService] Cover metadata - HostSystem: {coverEntry.HostSystem}, DateTime: {coverEntry.DateTime}");

                    outputZipStream.PutNextEntry(coverEntry);
                    await outputZipStream.WriteAsync(coverImageData, 0, coverImageData.Length);
                    outputZipStream.CloseEntry();

                    // Finish the ZIP
                    outputZipStream.Finish();
                }
            }

            outputStream.Position = 0;
            Console.WriteLine($"[EbookCoverService] Successfully replaced EPUB cover, output size: {outputStream.Length} bytes");
            return outputStream;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EbookCoverService] Failed to process EPUB: {ex.Message}");
            Console.WriteLine($"[EbookCoverService] Stack trace: {ex.StackTrace}");

            // Reset stream position if possible
            if (ebookStream.CanSeek)
            {
                ebookStream.Position = 0;
            }

            return ebookStream;
        }
    }

    private string DetermineImageExtension(string url, byte[] imageData)
    {
        // Try to determine from URL extension
        var urlLower = url.ToLowerInvariant();
        if (urlLower.EndsWith(".jpg") || urlLower.EndsWith(".jpeg"))
            return ".jpg";
        if (urlLower.EndsWith(".png"))
            return ".png";
        if (urlLower.EndsWith(".gif"))
            return ".gif";

        // Try to determine from image data header
        if (imageData.Length >= 4)
        {
            // PNG signature: 89 50 4E 47
            if (imageData[0] == 0x89 && imageData[1] == 0x50 && imageData[2] == 0x4E && imageData[3] == 0x47)
                return ".png";

            // JPEG signature: FF D8 FF
            if (imageData[0] == 0xFF && imageData[1] == 0xD8 && imageData[2] == 0xFF)
                return ".jpg";

            // GIF signature: 47 49 46
            if (imageData[0] == 0x47 && imageData[1] == 0x49 && imageData[2] == 0x46)
                return ".gif";
        }

        // Default to JPEG if we can't determine
        return ".jpg";
    }
}
