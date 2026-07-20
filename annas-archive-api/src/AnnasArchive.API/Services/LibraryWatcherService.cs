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
using AnnasArchive.API.Configuration;
using AnnasArchive.API.Services.Library;
using Microsoft.Extensions.Configuration;
using Serilog;
using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;

namespace AnnasArchive.API.Services;

public class LibraryWatcherService : BackgroundService
{
    // Throttling configuration is now centralized in AiThrottlingConfiguration

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx", ".pobi", ".fb2", ".txt", ".rtf", ".lit", ".djvu"
    };

    private readonly ConcurrentDictionary<string, DateTime> _pending = new();
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly IGenreClassificationService _genreClassification;
    private readonly IDuplicateDetectionService _duplicateDetection;
    private readonly IMetadataExtractionService _metadataExtraction;
    private readonly IEnrichmentStatsService _statsService;
    private readonly string? _autoTagNewBooks;
    private FileSystemWatcher? _watcher;
    private int _processedSinceLastSave;

    public LibraryWatcherService(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        IGenreClassificationService genreClassification,
        IDuplicateDetectionService duplicateDetection,
        IMetadataExtractionService metadataExtraction,
        IEnrichmentStatsService statsService)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
        _genreClassification = genreClassification;
        _duplicateDetection = duplicateDetection;
        _metadataExtraction = metadataExtraction;
        _statsService = statsService;
        _autoTagNewBooks = configuration["LibraryWatcher:AutoTagNewBooks"];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var libraryRoot = ResolveLibraryRoot();
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(Path.Combine(libraryRoot, "_covers"));

        // Load existing stats
        await _statsService.LoadAsync(stoppingToken);

        // Build library index for duplicate checking
        _duplicateDetection.BuildLibraryIndex(libraryRoot);

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

            // Save stats on shutdown
            await _statsService.SaveAsync(cancellationToken);
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
            var processed = 0;

            foreach (var entry in _pending.ToArray())
            {
                if (token.IsCancellationRequested)
                    break;

                if (now - entry.Value < AiThrottlingConfiguration.LibraryDebounceWindow)
                    continue;

                if (_pending.TryRemove(entry.Key, out _))
                {
                    await ProcessFileAsync(libraryRoot, entry.Key, token);
                    processed++;

                    // Throttle: delay between books to prevent rate limiting
                    if (processed > 0)
                    {
                        Log.Debug("[LibraryWatcher] Throttling: waiting {Delay}s before next book", AiThrottlingConfiguration.LibraryDelayBetweenBooks.TotalSeconds);
                        await AiThrottlingConfiguration.ThrottleBetweenBooksAsync(token);
                    }
                }
            }

            await Task.Delay(1000, token);
        }
    }

    private async Task PeriodicScanAsync(string libraryRoot, CancellationToken token)
    {
        await RunFullScanAsync(libraryRoot, token);

        var timer = new PeriodicTimer(AiThrottlingConfiguration.LibraryScanInterval);
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

        var duplicates = _duplicateDetection.FindDuplicates(libraryRoot, files);
        foreach (var duplicate in duplicates)
        {
            _duplicateDetection.DeleteLibraryArtifacts(libraryRoot, duplicate);
        }

        // Filter out duplicates
        var allFiles = files.Where(f => !duplicates.Contains(f)).ToList();
        var totalFiles = allFiles.Count;

        // Pre-scan to find files that need enrichment (skip already complete)
        var filesToEnrich = new List<string>();
        var alreadyComplete = 0;
        foreach (var file in allFiles)
        {
            var metaPath = $"{file}.meta.json";
            if (File.Exists(metaPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(metaPath, token);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("enrichmentComplete", out var prop) && prop.GetBoolean())
                    {
                        alreadyComplete++;
                        continue;
                    }
                }
                catch { }
            }
            filesToEnrich.Add(file);
        }

        Log.Information("[LibraryWatcher] Scan starting: {Total} files total, {NeedsWork} need enrichment, {Complete} already complete",
            totalFiles, filesToEnrich.Count, alreadyComplete);

        if (filesToEnrich.Count == 0)
        {
            Log.Information("[LibraryWatcher] All files already enriched, nothing to do");
            return;
        }

        var enrichedCount = 0;
        var batchNumber = 0;
        var needsEnrichment = filesToEnrich.Count;

        foreach (var batch in filesToEnrich.Chunk(AiThrottlingConfiguration.BatchSize))
        {
            if (token.IsCancellationRequested)
                break;

            batchNumber++;
            var batchEnrichedCount = 0;

            foreach (var file in batch)
            {
                if (token.IsCancellationRequested)
                    break;

                var wasEnriched = await ProcessFileAsync(libraryRoot, file, token);

                if (wasEnriched)
                {
                    enrichedCount++;
                    batchEnrichedCount++;
                    Log.Information("[LibraryWatcher] Progress: {Done}/{Total} books enriched", enrichedCount, needsEnrichment);
                    // Only throttle between books that were actually enriched
                    await AiThrottlingConfiguration.ThrottleBetweenBooksAsync(token);
                }
            }

            // Only pause between batches if we actually did enrichment work
            if (batchEnrichedCount > 0 && enrichedCount < needsEnrichment && !token.IsCancellationRequested)
            {
                Log.Information("[LibraryWatcher] Batch {Batch} complete. Pausing {Delay}s before next batch",
                    batchNumber, AiThrottlingConfiguration.LibraryDelayBetweenBatches.TotalSeconds);
                await AiThrottlingConfiguration.ThrottleLibraryBatchAsync(token);
            }
            // No log or delay when batch has no work - just continue silently
        }

        Log.Information("[LibraryWatcher] Scan complete. Enriched {Enriched} books", enrichedCount);
    }

    /// <summary>
    /// Processes a single file for enrichment.
    /// </summary>
    /// <returns>True if enrichment was performed, false if file was skipped.</returns>
    private async Task<bool> ProcessFileAsync(string libraryRoot, string filePath, CancellationToken token)
    {
        if (!File.Exists(filePath))
            return false;

        var fileInfo = new FileInfo(filePath);
        var metaPath = $"{filePath}.meta.json";

        ExistingMeta? existing = null;
        if (File.Exists(metaPath))
        {
            existing = await LoadExistingMetaAsync(metaPath, token);

            // Simple check: if enrichment is complete, skip this file
            if (existing?.EnrichmentComplete == true)
            {
                return false;
            }
        }

        if (!await WaitForStableFileAsync(filePath, token))
        {
            EnqueueIfSupported(filePath);
            return false;
        }

        var ext = Path.GetExtension(filePath);
        var format = ext.TrimStart('.').ToUpperInvariant();
        var parsed = _metadataExtraction.ParseTitleAuthorFromFileName(filePath);
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
            ["aiEnrichedAt"] = existing?.AiEnrichedAt,
            ["enrichmentComplete"] = existing?.EnrichmentComplete ?? false
        };

        // Auto-tag new books (files that haven't been fully enriched yet)
        if (existing?.EnrichmentComplete != true && !string.IsNullOrWhiteSpace(_autoTagNewBooks))
        {
            var currentTags = (meta["tags"] as string[] ?? Array.Empty<string>()).ToList();
            if (!currentTags.Contains(_autoTagNewBooks, StringComparer.OrdinalIgnoreCase))
            {
                currentTags.Add(_autoTagNewBooks);
                meta["tags"] = currentTags.ToArray();
                Log.Information("[LibraryWatcher] Auto-tagged new book with '{Tag}'", _autoTagNewBooks);
            }
        }

        Log.Information("[LibraryWatcher] Processing {FileName}", Path.GetFileName(filePath));
        Log.Information("[LibraryWatcher]   Existing CoverUrl: {CoverUrl}", existing?.CoverUrl);
        if (!string.IsNullOrWhiteSpace(existing?.CoverUrl))
        {
            var coverType = IsLocalCover(existing.CoverUrl) ? "local (_covers/)" : "external";
            Log.Information("[LibraryWatcher]   ✓ Existing {CoverType} cover detected - will preserve", coverType);
        }

        if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            var skipCover = IsLocalCover(TryGetMetaValue(meta, "coverUrl") as string);
            var epubMeta = await _metadataExtraction.ExtractEpubMetadataAsync(filePath, libraryRoot, skipCover, token);
            if (epubMeta != null)
            {
                SetIfMissing(meta, "title", epubMeta.Title);
                SetIfMissing(meta, "authors", epubMeta.Authors);
                SetIfMissing(meta, "publishedDate", epubMeta.PublishedDate);
                SetIfMissing(meta, "pages", epubMeta.Pages);
                if (!string.IsNullOrWhiteSpace(epubMeta.CoverUrl))
                    meta["coverUrl"] = epubMeta.CoverUrl;
            }
        }

        if (string.IsNullOrWhiteSpace(TryGetMetaValue(meta, "coverUrl") as string))
        {
            var sdrCover = await _metadataExtraction.ExtractSdrCoverAsync(filePath, libraryRoot, token);
            if (!string.IsNullOrWhiteSpace(sdrCover))
                meta["coverUrl"] = sdrCover;
        }

        var coverUrl = TryGetMetaValue(meta, "coverUrl") as string;
        var title = TryGetMetaValue(meta, "title") as string;
        var authors = TryGetMetaArray(meta, "authors") ?? Array.Empty<string>();
        var series = TryGetMetaValue(meta, "series") as string;
        var aiEnrichedAt = TryGetMetaValue(meta, "aiEnrichedAt") as string;
        var goodreadsRating = TryGetMetaValue(meta, "goodreadsRating") as double?;

        // API Call 1: OpenLibrary (free, high rate limit)
        OpenLibraryData? openLibraryData = null;
        if (IsMetadataReliable(title, authors))
        {
            openLibraryData = await FetchOpenLibraryDataAsync(title!, authors!, token);
            var olSuccess = openLibraryData != null && openLibraryData.Confidence >= 0.5;
            _statsService.RecordCall("OpenLibrary", olSuccess, openLibraryData?.Confidence);

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

                // Only use external cover if no local cover exists
                if (!IsLocalCover(coverUrl) && string.IsNullOrWhiteSpace(coverUrl) && !string.IsNullOrWhiteSpace(openLibraryData.CoverUrl))
                    meta["coverUrl"] = openLibraryData.CoverUrl;

                if (string.IsNullOrWhiteSpace(series) && !string.IsNullOrWhiteSpace(openLibraryData.Series))
                    meta["series"] = openLibraryData.Series;
            }

            // Throttle between API calls
            await AiThrottlingConfiguration.ThrottleAsync(token);
        }

        // API Call 2: Combined AI validation + enrichment (single call instead of two)
        // Only call if OpenLibrary confidence is low AND no prior AI enrichment
        var aiProvidedBetterMetadata = false;
        string? aiTitle = null;
        string[]? aiAuthors = null;

        if (string.IsNullOrWhiteSpace(aiEnrichedAt) && (openLibraryData?.Confidence ?? 0) < 0.75 && IsMetadataReliable(title, authors))
        {
            var aiResult = await FetchAiValidationAndEnrichmentAsync(
                title!,
                authors,
                fileInfo.Name,
                openLibraryData,
                token);

            _statsService.RecordCall("GPT4", aiResult != null);

            if (aiResult != null)
            {
                // Apply AI results
                if (aiResult.UseOpenLibrary && openLibraryData != null)
                {
                    // AI validated OpenLibrary data - use it
                    if (!string.IsNullOrWhiteSpace(openLibraryData.Title))
                        meta["title"] = openLibraryData.Title;
                    if (openLibraryData.Authors.Length > 0)
                        meta["authors"] = openLibraryData.Authors;
                    if (!IsLocalCover(coverUrl) && string.IsNullOrWhiteSpace(coverUrl) && !string.IsNullOrWhiteSpace(openLibraryData.CoverUrl))
                        meta["coverUrl"] = openLibraryData.CoverUrl;
                    meta["openLibraryConfidence"] = Math.Max(openLibraryData.Confidence, 0.75);
                }
                else if (!string.IsNullOrWhiteSpace(aiResult.Title))
                {
                    // AI provided better metadata - save for retry
                    aiTitle = aiResult.Title;
                    aiAuthors = aiResult.Authors;
                    aiProvidedBetterMetadata = true;

                    // Use AI-provided metadata
                    meta["title"] = aiResult.Title;
                    meta["authors"] = aiResult.Authors;
                    meta["publishedDate"] = aiResult.PublishedDate;
                    meta["series"] = aiResult.Series;
                    var existingCover = TryGetMetaValue(meta, "coverUrl") as string;
                    if (string.IsNullOrWhiteSpace(existingCover) && !string.IsNullOrWhiteSpace(aiResult.CoverUrl))
                        meta["coverUrl"] = aiResult.CoverUrl;
                }
                meta["aiEnrichedAt"] = DateTime.UtcNow.ToString("o");
            }

            // Throttle between API calls
            await AiThrottlingConfiguration.ThrottleAsync(token);
        }

        // API Call 2b: RETRY OpenLibrary with AI-corrected title/author
        // This is the key improvement - if AI gave us better metadata, try OpenLibrary again
        if (aiProvidedBetterMetadata && !string.IsNullOrWhiteSpace(aiTitle))
        {
            Log.Information("[LibraryWatcher] Retrying OpenLibrary with AI-corrected metadata: {Title}", aiTitle);
            var retryOlData = await FetchOpenLibraryDataAsync(aiTitle!, aiAuthors ?? Array.Empty<string>(), token);
            _statsService.RecordCall("OpenLibrary_Retry", retryOlData != null && retryOlData.Confidence >= 0.75, retryOlData?.Confidence);

            if (retryOlData != null && retryOlData.Confidence >= 0.75)
            {
                Log.Information("[LibraryWatcher] OpenLibrary retry successful! Confidence: {Confidence}", retryOlData.Confidence);
                meta["openLibraryConfidence"] = retryOlData.Confidence;

                // Update metadata from high-confidence OpenLibrary result
                if (!string.IsNullOrWhiteSpace(retryOlData.Title))
                    meta["title"] = retryOlData.Title;
                if (retryOlData.Authors.Length > 0)
                    meta["authors"] = retryOlData.Authors;
                if (retryOlData.FirstPublishYear != null)
                    meta["publishedDate"] = retryOlData.FirstPublishYear.ToString();

                coverUrl = TryGetMetaValue(meta, "coverUrl") as string;
                if (!IsLocalCover(coverUrl) && string.IsNullOrWhiteSpace(coverUrl) && !string.IsNullOrWhiteSpace(retryOlData.CoverUrl))
                    meta["coverUrl"] = retryOlData.CoverUrl;

                if (string.IsNullOrWhiteSpace(TryGetMetaValue(meta, "series") as string) && !string.IsNullOrWhiteSpace(retryOlData.Series))
                    meta["series"] = retryOlData.Series;
            }

            await AiThrottlingConfiguration.ThrottleAsync(token);
        }

        // Re-read title/authors after potential updates
        title = TryGetMetaValue(meta, "title") as string;
        authors = TryGetMetaArray(meta, "authors") ?? Array.Empty<string>();

        // API Call 3: Google Books (only for cover if still missing)
        coverUrl = TryGetMetaValue(meta, "coverUrl") as string;
        if (!IsLocalCover(coverUrl) && string.IsNullOrWhiteSpace(coverUrl))
        {
            var externalCover = await FetchGoogleBooksCoverAsync(title ?? "", authors, token);
            _statsService.RecordCall("GoogleBooks", !string.IsNullOrWhiteSpace(externalCover));

            if (!string.IsNullOrWhiteSpace(externalCover))
                meta["coverUrl"] = externalCover;

            // Throttle between API calls
            await AiThrottlingConfiguration.ThrottleAsync(token);
        }

        // API Call 4: Goodreads (search only - no fallback chain)
        if (!goodreadsRating.HasValue && IsMetadataReliable(title, authors))
        {
            var fetchedRating = await FetchGoodreadsRatingSimpleAsync(title ?? "", authors, token);
            _statsService.RecordCall("Goodreads", fetchedRating.HasValue);

            if (fetchedRating.HasValue)
                meta["goodreadsRating"] = fetchedRating.Value;
            else
                meta["goodreadsRating"] ??= null;
        }

        // Mark enrichment complete after a full pass
        meta["enrichmentComplete"] = true;

        // Track book processed
        var hasGoodMetadata = !string.IsNullOrWhiteSpace(TryGetMetaValue(meta, "title") as string) &&
                              (TryGetMetaArray(meta, "authors")?.Length ?? 0) > 0;
        _statsService.RecordBookProcessed(hasGoodMetadata);

        // Save stats periodically (every 10 books)
        _processedSinceLastSave++;
        if (_processedSinceLastSave >= 10)
        {
            _processedSinceLastSave = 0;
            _ = _statsService.SaveAsync(token);
        }

        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        Log.Information("[LibraryWatcher]   Final CoverUrl before write: {CoverUrl}", meta["coverUrl"]);
        await File.WriteAllTextAsync(metaPath, json, token);
        Log.Information("[LibraryWatcher]   WROTE metadata to {MetaPath}", metaPath);

        return true; // Enrichment was performed
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

    private static bool IsMetadataReliable(string? title, string[]? authors)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length < 3)
            return false;

        if (authors == null || authors.Length == 0)
            return true;

        return authors.Any(a => !string.IsNullOrWhiteSpace(a) && a.Trim().Length >= 3);
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
                    var primaryGenre = _genreClassification.ClassifyGenre(rawTags);

                    // Extract useful tags (filter out generic terms and the primary genre)
                    var tags = _genreClassification.ExtractTags(rawTags, primaryGenre, limit: 5);
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

    /// <summary>
    /// Combined AI validation and enrichment in a single API call.
    /// Reduces API calls from 2 to 1 for low-confidence OpenLibrary matches.
    /// </summary>
    private async Task<AiValidationAndEnrichment?> FetchAiValidationAndEnrichmentAsync(
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

            var systemPrompt = @"You are a book metadata librarian. Given a file name and current metadata, do two things:
1. If OpenLibrary data is provided, decide if it matches the book
2. Normalize/clean up the metadata

Return ONLY valid JSON. Do not include markdown or extra text.";

            var openLibraryBlock = openLibrary == null
                ? "OpenLibrary: none"
                : $"OpenLibrary: title={openLibrary.Title}, authors={string.Join(", ", openLibrary.Authors)}, year={openLibrary.FirstPublishYear}, coverUrl={openLibrary.CoverUrl}, series={openLibrary.Series}, confidence={openLibrary.Confidence}";

            var userPrompt = $@"File name: {fileName}
Current title: {title}
Current authors: {string.Join(", ", authors)}
{openLibraryBlock}

Return JSON with:
{{
  ""useOpenLibrary"": boolean (true if OpenLibrary data matches this book),
  ""title"": string (cleaned/normalized title if not using OpenLibrary),
  ""authors"": string[] (cleaned authors if not using OpenLibrary),
  ""publishedDate"": string|null,
  ""series"": string|null,
  ""coverUrl"": string|null (only if you know a valid cover URL)
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

            Log.Debug("[LibraryWatcher] Calling OpenAI for combined validation+enrichment: {FileName}", fileName);
            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload, token);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("[LibraryWatcher] OpenAI API returned {StatusCode} for {FileName}", response.StatusCode, fileName);
                return null;
            }

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

            var useOpenLibrary = root.TryGetProperty("useOpenLibrary", out var useOlProp) &&
                                 useOlProp.ValueKind == JsonValueKind.True;

            var aiTitle = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var aiAuthors = ExtractStringArray(root, "authors");

            return new AiValidationAndEnrichment(
                useOpenLibrary,
                aiTitle,
                aiAuthors.Length > 0 ? aiAuthors : authors,
                root.TryGetProperty("publishedDate", out var pd) ? pd.GetString() : null,
                root.TryGetProperty("series", out var s) ? s.GetString() : null,
                root.TryGetProperty("coverUrl", out var cu) ? cu.GetString() : null
            );
        }
        catch (Exception ex)
        {
            Log.Warning("[LibraryWatcher] AI validation+enrichment failed for {FileName}: {Error}", fileName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Simplified Goodreads rating fetch - search only, no fallback chain.
    /// Reduces potential 6 HTTP requests to just 1.
    /// </summary>
    private async Task<double?> FetchGoodreadsRatingSimpleAsync(
        string title,
        string[] authors,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

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
                return null;

            var html = await resp.Content.ReadAsStringAsync(token);
            if (string.IsNullOrWhiteSpace(html))
                return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var rows = doc.DocumentNode.SelectNodes("//table[contains(@class,'tableList')]//tr");
            if (rows == null || rows.Count == 0)
                return null;

            // Find best matching result from search
            double? bestRating = null;
            double bestScore = 0;

            foreach (var row in rows.Take(5)) // Only check first 5 results
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

                var titleScore = TokenSimilarity(title, candidateTitle);
                var authorScore = CandidateAuthorScore(authors, candidateAuthors);
                var score = (titleScore * 0.7) + (authorScore * 0.3);

                // Only accept if score is reasonable (> 0.5) and better than previous
                if (score > 0.5 && score > bestScore && rating.HasValue)
                {
                    bestScore = score;
                    bestRating = rating;
                }
            }

            return bestRating;
        }
        catch (Exception ex)
        {
            Log.Debug("[LibraryWatcher] Goodreads simple search failed: {Error}", ex.Message);
            return null;
        }
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
                AiEnrichedAt = root.TryGetProperty("aiEnrichedAt", out var ai) ? ai.GetString() : null,
                EnrichmentComplete = root.TryGetProperty("enrichmentComplete", out var complete) &&
                    complete.ValueKind == JsonValueKind.True
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

    private sealed record AiValidationAndEnrichment(
        bool UseOpenLibrary,
        string? Title,
        string[] Authors,
        string? PublishedDate,
        string? Series,
        string? CoverUrl);

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
        public bool EnrichmentComplete { get; init; }

        public bool HasCoreMetadata =>
            !string.IsNullOrWhiteSpace(Title) &&
            Authors != null &&
            Authors.Length > 0;
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

    /// <summary>
    /// Returns true if the coverUrl is a local cover stored in _covers/ directory.
    /// Local covers should never be overwritten by external URLs.
    /// </summary>
    private static bool IsLocalCover(string? coverUrl)
    {
        return !string.IsNullOrWhiteSpace(coverUrl) &&
               coverUrl.StartsWith("_covers/", StringComparison.OrdinalIgnoreCase);
    }
}
