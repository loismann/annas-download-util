using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;

namespace AnnasArchive.API.Services;

public class LibraryWatcherService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(3);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx", ".pobi", ".fb2", ".txt", ".rtf", ".lit", ".djvu"
    };

    private readonly ConcurrentDictionary<string, DateTime> _pending = new();
    private readonly IHttpClientFactory _httpFactory;
    private FileSystemWatcher? _watcher;

    public LibraryWatcherService(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var libraryRoot = ResolveLibraryRoot();
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(Path.Combine(libraryRoot, "_covers"));

        StartWatcher(libraryRoot);

        var queueTask = ProcessQueueAsync(libraryRoot, stoppingToken);
        var scanTask = PeriodicScanAsync(libraryRoot, stoppingToken);

        await Task.WhenAll(queueTask, scanTask);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch
        {
            // Ignore cleanup errors during shutdown
        }

        await base.StopAsync(cancellationToken);
    }

    private void StartWatcher(string libraryRoot)
    {
        _watcher = new FileSystemWatcher(libraryRoot)
        {
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Created += (_, e) => EnqueueIfSupported(e.FullPath);
        _watcher.Changed += (_, e) => EnqueueIfSupported(e.FullPath);
        _watcher.Renamed += (_, e) => EnqueueIfSupported(e.FullPath);
    }

    private void EnqueueIfSupported(string path)
    {
        var ext = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(ext))
            return;

        _pending[path] = DateTime.UtcNow;
    }

    private async Task ProcessQueueAsync(string libraryRoot, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _pending.ToArray())
            {
                if (now - entry.Value < DebounceWindow)
                    continue;

                if (_pending.TryRemove(entry.Key, out _))
                {
                    await ProcessFileAsync(libraryRoot, entry.Key, token);
                }
            }

            await Task.Delay(1000, token);
        }
    }

    private async Task PeriodicScanAsync(string libraryRoot, CancellationToken token)
    {
        await RunFullScanAsync(libraryRoot, token);

        var timer = new PeriodicTimer(ScanInterval);
        while (await timer.WaitForNextTickAsync(token))
        {
            await RunFullScanAsync(libraryRoot, token);
        }
    }

    private async Task RunFullScanAsync(string libraryRoot, CancellationToken token)
    {
        var files = Directory.GetFiles(libraryRoot)
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
            .ToList();

        var duplicates = FindDuplicateFiles(libraryRoot, files);
        foreach (var duplicate in duplicates)
        {
            DeleteLibraryArtifacts(libraryRoot, duplicate);
        }

        foreach (var file in files)
        {
            if (token.IsCancellationRequested)
                break;

            if (duplicates.Contains(file))
                continue;

            await ProcessFileAsync(libraryRoot, file, token);
        }
    }

    private async Task ProcessFileAsync(string libraryRoot, string filePath, CancellationToken token)
    {
        if (!File.Exists(filePath))
            return;

        var fileInfo = new FileInfo(filePath);
        var metaPath = $"{filePath}.meta.json";

        ExistingMeta? existing = null;
        if (File.Exists(metaPath))
        {
            existing = await LoadExistingMetaAsync(metaPath, token);
            var metaInfo = new FileInfo(metaPath);
            var metaIsFresh = metaInfo.LastWriteTimeUtc >= fileInfo.LastWriteTimeUtc;
            if (metaIsFresh && existing?.HasCoreMetadata == true && existing.AiEnrichedAt != null &&
                existing.GoodreadsRating.HasValue)
                return;
            if (metaIsFresh && existing?.HasCoreMetadata == true &&
                existing.OpenLibraryConfidence is >= 0.75 &&
                existing.GoodreadsRating.HasValue &&
                !string.IsNullOrWhiteSpace(existing.CoverUrl))
                return;
        }

        if (!await WaitForStableFileAsync(filePath, token))
        {
            EnqueueIfSupported(filePath);
            return;
        }

        var ext = Path.GetExtension(filePath);
        var format = ext.TrimStart('.').ToUpperInvariant();
        var parsed = ParseTitleAuthorFromFileName(filePath);
        var rawBaseName = Path.GetFileNameWithoutExtension(filePath);
        var resolvedTitle = ShouldUseParsedTitle(existing?.Title, parsed.Title, rawBaseName)
            ? parsed.Title
            : existing?.Title;
        var resolvedAuthors = (existing?.Authors == null || existing.Authors.Length == 0)
            ? parsed.Authors
            : existing.Authors;

        var meta = new Dictionary<string, object?>
        {
            ["title"] = resolvedTitle ?? parsed.Title ?? rawBaseName,
            ["authors"] = resolvedAuthors ?? parsed.Authors ?? Array.Empty<string>(),
            ["format"] = format,
            ["fileSize"] = FormatFileSize(fileInfo.Length),
            ["fileName"] = Path.GetFileName(filePath),
            ["coverUrl"] = existing?.CoverUrl,
            ["source"] = existing?.Source ?? "library",
            ["md5"] = existing?.Md5,
            ["savedAt"] = existing?.SavedAt ?? DateTime.UtcNow.ToString("o"),
            ["primaryGenre"] = existing?.PrimaryGenre,
            ["tags"] = existing?.Tags ?? Array.Empty<string>(),
            ["series"] = existing?.Series,
            ["genres"] = existing?.Genres ?? Array.Empty<string>(),
            ["publishedDate"] = existing?.PublishedDate,
            ["pages"] = existing?.Pages,
            ["goodreadsRating"] = existing?.GoodreadsRating,
            ["personalRating"] = existing?.PersonalRating,
            ["readerEnabled"] = existing?.ReaderEnabled,
            ["openLibraryConfidence"] = existing?.OpenLibraryConfidence,
            ["aiEnrichedAt"] = existing?.AiEnrichedAt
        };

        Console.WriteLine($"[LibraryWatcher] Processing {Path.GetFileName(filePath)}");
        Console.WriteLine($"[LibraryWatcher]   Existing CoverUrl: {existing?.CoverUrl}");

        if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            await PopulateEpubMetadataAsync(filePath, meta, libraryRoot, token);
        }

        if (string.IsNullOrWhiteSpace(TryGetMetaValue(meta, "coverUrl") as string))
        {
            await TryExtractSdrCoverAsync(filePath, libraryRoot, meta, token);
        }

        var coverUrl = TryGetMetaValue(meta, "coverUrl") as string;
        var title = TryGetMetaValue(meta, "title") as string;
        var authors = TryGetMetaArray(meta, "authors") ?? Array.Empty<string>();
        var series = TryGetMetaValue(meta, "series") as string;
        var aiEnrichedAt = TryGetMetaValue(meta, "aiEnrichedAt") as string;
        var goodreadsRating = TryGetMetaValue(meta, "goodreadsRating") as double?;

        OpenLibraryData? openLibraryData = null;
        if (IsMetadataReliable(title, authors))
        {
            openLibraryData = await FetchOpenLibraryDataAsync(title!, authors!, token);
            if (openLibraryData != null)
            {
                meta["openLibraryConfidence"] = openLibraryData.Confidence;
                if (openLibraryData.Confidence >= 0.75)
                {
                    if (!string.IsNullOrWhiteSpace(openLibraryData.Title))
                        meta["title"] = openLibraryData.Title;
                    if (openLibraryData.Authors.Length > 0)
                        meta["authors"] = openLibraryData.Authors;
                    if (openLibraryData.FirstPublishYear != null)
                        meta["publishedDate"] = openLibraryData.FirstPublishYear.ToString();
                }

                if (string.IsNullOrWhiteSpace(coverUrl) && !string.IsNullOrWhiteSpace(openLibraryData.CoverUrl))
                    meta["coverUrl"] = openLibraryData.CoverUrl;

                if (string.IsNullOrWhiteSpace(series) && !string.IsNullOrWhiteSpace(openLibraryData.Series))
                    meta["series"] = openLibraryData.Series;
            }
        }

        if (openLibraryData != null && openLibraryData.Confidence < 0.75)
        {
            var aiValidation = await ValidateOpenLibraryCandidateAsync(
                title ?? "",
                authors,
                fileInfo.Name,
                openLibraryData,
                token);

            if (aiValidation?.IsMatch == true)
            {
                if (!string.IsNullOrWhiteSpace(openLibraryData.Title))
                    meta["title"] = openLibraryData.Title;
                if (openLibraryData.Authors.Length > 0)
                    meta["authors"] = openLibraryData.Authors;
                if (string.IsNullOrWhiteSpace(coverUrl) && !string.IsNullOrWhiteSpace(openLibraryData.CoverUrl))
                    meta["coverUrl"] = openLibraryData.CoverUrl;
                meta["openLibraryConfidence"] = Math.Max(openLibraryData.Confidence, 0.75);
            }
        }

        if (string.IsNullOrWhiteSpace(aiEnrichedAt) && (openLibraryData?.Confidence ?? 0) < 0.75 && IsMetadataReliable(title, authors))
        {
            var aiMeta = await FetchAiMetadataAsync(title!, authors, fileInfo.Name, openLibraryData, token);
            if (aiMeta != null)
            {
                meta["title"] = aiMeta.Title;
                meta["authors"] = aiMeta.Authors;
                meta["publishedDate"] = aiMeta.PublishedDate;
                meta["series"] = aiMeta.Series;
                if (!string.IsNullOrWhiteSpace(aiMeta.CoverUrl))
                    meta["coverUrl"] = aiMeta.CoverUrl;
                meta["aiEnrichedAt"] = DateTime.UtcNow.ToString("o");
            }
        }

        coverUrl = TryGetMetaValue(meta, "coverUrl") as string;
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            var externalCover = await FetchGoogleBooksCoverAsync(title ?? "", authors, token);
            if (!string.IsNullOrWhiteSpace(externalCover))
                meta["coverUrl"] = externalCover;
        }

        if (!goodreadsRating.HasValue && IsMetadataReliable(title, authors))
        {
            var fetchedRating = await FetchGoodreadsRatingAsync(title ?? "", authors, openLibraryData, token);
            if (fetchedRating.HasValue)
                meta["goodreadsRating"] = fetchedRating.Value;
            else
                meta["goodreadsRating"] ??= null;
        }

        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        Console.WriteLine($"[LibraryWatcher]   Final CoverUrl before write: {meta["coverUrl"]}");
        await File.WriteAllTextAsync(metaPath, json, token);
        Console.WriteLine($"[LibraryWatcher]   WROTE metadata to {metaPath}");
    }

    private async Task<bool> WaitForStableFileAsync(string filePath, CancellationToken token)
    {
        try
        {
            long lastSize = -1;
            for (var i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested();
                var size = new FileInfo(filePath).Length;
                if (size == lastSize)
                    return true;
                lastSize = size;
                await Task.Delay(600, token);
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private async Task PopulateEpubMetadataAsync(
        string filePath,
        Dictionary<string, object?> meta,
        string libraryRoot,
        CancellationToken token)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var containerEntry = zip.GetEntry("META-INF/container.xml");
            if (containerEntry == null)
                return;

            using var containerStream = containerEntry.Open();
            var containerDoc = await XDocument.LoadAsync(containerStream, LoadOptions.None, token);
            var rootfile = containerDoc
                .Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "rootfile")
                ?.Attribute("full-path")
                ?.Value;

            if (string.IsNullOrWhiteSpace(rootfile))
                return;

            var opfEntry = zip.GetEntry(rootfile);
            if (opfEntry == null)
                return;

            using var opfStream = opfEntry.Open();
            var opfDoc = await XDocument.LoadAsync(opfStream, LoadOptions.None, token);

            var metadata = opfDoc.Descendants().FirstOrDefault(x => x.Name.LocalName == "metadata");
            if (metadata != null)
            {
                var title = metadata.Descendants().FirstOrDefault(x => x.Name.LocalName == "title")?.Value;
                if (!string.IsNullOrWhiteSpace(title))
                    SetIfMissing(meta, "title", title.Trim());

                var creators = metadata.Descendants()
                    .Where(x => x.Name.LocalName == "creator")
                    .Select(x => x.Value.Trim())
                    .Where(x => x.Length > 0)
                    .Distinct()
                    .ToArray();
                if (creators.Length > 0)
                    SetIfMissing(meta, "authors", creators);

                var date = metadata.Descendants().FirstOrDefault(x => x.Name.LocalName == "date")?.Value;
                if (!string.IsNullOrWhiteSpace(date))
                    SetIfMissing(meta, "publishedDate", date.Trim());

                var pageCount = metadata.Descendants()
                    .Where(x => x.Name.LocalName == "meta")
                    .FirstOrDefault(x => string.Equals(x.Attribute("name")?.Value, "calibre:page_count", StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("content")
                    ?.Value;
                if (!string.IsNullOrWhiteSpace(pageCount))
                    SetIfMissing(meta, "pages", pageCount);

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
                        .FirstOrDefault(x => x.Name.LocalName == "item" && string.Equals(x.Attribute("id")?.Value, coverId, StringComparison.OrdinalIgnoreCase))
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
                    await TryExtractCoverAsync(zip, opfDir, href, filePath, libraryRoot, meta, token);
                }
                else
                {
                    await TryExtractLargestImageAsync(zip, filePath, libraryRoot, meta, token);
                }
            }
        }
        catch
        {
            // ignore metadata parse errors
        }
    }

    private async Task TryExtractCoverAsync(
        ZipArchive zip,
        string opfDir,
        string href,
        string filePath,
        string libraryRoot,
        Dictionary<string, object?> meta,
        CancellationToken token)
    {
        var coverPath = string.IsNullOrEmpty(opfDir) ? href : $"{opfDir}/{href}";
        var coverEntry = zip.GetEntry(coverPath);
        if (coverEntry == null)
            return;

        var coverExt = Path.GetExtension(href);
        if (string.IsNullOrWhiteSpace(coverExt))
            coverExt = ".jpg";

        var coverFileName = $"{Path.GetFileName(filePath)}.cover{coverExt}";
        var coverDiskPath = Path.Combine(libraryRoot, "_covers", coverFileName);

        Directory.CreateDirectory(Path.GetDirectoryName(coverDiskPath)!);
        await using var coverStream = coverEntry.Open();
        await using var outStream = File.Create(coverDiskPath);
        await coverStream.CopyToAsync(outStream, token);

        meta["coverUrl"] = $"_covers/{coverFileName}";
    }

    private async Task TryExtractLargestImageAsync(
        ZipArchive zip,
        string filePath,
        string libraryRoot,
        Dictionary<string, object?> meta,
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
            return;

        var coverExt = Path.GetExtension(largest.FullName);
        if (string.IsNullOrWhiteSpace(coverExt))
            coverExt = ".jpg";

        var coverFileName = $"{Path.GetFileName(filePath)}.cover{coverExt}";
        var coverDiskPath = Path.Combine(libraryRoot, "_covers", coverFileName);

        Directory.CreateDirectory(Path.GetDirectoryName(coverDiskPath)!);
        await using var coverStream = largest.Open();
        await using var outStream = File.Create(coverDiskPath);
        await coverStream.CopyToAsync(outStream, token);

        meta["coverUrl"] = $"_covers/{coverFileName}";
    }

    private async Task TryExtractSdrCoverAsync(
        string filePath,
        string libraryRoot,
        Dictionary<string, object?> meta,
        CancellationToken token)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath) ?? "";
            var sdrPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(filePath) + ".sdr");
            if (!Directory.Exists(sdrPath))
                return;

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
                return;

            string PickBest(IEnumerable<string> files)
            {
                var coverMatch = files.FirstOrDefault(path =>
                    Path.GetFileName(path).Contains("cover", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(coverMatch))
                    return coverMatch;

                return files
                    .OrderByDescending(path => new FileInfo(path).Length)
                    .First();
            }

            var best = PickBest(candidates);
            var coverExt = Path.GetExtension(best);
            if (string.IsNullOrWhiteSpace(coverExt))
                coverExt = ".jpg";

            var coverFileName = $"{Path.GetFileName(filePath)}.cover{coverExt}";
            var coverDiskPath = Path.Combine(libraryRoot, "_covers", coverFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(coverDiskPath)!);

            await using var source = File.OpenRead(best);
            await using var dest = File.Create(coverDiskPath);
            await source.CopyToAsync(dest, token);

            meta["coverUrl"] = $"_covers/{coverFileName}";
        }
        catch
        {
            // ignore sdr cover extraction errors
        }
    }

    private static HashSet<string> FindDuplicateFiles(string libraryRoot, List<string> files)
    {
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var title = GetTitleForDedup(file);
            var normalized = NormalizeTitleForDedup(title);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (!byTitle.TryGetValue(normalized, out var list))
            {
                list = new List<string>();
                byTitle[normalized] = list;
            }
            list.Add(file);
        }

        // Pass 1: same-format duplicates by title (safe cleanup).
        foreach (var group in byTitle.Values.Where(list => list.Count > 1))
        {
            var byExt = group
                .GroupBy(path => Path.GetExtension(path).ToLowerInvariant());

            foreach (var extGroup in byExt)
            {
                var ordered = extGroup
                    .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                    .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var keep = ordered.First();
                foreach (var path in ordered.Skip(1))
                    duplicates.Add(path);
            }
        }

        // Pass 2: cross-format duplicates only when title + author match.
        var byTitleAuthor = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (duplicates.Contains(file))
                continue;

            var title = NormalizeTitleForDedup(GetTitleForDedup(file));
            var authorKey = NormalizeAuthorForDedup(GetAuthorsForDedup(file));
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(authorKey))
                continue;

            var key = $"{title}||{authorKey}";
            if (!byTitleAuthor.TryGetValue(key, out var list))
            {
                list = new List<string>();
                byTitleAuthor[key] = list;
            }
            list.Add(file);
        }

        foreach (var group in byTitleAuthor.Values.Where(list => list.Count > 1))
        {
            var ordered = group
                .OrderBy(path => FormatRank(Path.GetExtension(path)))
                .ThenByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var keep = ordered.First();
            foreach (var path in ordered.Skip(1))
                duplicates.Add(path);
        }

        return duplicates;
    }

    private static string GetTitleForDedup(string filePath)
    {
        var metaPath = $"{filePath}.meta.json";
        if (File.Exists(metaPath))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("title", out var titleProp))
                    return titleProp.GetString() ?? "";
            }
            catch
            {
                // ignore
            }
        }

        var parsed = ParseTitleAuthorFromFileName(filePath);
        return parsed.Title ?? Path.GetFileNameWithoutExtension(filePath);
    }

    private static string[] GetAuthorsForDedup(string filePath)
    {
        var metaPath = $"{filePath}.meta.json";
        if (File.Exists(metaPath))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("authors", out var authorsProp) &&
                    authorsProp.ValueKind == JsonValueKind.Array)
                {
                    var authors = authorsProp.EnumerateArray()
                        .Select(v => v.GetString())
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v!.Trim())
                        .ToArray();
                    if (authors.Length > 0)
                        return authors;
                }
            }
            catch
            {
                // ignore
            }
        }

        var parsed = ParseTitleAuthorFromFileName(filePath);
        return parsed.Authors ?? Array.Empty<string>();
    }

    private static string NormalizeTitleForDedup(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        var normalized = title.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\[[^\]]*\]", "");
        normalized = Regex.Replace(normalized, @"\([^)]+\)", "");
        normalized = Regex.Replace(normalized, @"\bbook\s+\d+\b", "");
        normalized = Regex.Replace(normalized, @"[^a-z0-9\s]", "");
        normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim();
        return normalized;
    }

    private static string NormalizeAuthorForDedup(string[]? authors)
    {
        if (authors == null || authors.Length == 0)
            return "";

        var normalized = string.Join(";", authors)
            .ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9\s,;]", "");
        normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim();
        return normalized;
    }

    private static int FormatRank(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".epub" => 0,
            ".azw3" => 1,
            ".azw" => 2,
            ".kfx" => 3,
            ".pobi" => 4,
            ".mobi" => 5,
            ".pdf" => 6,
            ".fb2" => 7,
            _ => 8
        };
    }

    private static void DeleteLibraryArtifacts(string libraryRoot, string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);

            var metaPath = $"{filePath}.meta.json";
            if (File.Exists(metaPath))
                File.Delete(metaPath);

            var coverDir = Path.Combine(libraryRoot, "_covers");
            if (Directory.Exists(coverDir))
            {
                var safeName = Path.GetFileName(filePath);
                foreach (var cover in Directory.GetFiles(coverDir, $"{safeName}.cover.*"))
                {
                    try { File.Delete(cover); } catch { }
                }
            }
        }
        catch
        {
            // ignore delete failures
        }
    }

    private static bool IsMetadataReliable(string? title, string[]? authors)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length < 3)
            return false;

        if (authors == null || authors.Length == 0)
            return true;

        return authors.Any(a => !string.IsNullOrWhiteSpace(a) && a.Trim().Length >= 3);
    }

    private static (string? Title, string[]? Authors) ParseTitleAuthorFromFileName(string filePath)
    {
        var raw = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(raw))
            return (null, null);

        var cleaned = Regex.Replace(raw, @"_(?:sample|preview)$", "", RegexOptions.IgnoreCase).Trim();
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

    private static bool ShouldUseParsedTitle(string? existingTitle, string? parsedTitle, string rawBaseName)
    {
        if (string.IsNullOrWhiteSpace(parsedTitle))
            return false;

        if (string.IsNullOrWhiteSpace(existingTitle))
            return true;

        var normalizedExisting = existingTitle.Trim();
        if (string.Equals(normalizedExisting, rawBaseName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalizedExisting.Contains('_'))
            return true;

        if (Regex.IsMatch(normalizedExisting, @"[A-Z0-9]{8,}$"))
            return true;

        return false;
    }

    private async Task<OpenLibraryData?> FetchOpenLibraryDataAsync(string title, string[] authors, CancellationToken token)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);

            var author = authors.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            var url = string.IsNullOrWhiteSpace(author)
                ? $"https://openlibrary.org/search.json?title={Uri.EscapeDataString(title)}&limit=10"
                : $"https://openlibrary.org/search.json?title={Uri.EscapeDataString(title)}&author={Uri.EscapeDataString(author)}&limit=10";
            using var resp = await http.GetAsync(url, token);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                return null;

            var best = (OpenLibraryData?)null;
            foreach (var item in docs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var candidateTitle = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                var candidateAuthors = ExtractStringArray(item, "author_name");
                var titleScore = TokenSimilarity(title, candidateTitle);
                var authorScore = CandidateAuthorScore(authors, candidateAuthors);
                var confidence = Math.Round((titleScore * 0.7) + (authorScore * 0.3), 3);

                if (best == null || confidence > best.Confidence)
                {
                    string? coverUrl = null;
                    if (item.TryGetProperty("cover_i", out var coverProp) && coverProp.ValueKind == JsonValueKind.Number)
                    {
                        var coverId = coverProp.GetInt32();
                        coverUrl = $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg";
                    }

                    var subjects = ExtractStringArray(item, "subject");
                    var subjectFacets = ExtractStringArray(item, "subject_facet");
                    var rawTags = subjectFacets.Length > 0 ? subjectFacets : subjects;

                    // Map Open Library subjects to standard genre
                    var primaryGenre = MapToStandardGenre(rawTags);

                    // Extract useful tags (filter out generic terms and the primary genre)
                    var tags = ExtractTags(rawTags, primaryGenre, limit: 5);
                    var series = ExtractStringArray(item, "series").FirstOrDefault();
                    int? publishYear = null;
                    if (item.TryGetProperty("first_publish_year", out var yearProp) && yearProp.ValueKind == JsonValueKind.Number)
                        publishYear = yearProp.GetInt32();

                    var isbns = ExtractStringArray(item, "isbn");
                    best = new OpenLibraryData(
                        coverUrl,
                        primaryGenre,
                        tags,
                        series,
                        candidateTitle,
                        candidateAuthors,
                        publishYear,
                        confidence,
                        isbns
                    );
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> FetchGoogleBooksCoverAsync(string title, string[] authors, CancellationToken token)
    {
        var author = authors.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (string.IsNullOrWhiteSpace(title))
            return null;

        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);

            var query = string.IsNullOrWhiteSpace(author)
                ? $"intitle:{title}"
                : $"intitle:{title} inauthor:{author}";
            var url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}&maxResults=5";
            using var resp = await http.GetAsync(url, token);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("volumeInfo", out var info))
                    continue;

                if (info.TryGetProperty("imageLinks", out var images) &&
                    images.TryGetProperty("thumbnail", out var thumbProp))
                {
                    var thumb = thumbProp.GetString();
                    if (!string.IsNullOrWhiteSpace(thumb))
                        return thumb.Replace("http:", "https:");
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private async Task<double?> FetchGoodreadsRatingAsync(
        string title,
        string[] authors,
        OpenLibraryData? openLibrary,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var (best, searchHtml) = await FetchGoodreadsSearchResultsAsync(title, authors, token);
        if (best?.Rating.HasValue == true)
            return best.Rating;

        if (!string.IsNullOrWhiteSpace(best?.Url))
        {
            var rating = await FetchGoodreadsRatingFromBookPageAsync(best.Url!, token);
            if (rating.HasValue)
                return rating;
        }

        if (openLibrary?.Isbns is { Length: > 0 })
        {
            var rating = await FetchGoodreadsRatingFromIsbnAsync(openLibrary.Isbns, token);
            if (rating.HasValue)
                return rating;
        }

        if (!string.IsNullOrWhiteSpace(searchHtml))
        {
            var rating = await FetchGoodreadsRatingWithAiFallbackAsync(searchHtml!, title, authors, token);
            if (rating.HasValue)
                return rating;
        }

        return null;
    }

    private async Task<(GoodreadsSearchResult? Best, string? Html)> FetchGoodreadsSearchResultsAsync(
        string title,
        string[] authors,
        CancellationToken token)
    {
        var primaryAuthor = authors.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        var query = string.IsNullOrWhiteSpace(primaryAuthor) ? title : $"{title} {primaryAuthor}";
        var url = $"https://www.goodreads.com/search?q={Uri.EscapeDataString(query)}&search_type=books";

        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");

            using var resp = await http.GetAsync(url, token);
            if (!resp.IsSuccessStatusCode)
                return (null, null);

            var html = await resp.Content.ReadAsStringAsync(token);
            if (string.IsNullOrWhiteSpace(html))
                return (null, null);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var rows = doc.DocumentNode.SelectNodes("//table[contains(@class,'tableList')]//tr");
            if (rows == null || rows.Count == 0)
                return (null, html);

            GoodreadsSearchResult? best = null;
            foreach (var row in rows)
            {
                var titleNode = row.SelectSingleNode(".//a[contains(@class,'bookTitle')]");
                if (titleNode == null)
                    continue;

                var candidateTitle = WebUtility.HtmlDecode(titleNode.InnerText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(candidateTitle))
                    continue;

                var authorNodes = row.SelectNodes(".//a[contains(@class,'authorName')]");
                var candidateAuthors = authorNodes == null
                    ? Array.Empty<string>()
                    : authorNodes
                        .Select(n => WebUtility.HtmlDecode(n.InnerText ?? string.Empty).Trim())
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                var ratingNode = row.SelectSingleNode(".//span[contains(@class,'minirating')]");
                var rating = ratingNode != null ? ParseGoodreadsRating(ratingNode.InnerText) : null;

                var href = titleNode.GetAttributeValue("href", "");
                var urlValue = string.IsNullOrWhiteSpace(href) ? null : $"https://www.goodreads.com{href}";

                var titleScore = TokenSimilarity(title, candidateTitle);
                var authorScore = CandidateAuthorScore(authors, candidateAuthors);
                var score = (titleScore * 0.7) + (authorScore * 0.3);

                if (best == null || score > best.Score)
                {
                    best = new GoodreadsSearchResult(candidateTitle, candidateAuthors, rating, urlValue, score);
                }
            }

            return (best, html);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<double?> FetchGoodreadsRatingFromBookPageAsync(string url, CancellationToken token)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");

            using var resp = await http.GetAsync(url, token);
            if (!resp.IsSuccessStatusCode)
                return null;

            var html = await resp.Content.ReadAsStringAsync(token);
            return ExtractRatingFromHtml(html);
        }
        catch
        {
            return null;
        }
    }

    private async Task<double?> FetchGoodreadsRatingFromIsbnAsync(string[] isbns, CancellationToken token)
    {
        foreach (var isbn in isbns.Take(4))
        {
            if (string.IsNullOrWhiteSpace(isbn))
                continue;

            var clean = isbn.Trim();
            if (clean.Length is < 9 or > 17)
                continue;

            var url = $"https://www.goodreads.com/book/isbn/{Uri.EscapeDataString(clean)}";
            var rating = await FetchGoodreadsRatingFromBookPageAsync(url, token);
            if (rating.HasValue)
                return rating;
        }

        return null;
    }

    private async Task<double?> FetchGoodreadsRatingWithAiFallbackAsync(
        string html,
        string title,
        string[] authors,
        CancellationToken token)
    {
        var markerIndex = html.IndexOf("avg rating", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            markerIndex = html.IndexOf("ratingValue", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var start = Math.Max(0, markerIndex - 4000);
        var length = Math.Min(12000, html.Length - start);
        var snippet = html.Substring(start, length);

        try
        {
            using var http = _httpFactory.CreateClient("OpenAI");
            var systemPrompt = @"You extract Goodreads average rating values from HTML snippets. Return ONLY JSON.";
            var authorText = authors.Length > 0 ? string.Join(", ", authors) : "Unknown";
            var userPrompt = $@"Title: {title}
Author: {authorText}
HTML snippet:
{snippet}

Return JSON:
{{ ""rating"": number|null }}";

            var payload = new
            {
                model = "gpt-4o",
                input = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.0,
                max_output_tokens = 120
            };

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload, token);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var text = ExtractResponseText(doc.RootElement);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var cleaned = text.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();
            }

            using var resultDoc = JsonDocument.Parse(cleaned);
            var root = resultDoc.RootElement;
            if (root.TryGetProperty("rating", out var ratingProp))
            {
                if (ratingProp.ValueKind == JsonValueKind.Number && ratingProp.TryGetDouble(out var num))
                    return num;
                if (ratingProp.ValueKind == JsonValueKind.String &&
                    double.TryParse(ratingProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static double? ExtractRatingFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var jsonLdMatch = Regex.Match(html, "\"ratingValue\"\\s*:\\s*\"(?<rating>[0-9.]+)\"");
        if (jsonLdMatch.Success && TryParseRating(jsonLdMatch.Groups["rating"].Value, out var rating))
            return rating;

        var itemPropMatch = Regex.Match(html, "itemprop=\"ratingValue\"[^>]*>(?<rating>[0-9.]+)<");
        if (itemPropMatch.Success && TryParseRating(itemPropMatch.Groups["rating"].Value, out rating))
            return rating;

        var avgMatch = Regex.Match(html, @"(?<rating>[0-9.]+)\\s*avg rating", RegexOptions.IgnoreCase);
        if (avgMatch.Success && TryParseRating(avgMatch.Groups["rating"].Value, out rating))
            return rating;

        return null;
    }

    private static double? ParseGoodreadsRating(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = Regex.Match(text, @"(?<rating>[0-9.]+)\\s*avg rating", RegexOptions.IgnoreCase);
        if (match.Success && TryParseRating(match.Groups["rating"].Value, out var rating))
            return rating;

        return null;
    }

    private static bool TryParseRating(string value, out double rating)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out rating);
    }

    private sealed record GoodreadsSearchResult(
        string Title,
        string[] Authors,
        double? Rating,
        string? Url,
        double Score);

    private static string[] ExtractStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return prop.EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var num))
            return num;

        if (prop.ValueKind == JsonValueKind.String &&
            double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var num))
            return num;

        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static bool IsGenericTag(string tag)
    {
        var normalized = tag.Trim().ToLowerInvariant();
        return normalized is "fiction" or "novel" or "books" or "literature" or "general";
    }

    private static object? TryGetMetaValue(Dictionary<string, object?> meta, string key)
    {
        return meta.TryGetValue(key, out var value) ? value : null;
    }

    private static string[]? TryGetMetaArray(Dictionary<string, object?> meta, string key)
    {
        return meta.TryGetValue(key, out var value) ? value as string[] : null;
    }

    private static void SetIfMissing(Dictionary<string, object?> meta, string key, object value)
    {
        if (!meta.TryGetValue(key, out var current) || current == null ||
            (current is string str && string.IsNullOrWhiteSpace(str)) ||
            (current is string[] arr && arr.Length == 0))
        {
            meta[key] = value;
        }
    }

    private static double CandidateAuthorScore(string[] inputAuthors, string[] candidateAuthors)
    {
        if (candidateAuthors.Length == 0 || inputAuthors.Length == 0)
            return 0;

        var best = 0.0;
        foreach (var input in inputAuthors)
        {
            foreach (var candidate in candidateAuthors)
            {
                best = Math.Max(best, TokenSimilarity(input, candidate));
            }
        }

        return best;
    }

    private static double TokenSimilarity(string? left, string? right)
    {
        var leftTokens = NormalizeForMatch(left);
        var rightTokens = NormalizeForMatch(right);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0;

        var intersect = leftTokens.Intersect(rightTokens).Count();
        var union = leftTokens.Union(rightTokens).Count();
        return union == 0 ? 0 : (double)intersect / union;
    }

    private static List<string> NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();

        var cleaned = new string(value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray());

        return cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToList();
    }

    private async Task<AiMetadata?> FetchAiMetadataAsync(
        string title,
        string[] authors,
        string fileName,
        OpenLibraryData? openLibrary,
        CancellationToken token)
    {
        try
        {
            using var http = _httpFactory.CreateClient("OpenAI");
            var model = "gpt-4o";

            var systemPrompt = @"You are a book metadata librarian. Normalize messy metadata into clean fields.
Return ONLY valid JSON. Do not include markdown or extra text.";

            var openLibraryBlock = openLibrary == null
                ? "OpenLibrary: none"
                : $"OpenLibrary: title={openLibrary.Title}, authors={string.Join(", ", openLibrary.Authors)}, year={openLibrary.FirstPublishYear}, coverUrl={openLibrary.CoverUrl}, tags={string.Join(", ", openLibrary.Tags)}, series={openLibrary.Series}, confidence={openLibrary.Confidence}";

            var userPrompt = $@"File name: {fileName}
Current title: {title}
Current authors: {string.Join(", ", authors)}
{openLibraryBlock}

Return JSON with:
{{
  ""title"": string,
  ""authors"": string[],
  ""publishedDate"": string|null,
  ""primaryGenre"": string|null,
  ""tags"": string[],
  ""series"": string|null,
  ""coverUrl"": string|null
}}";

            var payload = new
            {
                model,
                input = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.2,
                max_output_tokens = 400
            };

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload, token);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var text = ExtractResponseText(doc.RootElement);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var cleaned = text.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();
            }

            using var resultDoc = JsonDocument.Parse(cleaned);
            var root = resultDoc.RootElement;

            var aiTitle = root.TryGetProperty("title", out var t) ? t.GetString() : title;
            var aiAuthors = ExtractStringArray(root, "authors");
            if (aiAuthors.Length == 0)
                aiAuthors = authors;

            var aiTags = ExtractStringArray(root, "tags");
            return new AiMetadata(
                aiTitle ?? title,
                aiAuthors,
                root.TryGetProperty("publishedDate", out var pd) ? pd.GetString() : null,
                root.TryGetProperty("primaryGenre", out var pg) ? pg.GetString() : null,
                aiTags,
                root.TryGetProperty("series", out var s) ? s.GetString() : null,
                root.TryGetProperty("coverUrl", out var cu) ? cu.GetString() : null
            );
        }
        catch
        {
            return null;
        }
    }

    private async Task<AiValidation?> ValidateOpenLibraryCandidateAsync(
        string title,
        string[] authors,
        string fileName,
        OpenLibraryData openLibrary,
        CancellationToken token)
    {
        try
        {
            using var http = _httpFactory.CreateClient("OpenAI");
            var model = "gpt-4o";

            var systemPrompt = @"You are a book metadata verifier. Decide if the OpenLibrary candidate matches the book.
Return ONLY valid JSON. Do not include markdown or extra text.";

            var userPrompt = $@"File name: {fileName}
Current title: {title}
Current authors: {string.Join(", ", authors)}
OpenLibrary title: {openLibrary.Title}
OpenLibrary authors: {string.Join(", ", openLibrary.Authors)}
OpenLibrary coverUrl: {openLibrary.CoverUrl}
OpenLibrary confidence: {openLibrary.Confidence}

Return JSON:
{{
  ""isMatch"": boolean
}}";

            var payload = new
            {
                model,
                input = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.1,
                max_output_tokens = 120
            };

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload, token);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var text = ExtractResponseText(doc.RootElement);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var cleaned = text.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();
            }

            using var resultDoc = JsonDocument.Parse(cleaned);
            var root = resultDoc.RootElement;

            var isMatch = root.TryGetProperty("isMatch", out var matchProp) &&
                          matchProp.ValueKind == JsonValueKind.True;

            return new AiValidation(isMatch);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractResponseText(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textProp))
                            return textProp.GetString();
                    }
                }
            }
        }
        return null;
    }

    private static async Task<ExistingMeta?> LoadExistingMetaAsync(string metaPath, CancellationToken token)
    {
        try
        {
            var json = await File.ReadAllTextAsync(metaPath, token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new ExistingMeta
            {
                Title = root.TryGetProperty("title", out var title) ? title.GetString() : null,
                Authors = ExtractStringArray(root, "authors"),
                CoverUrl = root.TryGetProperty("coverUrl", out var cover) ? cover.GetString() : null,
                Source = root.TryGetProperty("source", out var source) ? source.GetString() : null,
                Md5 = root.TryGetProperty("md5", out var md5) ? md5.GetString() : null,
                SavedAt = root.TryGetProperty("savedAt", out var saved) ? saved.GetString() : null,
                PrimaryGenre = root.TryGetProperty("primaryGenre", out var pg) ? pg.GetString() : null,
                Tags = ExtractStringArray(root, "tags"),
                Series = root.TryGetProperty("series", out var series) ? series.GetString() : null,
                Genres = ExtractStringArray(root, "genres"),
                PublishedDate = root.TryGetProperty("publishedDate", out var pd) ? pd.GetString() : null,
                Pages = root.TryGetProperty("pages", out var pages) ? pages.GetString() : null,
                GoodreadsRating = TryGetDouble(root, "goodreadsRating"),
                PersonalRating = TryGetInt(root, "personalRating"),
                ReaderEnabled = root.TryGetProperty("readerEnabled", out var readerEnabled)
                    ? readerEnabled.ValueKind == JsonValueKind.True
                        ? true
                        : readerEnabled.ValueKind == JsonValueKind.False
                            ? false
                            : null
                    : null,
                OpenLibraryConfidence = root.TryGetProperty("openLibraryConfidence", out var conf) && conf.ValueKind == JsonValueKind.Number
                    ? conf.GetDouble()
                    : null,
                AiEnrichedAt = root.TryGetProperty("aiEnrichedAt", out var ai) ? ai.GetString() : null
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed record OpenLibraryData(
        string? CoverUrl,
        string? PrimaryGenre,
        string[] Tags,
        string? Series,
        string? Title,
        string[] Authors,
        int? FirstPublishYear,
        double Confidence,
        string[] Isbns);

    private sealed record AiMetadata(
        string Title,
        string[] Authors,
        string? PublishedDate,
        string? PrimaryGenre,
        string[] Tags,
        string? Series,
        string? CoverUrl);

    private sealed record AiValidation(
        bool IsMatch);

    private sealed class ExistingMeta
    {
        public string? Title { get; init; }
        public string[]? Authors { get; init; }
        public string? CoverUrl { get; init; }
        public string? Source { get; init; }
        public string? Md5 { get; init; }
        public string? SavedAt { get; init; }
        public string? PrimaryGenre { get; init; }
        public string[]? Tags { get; init; }
        public string? Series { get; init; }
        public string[]? Genres { get; init; }
        public string? PublishedDate { get; init; }
        public string? Pages { get; init; }
        public double? GoodreadsRating { get; init; }
        public int? PersonalRating { get; init; }
        public bool? ReaderEnabled { get; init; }
        public double? OpenLibraryConfidence { get; init; }
        public string? AiEnrichedAt { get; init; }

        public bool HasCoreMetadata =>
            !string.IsNullOrWhiteSpace(Title) &&
            Authors != null &&
            Authors.Length > 0;
    }

    private static readonly string[] StandardGenres =
    {
        "Science Fiction", "Fantasy", "Mystery & Detective", "Thriller", "Romance",
        "Historical Fiction", "Literary Fiction", "Horror", "Adventure", "Young Adult",
        "Children's", "Graphic Novel", "Short Stories", "Classics", "Biography & Memoir",
        "History", "Science & Technology", "Philosophy", "Self-Help", "Business & Economics",
        "Travel", "True Crime", "Essays", "Politics & Current Events", "Religion & Spirituality",
        "Art & Photography", "Cooking & Food", "Health & Fitness", "Poetry", "Drama",
        "Reference", "Uncategorized"
    };

    private static readonly Dictionary<string, string[]> GenreKeywordMap = new()
    {
        ["Science Fiction"] = new[] { "science fiction", "sci-fi", "scifi", "space opera", "cyberpunk", "dystopia", "dystopian", "time travel", "space", "aliens", "future", "robots", "artificial intelligence" },
        ["Fantasy"] = new[] { "fantasy", "magic", "wizards", "dragons", "sword and sorcery", "epic fantasy", "urban fantasy", "paranormal", "mythical", "fairy tale", "elves", "supernatural" },
        ["Mystery & Detective"] = new[] { "mystery", "detective", "crime", "murder", "investigation", "whodunit", "noir", "police procedural", "sleuth", "clues" },
        ["Thriller"] = new[] { "thriller", "suspense", "action", "espionage", "spy", "psychological thriller", "conspiracy", "terrorism" },
        ["Romance"] = new[] { "romance", "love story", "romantic", "love", "relationships", "contemporary romance", "historical romance", "romantic comedy" },
        ["Historical Fiction"] = new[] { "historical fiction", "historical", "period", "world war", "civil war", "victorian", "medieval", "ancient" },
        ["Literary Fiction"] = new[] { "literary fiction", "literary", "contemporary fiction", "modern fiction", "satire", "allegory" },
        ["Horror"] = new[] { "horror", "terror", "scary", "ghost", "vampire", "zombie", "monsters", "haunted", "dark", "gothic" },
        ["Adventure"] = new[] { "adventure", "quest", "journey", "exploration", "expedition", "survival", "treasure", "pirates" },
        ["Young Adult"] = new[] { "young adult", "ya", "teen", "teenage", "coming of age", "high school", "adolescent" },
        ["Children's"] = new[] { "children", "kids", "juvenile", "picture book", "early reader", "middle grade", "bedtime story" },
        ["Graphic Novel"] = new[] { "graphic novel", "comic", "manga", "illustrated", "sequential art" },
        ["Short Stories"] = new[] { "short stories", "anthology", "collection", "novellas", "short fiction" },
        ["Classics"] = new[] { "classic", "classical", "nineteenth century", "19th century", "eighteenth century", "18th century", "masterpiece" },
        ["Biography & Memoir"] = new[] { "biography", "memoir", "autobiography", "life story", "diaries", "letters", "personal narrative", "biographical" },
        ["History"] = new[] { "history", "historical", "civilization", "archaeology", "ancient history", "military history", "social history" },
        ["Science & Technology"] = new[] { "science", "technology", "physics", "biology", "chemistry", "mathematics", "astronomy", "engineering", "computers", "nature" },
        ["Philosophy"] = new[] { "philosophy", "philosophical", "ethics", "logic", "metaphysics", "epistemology", "existentialism", "phenomenology" },
        ["Self-Help"] = new[] { "self-help", "self improvement", "personal development", "motivation", "success", "happiness", "productivity" },
        ["Business & Economics"] = new[] { "business", "economics", "finance", "management", "entrepreneurship", "marketing", "investing", "money", "capitalism" },
        ["Travel"] = new[] { "travel", "tourism", "guidebook", "travelogue", "adventure travel", "cultural exploration", "geography" },
        ["True Crime"] = new[] { "true crime", "criminal", "murder case", "serial killer", "investigation", "forensic", "crime story" },
        ["Essays"] = new[] { "essays", "essay", "nonfiction", "criticism", "commentary", "reflections", "observations" },
        ["Politics & Current Events"] = new[] { "politics", "political", "government", "democracy", "current events", "international relations", "diplomacy", "elections" },
        ["Religion & Spirituality"] = new[] { "religion", "religious", "spirituality", "faith", "theology", "christianity", "buddhism", "islam", "meditation", "prayer" },
        ["Art & Photography"] = new[] { "art", "photography", "painting", "sculpture", "artists", "visual arts", "design", "architecture" },
        ["Cooking & Food"] = new[] { "cooking", "food", "recipes", "cookbook", "culinary", "cuisine", "baking", "gastronomy" },
        ["Health & Fitness"] = new[] { "health", "fitness", "exercise", "nutrition", "diet", "wellness", "medicine", "medical", "yoga" },
        ["Poetry"] = new[] { "poetry", "poems", "verse", "sonnets", "haiku" },
        ["Drama"] = new[] { "drama", "plays", "theater", "theatre", "screenplay", "script" },
        ["Reference"] = new[] { "reference", "encyclopedia", "dictionary", "handbook", "manual", "guide", "textbook", "directory" }
    };

    private static string MapToStandardGenre(string[] openLibrarySubjects)
    {
        if (openLibrarySubjects == null || openLibrarySubjects.Length == 0)
            return "Uncategorized";

        var scores = new Dictionary<string, int>();

        foreach (var subject in openLibrarySubjects)
        {
            var normalized = subject.ToLowerInvariant().Trim();
            foreach (var (genre, keywords) in GenreKeywordMap)
            {
                var score = 0;
                foreach (var keyword in keywords)
                {
                    if (normalized == keyword)
                        score += 10; // Exact match
                    else if (normalized.Contains(keyword))
                        score += 5; // Contains keyword
                    else if (keyword.Contains(normalized) && normalized.Length > 3)
                        score += 2; // Keyword contains subject
                }

                if (score > 0)
                {
                    if (!scores.ContainsKey(genre))
                        scores[genre] = 0;
                    scores[genre] += score;
                }
            }
        }

        if (scores.Count == 0)
            return "Uncategorized";

        return scores.OrderByDescending(kv => kv.Value).First().Key;
    }

    private static string[] ExtractTags(string[] openLibrarySubjects, string primaryGenre, int limit)
    {
        if (openLibrarySubjects == null || openLibrarySubjects.Length == 0)
            return Array.Empty<string>();

        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fiction", "non-fiction", "nonfiction", "general", "book", "books",
            "literature", "accessible book", "protected daisy", "in library",
            "open library staff picks", "american", "english", "british"
        };

        return openLibrarySubjects
            .Where(s =>
            {
                var lower = s.ToLowerInvariant();
                return !stopWords.Contains(lower) &&
                       !string.Equals(s, primaryGenre, StringComparison.OrdinalIgnoreCase) &&
                       !lower.Contains("fictitious character") &&
                       s.Length > 1 &&
                       !int.TryParse(s, out _);
            })
            .Take(limit)
            .ToArray();
    }

    private static string ResolveLibraryRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("LIBRARY_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot;

        const string synologyDefault = "/volume1/books/Library";
        if (Directory.Exists(synologyDefault))
            return synologyDefault;

        return Path.Combine(AppContext.BaseDirectory, "library");
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
            return "0B";

        string[] units = { "B", "KB", "MB", "GB" };
        var size = (double)bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.0}{units[unitIndex]}";
    }
}
