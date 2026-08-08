using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AnnasArchive.API.Models;
using ICSharpCode.SharpZipLib.Zip;
using Serilog;
using VersOne.Epub;
using ZipArchive = System.IO.Compression.ZipArchive;
using ZipArchiveMode = System.IO.Compression.ZipArchiveMode;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Describes one EPUB for the cache engine: the cache key (hashed into the cache
/// dir name), a log label, the path whose filename is the title fallback for
/// books without embedded titles, and how to fetch the raw bytes.
/// </summary>
public sealed record EpubSource(string CacheKey, string Label, string TitlePath, Func<Task<byte[]>> FetchBytes);

/// <summary>
/// Shared EPUB chapter-cache engine used by both the Dropbox and library readers.
/// Owns EPUB parsing (including zip repair and the tolerant fallback parser) and the
/// on-disk chapter cache; callers supply the raw EPUB bytes via a delegate, so the
/// facades (DropboxEpubCache / LibraryEpubCache) only differ in where bytes come from.
/// </summary>
public static class EpubChapterCache
{
    private static readonly string CacheRoot = StoragePaths.EpubCacheRoot();
    private static readonly ConcurrentDictionary<string, Task> CacheBuildTasks = new();
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string GetCacheRoot() => CacheRoot;

    public static string ComputeHash(string value)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(value);
        var hashBytes = sha.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static string CacheDirFor(string cacheKey)
    {
        Directory.CreateDirectory(CacheRoot);
        return Path.Combine(CacheRoot, ComputeHash(cacheKey));
    }

    public static async Task<(CachedChapterIndex Index, string CacheDir)> GetOrBuildChapterIndexAsync(EpubSource source)
    {
        var cacheDir = CacheDirFor(source.CacheKey);
        Directory.CreateDirectory(cacheDir);

        var metaPath = Path.Combine(cacheDir, "metadata.json");

        if (File.Exists(metaPath))
        {
            var cached = await TryReadIndex(metaPath);
            if (cached != null)
            {
                _ = CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(source, cacheDir));
                return (cached, cacheDir);
            }
        }

        await CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(source, cacheDir));
        var fresh = await TryReadIndex(metaPath)
            ?? throw new InvalidOperationException("Failed to read chapter index after build.");

        CacheBuildTasks.TryRemove(cacheDir, out _);
        return (fresh, cacheDir);
    }

    /// <summary>
    /// Like <see cref="GetOrBuildChapterIndexAsync"/> but writes only the index (no chapter
    /// files) when nothing is cached yet, so the reader UI can show the table of contents fast.
    /// </summary>
    public static async Task<(CachedChapterIndex Index, string CacheDir)> GetOrBuildChapterIndexQuickAsync(EpubSource source)
    {
        var cacheDir = CacheDirFor(source.CacheKey);
        Directory.CreateDirectory(cacheDir);

        var metaPath = Path.Combine(cacheDir, "metadata.json");
        if (File.Exists(metaPath))
        {
            var cached = await TryReadIndex(metaPath);
            if (cached != null)
                return (cached, cacheDir);
        }

        if (CacheBuildTasks.TryGetValue(cacheDir, out var buildTask))
        {
            await buildTask;
            var cached = await TryReadIndex(metaPath);
            if (cached != null)
                return (cached, cacheDir);
        }

        try
        {
            await BuildIndexOnlyAsync(source, cacheDir);
        }
        catch (ArgumentException ex)
        {
            Log.Information("[epub] Invalid argument for quick index build {Label}: {ParamName}", source.Label, ex.ParamName);
        }
        catch (Exception ex)
        {
            Log.Information("[epub] Quick index build failed for {Label}: {ErrorMessage}", source.Label, ex.Message);
        }

        var fresh = await TryReadIndex(metaPath);
        if (fresh != null)
            return (fresh, cacheDir);

        await CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(source, cacheDir));
        fresh = await TryReadIndex(metaPath)
            ?? throw new InvalidOperationException("Failed to read chapter index after quick build.");
        return (fresh, cacheDir);
    }

    public static Task EnsureCacheBuildAsync(EpubSource source, string cacheDir) =>
        CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(source, cacheDir));

    public static async Task<DropboxCacheStatusDto> GetCacheStatusAsync(EpubSource source)
    {
        var cacheDir = CacheDirFor(source.CacheKey);
        var metaPath = Path.Combine(cacheDir, "metadata.json");
        var errorPath = Path.Combine(cacheDir, "error.txt");
        var inProgress = CacheBuildTasks.ContainsKey(cacheDir);
        var error = File.Exists(errorPath) ? await File.ReadAllTextAsync(errorPath) : null;

        if (!File.Exists(metaPath))
        {
            _ = CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(source, cacheDir));
            return new DropboxCacheStatusDto(false, true, 0, 0, 0, null, error);
        }

        var meta = await TryReadIndex(metaPath);
        if (meta == null)
            return new DropboxCacheStatusDto(false, inProgress, 0, 0, 0, null, error);

        var total = meta.Chapters.Count;
        var cached = meta.Chapters.Count(ch => File.Exists(Path.Combine(cacheDir, ch.FileName)));
        var percent = total == 0 ? 0 : Math.Round((double)cached / total * 100, 2);

        return new DropboxCacheStatusDto(true, inProgress, total, cached, percent, meta.CachedAt, error);
    }

    public static bool DeleteCache(string cacheKey)
    {
        var cacheDir = CacheDirFor(cacheKey);
        try
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
                CacheBuildTasks.TryRemove(cacheDir, out _);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<List<DropboxSearchMatchDto>> SearchAsync(EpubSource source, string query)
    {
        var (index, cacheDir) = await GetOrBuildChapterIndexAsync(source);
        await EnsureCacheBuildAsync(source, cacheDir);

        var normalizedQuery = query.Trim();
        var results = new List<DropboxSearchMatchDto>();

        foreach (var chapter in index.Chapters)
        {
            var chapterPath = Path.Combine(cacheDir, chapter.FileName);
            if (!File.Exists(chapterPath)) continue;

            var content = await File.ReadAllTextAsync(chapterPath);
            var matches = Regex.Matches(content, Regex.Escape(normalizedQuery), RegexOptions.IgnoreCase);
            if (matches.Count == 0) continue;

            var first = matches[0];
            var start = Math.Max(0, first.Index - 80);
            var end = Math.Min(content.Length, first.Index + normalizedQuery.Length + 120);
            var snippet = content[start..end];
            snippet = Regex.Replace(snippet, @"\s+", " ").Trim();

            results.Add(new DropboxSearchMatchDto(
                chapter.Id,
                chapter.Title,
                matches.Count,
                first.Index,
                snippet));
        }

        return results
            .OrderByDescending(r => r.MatchCount)
            .ThenBy(r => r.ChapterId)
            .ToList();
    }

    private static async Task BuildCacheInternalAsync(EpubSource source, string cacheDir)
    {
        var errorPath = Path.Combine(cacheDir, "error.txt");
        try
        {
            var sourceBytes = await source.FetchBytes();
            var (title, flatChapters) = await GetFlatChaptersAsync(sourceBytes, source.Label, source.TitlePath);
            flatChapters = flatChapters
                .Where(ch => ch.WordCount >= 50)
                .ToList();

            var chapterMetas = new List<CachedChapterMeta>();
            foreach (var ch in flatChapters)
            {
                var fileName = $"chapter-{ch.Id:D4}.txt";
                var chapterPath = Path.Combine(cacheDir, fileName);
                await File.WriteAllTextAsync(chapterPath, ch.PlainText);

                chapterMetas.Add(new CachedChapterMeta(
                    ch.Id,
                    ch.Title,
                    ch.Level,
                    ch.PlainText.Length,
                    ch.WordCount,
                    fileName,
                    null,
                    null));
            }

            var meta = new CachedChapterIndex(
                source.CacheKey,
                title,
                DateTime.UtcNow,
                chapterMetas);

            var metaJson = JsonSerializer.Serialize(meta, CacheJsonOptions);
            await WriteMetadataAtomicAsync(Path.Combine(cacheDir, "metadata.json"), metaJson);
            if (File.Exists(errorPath))
                File.Delete(errorPath);
        }
        catch (ArgumentException ex)
        {
            var message = $"[epub] Invalid argument building EPUB cache for {source.Label}: {ex.ParamName}";
            Log.Information(message);
            await File.WriteAllTextAsync(errorPath, message);
            throw;
        }
        catch (Exception ex)
        {
            var message = $"[epub] Failed to build EPUB cache for {source.Label}: {ex}";
            Log.Information(message);
            await File.WriteAllTextAsync(errorPath, message);
            throw;
        }
        finally
        {
            CacheBuildTasks.TryRemove(cacheDir, out _);
        }
    }

    private static async Task BuildIndexOnlyAsync(EpubSource source, string cacheDir)
    {
        var sourceBytes = await source.FetchBytes();
        var (title, flatChapters) = await GetFlatChaptersAsync(sourceBytes, source.Label, source.TitlePath);
        flatChapters = flatChapters
            .Where(ch => ch.WordCount >= 50)
            .ToList();

        var chapterMetas = flatChapters
            .Select(ch => new CachedChapterMeta(
                ch.Id,
                ch.Title,
                ch.Level,
                ch.PlainText.Length,
                ch.WordCount,
                $"chapter-{ch.Id:D4}.txt",
                null,
                null))
            .ToList();

        var meta = new CachedChapterIndex(
            source.CacheKey,
            title,
            DateTime.UtcNow,
            chapterMetas);

        var metaJson = JsonSerializer.Serialize(meta, CacheJsonOptions);
        await WriteMetadataAtomicAsync(Path.Combine(cacheDir, "metadata.json"), metaJson);
    }

    private static async Task<CachedChapterIndex?> TryReadIndex(string metaPath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(metaPath);
            return JsonSerializer.Deserialize<CachedChapterIndex>(json, CacheJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteMetadataAtomicAsync(string metaPath, string metaJson)
    {
        var tmpPath = $"{metaPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmpPath, metaJson);
        File.Move(tmpPath, metaPath, overwrite: true);
    }

    // ---- EPUB parsing (VersOne with zip-repair + tolerant raw-zip fallback) ----

    public static async Task<(string Title, List<FlatChapter> Chapters)> GetFlatChaptersAsync(
        byte[] sourceBytes,
        string label,
        string filePath)
    {
        try
        {
            var book = await ReadBookWithFallbackAsync(sourceBytes, label);
            var chapters = FlattenChapters(book).ToList();
            var title = string.IsNullOrWhiteSpace(book.Title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : book.Title;
            return (title, chapters);
        }
        catch (ArgumentException ex)
        {
            Log.Information("[epub] Invalid argument parsing EPUB {Label}: {ParamName}", label, ex.ParamName);
            throw;
        }
        catch (Exception ex)
        {
            var fallback = TryBuildChaptersFromZipBytes(sourceBytes, label);
            if (fallback != null && fallback.Value.Chapters.Count > 0)
            {
                var title = string.IsNullOrWhiteSpace(fallback.Value.Title)
                    ? Path.GetFileNameWithoutExtension(filePath)
                    : fallback.Value.Title!;
                return (title, fallback.Value.Chapters);
            }

            throw new InvalidOperationException($"Failed to parse EPUB after tolerant fallback: {label}", ex);
        }
    }

    private static async Task<EpubBook> ReadBookWithFallbackAsync(byte[] sourceBytes, string label)
    {
        var workingBytes = sourceBytes;
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coverFallbackApplied = false;
        var zipRepairAttempted = false;

        for (var attempt = 0; attempt < 3; attempt += 1)
        {
            using var source = new MemoryStream(workingBytes);
            try
            {
                return await EpubReader.ReadBookAsync(source);
            }
            catch (InvalidDataException)
            {
                if (!zipRepairAttempted)
                {
                    var repaired = TryRepairZip(workingBytes);
                    if (repaired != null)
                    {
                        Log.Information("[epub] Repaired zip structure for {Label}", label);
                        workingBytes = repaired;
                        zipRepairAttempted = true;
                        continue;
                    }
                }
                throw;
            }
            catch (EpubContentException ex)
            {
                var missingPath = EpubZipPaths.ExtractMissingEpubPath(ex.Message);
                if (string.IsNullOrWhiteSpace(missingPath) || added.Contains(missingPath))
                {
                    if (!coverFallbackApplied)
                    {
                        workingBytes = EnsureCommonCoverEntries(workingBytes);
                        coverFallbackApplied = true;
                        continue;
                    }
                    throw;
                }

                Log.Information("[epub] Missing content '{MissingPath}' in {Label}. Injecting placeholder.", missingPath, label);
                workingBytes = EnsureZipEntry(workingBytes, missingPath);
                added.Add(missingPath);
            }
        }

        throw new InvalidOperationException($"Failed to parse EPUB after fallback attempts: {label}");
    }

    private static (string? Title, List<FlatChapter> Chapters)? TryBuildChaptersFromZipBytes(
        byte[] sourceBytes,
        string label)
    {
        if (!TryReadZipEntries(sourceBytes, label, out var entries, out var opfPath))
        {
            var repaired = TryRepairZip(sourceBytes);
            if (repaired == null || !TryReadZipEntries(repaired, label, out entries, out opfPath))
                return null;
        }

        if (entries.Count == 0)
            return null;

        string? bookTitle = null;
        List<string> orderedHtml = new();

        if (!string.IsNullOrWhiteSpace(opfPath) && entries.TryGetValue(opfPath, out var opfBytes))
        {
            try
            {
                var opfText = EpubZipPaths.ReadTextFromBytes(opfBytes);
                var opfDir = EpubZipPaths.NormalizeZipDir(Path.GetDirectoryName(opfPath) ?? string.Empty);
                var doc = XDocument.Parse(opfText);

                bookTitle = doc.Descendants()
                    .FirstOrDefault(el => el.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))
                    ?.Value
                    ?.Trim();

                var items = doc.Descendants()
                    .Where(el => el.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
                    .Select(el => new
                    {
                        Id = el.Attribute("id")?.Value,
                        Href = el.Attribute("href")?.Value
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
                    .ToDictionary(
                        item => item.Id!,
                        item => EpubZipPaths.NormalizeZipPath(EpubZipPaths.ResolveOpfHref(opfDir, item.Href!)),
                        StringComparer.OrdinalIgnoreCase);

                var spine = doc.Descendants()
                    .Where(el => el.Name.LocalName.Equals("itemref", StringComparison.OrdinalIgnoreCase))
                    .Select(el => el.Attribute("idref")?.Value)
                    .Where(idref => !string.IsNullOrWhiteSpace(idref))
                    .Select(idref => items.TryGetValue(idref!, out var href) ? href : null)
                    .Where(href => !string.IsNullOrWhiteSpace(href))
                    .Select(href => EpubZipPaths.FindEntry(entries, href!))
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                orderedHtml = spine;
            }
            catch (ArgumentException ex)
            {
                Log.Information("[epub] Invalid argument parsing OPF for tolerant fallback ({Label}): {ParamName}", label, ex.ParamName);
            }
            catch (Exception ex)
            {
                Log.Information("[epub] Failed to parse OPF for tolerant fallback ({Label}): {ErrorMessage}", label, ex.Message);
            }
        }

        if (orderedHtml.Count == 0)
        {
            orderedHtml = entries.Keys
                .Where(EpubZipPaths.IsHtmlEntry)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var chapters = new List<FlatChapter>();
        var index = 0;
        foreach (var path in orderedHtml)
        {
            if (!entries.TryGetValue(path, out var data))
                continue;

            var html = EpubZipPaths.ReadTextFromBytes(data);
            var text = HtmlToPlainText(html);
            var words = CountWords(text);
            var title = EpubZipPaths.ExtractTitleFromHtml(html) ?? Path.GetFileNameWithoutExtension(path);
            chapters.Add(new FlatChapter(index++, title, 0, text, words));
        }

        if (chapters.Count == 0)
            return null;

        Log.Information("[epub] Tolerant fallback used for {Label}. Chapters={ChapterCount}", label, chapters.Count);
        return (bookTitle, chapters);
    }

    private static bool TryReadZipEntries(
        byte[] sourceBytes,
        string label,
        out Dictionary<string, byte[]> entries,
        out string? opfPath)
    {
        entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        opfPath = null;

        try
        {
            using var inputStream = new MemoryStream(sourceBytes);
            using var zipInput = new ZipInputStream(inputStream);
            ZipEntry? entry;
            while ((entry = zipInput.GetNextEntry()) != null)
            {
                if (!entry.IsFile) continue;
                var name = EpubZipPaths.NormalizeZipPath(entry.Name);
                if (!EpubZipPaths.IsHtmlEntry(name) && !name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var buffer = new MemoryStream();
                zipInput.CopyTo(buffer);
                entries[name] = buffer.ToArray();
                if (opfPath == null && name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                    opfPath = name;
            }

            return entries.Count > 0;
        }
        catch (ArgumentException ex)
        {
            Log.Information("[epub] Invalid argument reading zip entries for tolerant fallback ({Label}): {ParamName}", label, ex.ParamName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Information("[epub] Failed to read zip entries for tolerant fallback ({Label}): {ErrorMessage}", label, ex.Message);
            return false;
        }
    }

    private static byte[]? TryRepairZip(byte[] sourceBytes)
    {
        try
        {
            using var inputStream = new MemoryStream(sourceBytes);
            using var zipInput = new ZipInputStream(inputStream);
            var entries = new List<(string Name, byte[] Data)>();
            ZipEntry? entry;
            while ((entry = zipInput.GetNextEntry()) != null)
            {
                if (!entry.IsFile) continue;
                using var buffer = new MemoryStream();
                zipInput.CopyTo(buffer);
                entries.Add((entry.Name, buffer.ToArray()));
            }

            if (entries.Count == 0)
                return null;

            var mimeEntry = entries.FirstOrDefault(e => string.Equals(e.Name, "mimetype", StringComparison.OrdinalIgnoreCase));
            var others = entries.Where(e => !string.Equals(e.Name, "mimetype", StringComparison.OrdinalIgnoreCase)).ToList();

            using var outputStream = new MemoryStream();
            using var zipOutput = new ZipOutputStream(outputStream);
            zipOutput.SetLevel(6);

            if (!string.IsNullOrEmpty(mimeEntry.Name))
            {
                var crc32 = new ICSharpCode.SharpZipLib.Checksum.Crc32();
                crc32.Update(mimeEntry.Data);
                var mimeZipEntry = new ZipEntry("mimetype")
                {
                    CompressionMethod = CompressionMethod.Stored,
                    Size = mimeEntry.Data.Length,
                    CompressedSize = mimeEntry.Data.Length,
                    Crc = crc32.Value
                };
                zipOutput.PutNextEntry(mimeZipEntry);
                zipOutput.Write(mimeEntry.Data, 0, mimeEntry.Data.Length);
                zipOutput.CloseEntry();
            }

            foreach (var item in others)
            {
                var newEntry = new ZipEntry(item.Name)
                {
                    CompressionMethod = CompressionMethod.Deflated
                };
                zipOutput.PutNextEntry(newEntry);
                zipOutput.Write(item.Data, 0, item.Data.Length);
                zipOutput.CloseEntry();
            }

            zipOutput.Finish();
            return outputStream.ToArray();
        }
        catch (ArgumentException ex)
        {
            Log.Information("[epub] Invalid argument for zip repair: {ParamName}", ex.ParamName);
            return null;
        }
        catch (Exception ex)
        {
            Log.Information("[epub] Zip repair failed: {ErrorMessage}", ex.Message);
            return null;
        }
    }

    private static byte[] EnsureCommonCoverEntries(byte[] sourceBytes)
    {
        var paths = new[]
        {
            "OEBPS/Images/Cover.png",
            "OEBPS/Images/cover.png",
            "OEBPS/Images/cover.jpg",
            "OEBPS/Cover.png",
            "OEBPS/cover.jpg",
            "cover.png",
            "cover.jpg"
        };

        var updated = sourceBytes;
        foreach (var path in paths)
        {
            updated = EnsureZipEntry(updated, path);
        }
        return updated;
    }


    private static byte[] EnsureZipEntry(byte[] sourceBytes, string entryPath)
    {
        try
        {
            using var stream = new MemoryStream(sourceBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
            if (archive.GetEntry(entryPath) != null)
                return sourceBytes;

            var entry = archive.CreateEntry(entryPath);
            using var entryStream = entry.Open();
            entryStream.Flush();
            return stream.ToArray();
        }
        catch (InvalidDataException ex)
        {
            Log.Information("[epub] Invalid zip structure while adding '{EntryPath}': {ErrorMessage}", entryPath, ex.Message);
            return sourceBytes;
        }
    }

    private static IEnumerable<FlatChapter> FlattenChapters(EpubBook book)
    {
        var results = new List<FlatChapter>();
        var index = 0;

        if (book.Navigation != null && book.Navigation.Any())
        {
            void Walk(IEnumerable<EpubNavigationItem> items, int level)
            {
                foreach (var nav in items)
                {
                    if (nav.Type == EpubNavigationItemType.LINK && nav.HtmlContentFile != null)
                    {
                        var currentId = index++;
                        var title = string.IsNullOrWhiteSpace(nav.Title)
                            ? $"Chapter {currentId + 1}"
                            : nav.Title.Trim();

                        var text = HtmlToPlainText(nav.HtmlContentFile.Content);
                        var words = CountWords(text);

                        results.Add(new FlatChapter(currentId, title, level, text, words));
                    }

                    if (nav.NestedItems?.Any() == true)
                        Walk(nav.NestedItems, level + 1);
                }
            }

            Walk(book.Navigation, 0);
        }

        if (results.Count == 0 && book.ReadingOrder != null && book.ReadingOrder.Any())
        {
            foreach (var file in book.ReadingOrder)
            {
                var currentId = index++;
                var title = string.IsNullOrWhiteSpace(file.FilePath)
                    ? $"Section {currentId + 1}"
                    : Path.GetFileNameWithoutExtension(file.FilePath);

                var text = HtmlToPlainText(file.Content);
                var words = CountWords(text);

                results.Add(new FlatChapter(currentId, title, 0, text, words));
            }
        }

        return results;
    }

    private static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var cleaned = Regex.Replace(
            html,
            "<(script|style)[^>]*?>.*?</\\1>",
            string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"<(br|p|div|h[1-6]|li)[^>]*>",
            "\n\n",
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(cleaned, "<[^>]+>", " ");

        var decoded = WebUtility.HtmlDecode(cleaned);
        decoded = decoded.Replace("\r", "");

        decoded = Regex.Replace(decoded, @"[ \t]+", " ");
        decoded = Regex.Replace(decoded, @"(\n\s*){3,}", "\n\n");
        decoded = decoded.Trim();

        return decoded;
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Regex.Matches(text, @"\b\w+\b").Count;
    }
}
