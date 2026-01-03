#!/usr/bin/env dotnet script
/*
 * Standalone test to apply cover replacement and validate the result
 * Usage: dotnet script test-cover-replacement.cs <epub-path> <cover-url> [output-path]
 *
 * Or compile and run:
 * dotnet run test-cover-replacement.cs "/path/to/book.epub" "https://example.com/cover.jpg"
 */

using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

if (Args.Count < 2)
{
    Console.WriteLine("Usage: dotnet script test-cover-replacement.cs <epub-path> <cover-url> [output-path]");
    Console.WriteLine("");
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet script test-cover-replacement.cs 'book.epub' 'https://covers.openlibrary.org/b/id/12345-L.jpg'");
    return 1;
}

var inputPath = Args[0];
var coverUrl = Args[1];
var outputPath = Args.Count > 2 ? Args[2] : "/tmp/modified-epub-test.epub";

if (!File.Exists(inputPath))
{
    Console.WriteLine($"❌ Input file not found: {inputPath}");
    return 1;
}

Console.WriteLine("==================================================================");
Console.WriteLine("EPUB Cover Replacement Test");
Console.WriteLine("==================================================================");
Console.WriteLine($"Input:  {inputPath}");
Console.WriteLine($"Cover:  {coverUrl}");
Console.WriteLine($"Output: {outputPath}");
Console.WriteLine("");

try
{
    // Read the EPUB
    using var inputStream = File.OpenRead(inputPath);
    using var httpClient = new HttpClient();

    // Download cover
    Console.WriteLine("Step 1: Downloading cover image...");
    var coverData = await httpClient.GetByteArrayAsync(coverUrl);
    Console.WriteLine($"  Downloaded: {coverData.Length} bytes");

    // Process EPUB
    Console.WriteLine("\nStep 2: Processing EPUB...");
    var outputStream = await ReplaceCoverAsync(inputStream, coverData, ".jpg");

    // Save result
    Console.WriteLine("\nStep 3: Saving modified EPUB...");
    using (var fileStream = File.Create(outputPath))
    {
        outputStream.Position = 0;
        await outputStream.CopyToAsync(fileStream);
    }

    var originalSize = new FileInfo(inputPath).Length;
    var modifiedSize = new FileInfo(outputPath).Length;

    Console.WriteLine($"\n✅ Success!");
    Console.WriteLine($"  Original size:  {originalSize:N0} bytes");
    Console.WriteLine($"  Modified size:  {modifiedSize:N0} bytes");
    Console.WriteLine($"  Difference:     {(modifiedSize - originalSize):N0} bytes");

    Console.WriteLine($"\nModified EPUB saved to: {outputPath}");
    Console.WriteLine("\nTo validate:");
    Console.WriteLine($"  python3 validate-epub-kindle.py '{outputPath}'");

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ ERROR: {ex.Message}");
    Console.WriteLine($"Stack trace:\n{ex.StackTrace}");
    return 1;
}

// Simplified version of the EbookCoverService logic
async Task<Stream> ReplaceCoverAsync(Stream ebookStream, byte[] coverImageData, string coverExtension)
{
    var outputStream = new MemoryStream();

    using (var zipArchive = new ZipArchive(ebookStream, ZipArchiveMode.Read, leaveOpen: false))
    {
        using (var outputZip = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Find existing cover
            ZipArchiveEntry? existingCoverEntry = null;
            string? coverEntryPath = null;

            foreach (var entry in zipArchive.Entries)
            {
                var entryNameLower = entry.FullName.ToLowerInvariant();
                if (entryNameLower.Contains("cover") &&
                    (entryNameLower.EndsWith(".jpg") || entryNameLower.EndsWith(".jpeg") ||
                     entryNameLower.EndsWith(".png") || entryNameLower.EndsWith(".gif")))
                {
                    existingCoverEntry = entry;
                    coverEntryPath = entry.FullName;
                    Console.WriteLine($"  Found existing cover: {coverEntryPath}");
                    break;
                }
            }

            if (coverEntryPath == null)
            {
                coverEntryPath = $"cover{coverExtension}";
                Console.WriteLine($"  No existing cover found, using: {coverEntryPath}");
            }
            else
            {
                var directory = Path.GetDirectoryName(coverEntryPath)?.Replace("\\", "/");
                coverEntryPath = string.IsNullOrEmpty(directory)
                    ? $"cover{coverExtension}"
                    : $"{directory}/cover{coverExtension}";
                Console.WriteLine($"  Will replace with: {coverEntryPath}");
            }

            // Copy all entries except old cover
            int copiedCount = 0;
            foreach (var entry in zipArchive.Entries)
            {
                if (existingCoverEntry != null && entry.FullName == existingCoverEntry.FullName)
                {
                    Console.WriteLine($"  Skipping old cover: {entry.FullName}");
                    continue;
                }

                var newEntry = outputZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using (var originalStream = entry.Open())
                using (var newStream = newEntry.Open())
                {
                    await originalStream.CopyToAsync(newStream);
                }
                copiedCount++;
            }
            Console.WriteLine($"  Copied {copiedCount} entries");

            // Add new cover
            Console.WriteLine($"  Adding new cover: {coverEntryPath}");
            var coverEntry = outputZip.CreateEntry(coverEntryPath, CompressionLevel.Optimal);
            using (var coverStream = coverEntry.Open())
            {
                await coverStream.WriteAsync(coverImageData, 0, coverImageData.Length);
            }

            Console.WriteLine($"  ⚠️  WARNING: content.opf NOT updated (this is the bug!)");
        }
    }

    outputStream.Position = 0;
    return outputStream;
}

return 0;
