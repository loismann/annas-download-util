using System.Collections.Concurrent;
using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Caches the library book index in memory to avoid reading thousands of files on each request.
/// Implements IHostedService to warm the cache on application startup.
/// Uses FileSystemWatcher to invalidate cache when files change.
/// </summary>
public class LibraryIndexCache : IHostedService, IDisposable
{
    private readonly object _lock = new();
    private List<LibraryBookDto>? _cachedBooks;
    private DateTime _lastBuildTime = DateTime.MinValue;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentQueue<string> _pendingChanges = new();
    private Timer? _debounceTimer;
    private bool _isRebuilding;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2);

    public LibraryIndexCache()
    {
        InitializeWatcher();
    }

    /// <summary>
    /// Warm the cache on application startup.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Build cache in background so startup isn't blocked
        _ = Task.Run(() =>
        {
            try
            {
                Log.Information("[LibraryIndexCache] Warming cache on startup...");
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Build with empty baseUrl - URLs will be normalized on retrieval
                var books = BuildLibraryIndex(baseUrl: null);

                lock (_lock)
                {
                    _cachedBooks = books;
                    _lastBuildTime = DateTime.UtcNow;
                }

                sw.Stop();
                Log.Information("[LibraryIndexCache] Cache warmed on startup in {ElapsedMs}ms with {Count} books",
                    sw.ElapsedMilliseconds, books.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LibraryIndexCache] Failed to warm cache on startup");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void InitializeWatcher()
    {
        try
        {
            var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
            if (!Directory.Exists(libraryRoot))
            {
                Log.Warning("[LibraryIndexCache] Library root does not exist: {LibraryRoot}", libraryRoot);
                return;
            }

            _watcher = new FileSystemWatcher(libraryRoot)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                Filter = "*.meta.json",
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            _watcher.Created += OnFileChanged;
            _watcher.Changed += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;

            Log.Information("[LibraryIndexCache] FileSystemWatcher initialized for {LibraryRoot}", libraryRoot);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LibraryIndexCache] Failed to initialize FileSystemWatcher");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _pendingChanges.Enqueue(e.FullPath);
        ScheduleRebuild();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _pendingChanges.Enqueue(e.FullPath);
        ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                InvalidateCache();
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void InvalidateCache()
    {
        lock (_lock)
        {
            _cachedBooks = null;
            while (_pendingChanges.TryDequeue(out _)) { }
            Log.Information("[LibraryIndexCache] Cache invalidated");
        }
    }

    /// <summary>
    /// Gets the cached library books, rebuilding the cache if necessary.
    /// </summary>
    public List<LibraryBookDto> GetBooks(string baseUrl)
    {
        lock (_lock)
        {
            if (_cachedBooks != null)
            {
                // Normalize cover URLs with the actual base URL
                return NormalizeCoverUrls(_cachedBooks, baseUrl);
            }
        }

        // Build outside the lock to allow concurrent reads during rebuild
        return RebuildCache(baseUrl);
    }

    /// <summary>
    /// Gets a paginated list of library books.
    /// </summary>
    /// <param name="baseUrl">Base URL for normalizing cover URLs</param>
    /// <param name="skip">Number of books to skip (for pagination)</param>
    /// <param name="take">Number of books to return (0 = all)</param>
    /// <param name="sortBy">Sort field: "title", "date", "author"</param>
    /// <param name="sortDesc">Sort descending if true</param>
    /// <returns>Paginated result with books and total count</returns>
    public (List<LibraryBookDto> Books, int TotalCount) GetBooksPaginated(
        string baseUrl,
        int skip = 0,
        int take = 50,
        string sortBy = "date",
        bool sortDesc = true)
    {
        var allBooks = GetBooks(baseUrl);
        var totalCount = allBooks.Count;

        // Apply sorting
        IEnumerable<LibraryBookDto> sorted = sortBy.ToLowerInvariant() switch
        {
            "title" => sortDesc
                ? allBooks.OrderByDescending(b => b.Title, StringComparer.OrdinalIgnoreCase)
                : allBooks.OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase),
            "author" => sortDesc
                ? allBooks.OrderByDescending(b => b.Authors?.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase)
                : allBooks.OrderBy(b => b.Authors?.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase),
            "date" or _ => sortDesc
                ? allBooks.OrderByDescending(b => b.SavedAt ?? DateTime.MinValue)
                : allBooks.OrderBy(b => b.SavedAt ?? DateTime.MinValue)
        };

        // Apply pagination
        var paginated = sorted.Skip(skip);
        if (take > 0)
        {
            paginated = paginated.Take(take);
        }

        return (paginated.ToList(), totalCount);
    }

    /// <summary>
    /// Searches and filters library books with full server-side processing.
    /// This is the optimized endpoint for large libraries - all filtering, sorting, and pagination
    /// happens on the server so clients never need to load all books.
    /// </summary>
    public (List<LibraryBookDto> Books, int TotalCount, string[] AvailableGenres) SearchBooks(
        string baseUrl,
        string? searchTerm = null,
        string? genre = null,
        string[]? ownerTags = null,
        int minPersonalRating = 0,
        double minGoodreadsRating = 0,
        bool? bookmarked = null,
        bool? missingAuthor = null,
        bool? missingCover = null,
        int? genreCountLessThan = null,
        int? genreCountMoreThan = null,
        string sortBy = "date",
        bool sortDesc = true,
        int skip = 0,
        int take = 50)
    {
        var allBooks = GetBooks(baseUrl);

        // Build genre list before filtering for sidebar display
        var availableGenres = allBooks
            .SelectMany(b => (b.Tags ?? Array.Empty<string>()).Concat(new[] { b.PrimaryGenre ?? "" }))
            .Where(g => !string.IsNullOrWhiteSpace(g) &&
                        g != "Dad's Books" && g != "Mom's Books" && g != "Paul's Books")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Apply filters
        var filtered = allBooks.AsEnumerable();

        // Search term filter (searches title, authors, series, tags)
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim().ToLowerInvariant();
            filtered = filtered.Where(b =>
            {
                var haystack = string.Join(" ",
                    b.Title ?? "",
                    string.Join(" ", b.Authors ?? Array.Empty<string>()),
                    b.Series ?? "",
                    b.PrimaryGenre ?? "",
                    string.Join(" ", b.Tags ?? Array.Empty<string>())
                ).ToLowerInvariant();
                return haystack.Contains(term);
            });
        }

        // Genre filter
        if (!string.IsNullOrWhiteSpace(genre))
        {
            var genreLower = genre.ToLowerInvariant();
            filtered = filtered.Where(b =>
            {
                var primary = b.PrimaryGenre?.ToLowerInvariant() ?? "";
                var tags = (b.Tags ?? Array.Empty<string>()).Select(t => t.ToLowerInvariant());
                return primary == genreLower || tags.Contains(genreLower);
            });
        }

        // Owner tags filter (e.g., "Dad's Books", "Mom's Books")
        if (ownerTags != null && ownerTags.Length > 0)
        {
            var ownerTagsSet = new HashSet<string>(ownerTags, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(b =>
                (b.Tags ?? Array.Empty<string>()).Any(t => ownerTagsSet.Contains(t)));
        }

        // Personal rating filter
        if (minPersonalRating > 0)
        {
            filtered = filtered.Where(b => (b.PersonalRating ?? 0) >= minPersonalRating);
        }

        // Goodreads rating filter
        if (minGoodreadsRating > 0)
        {
            filtered = filtered.Where(b => (b.GoodreadsRating ?? 0) >= minGoodreadsRating);
        }

        // Bookmarked filter
        if (bookmarked == true)
        {
            filtered = filtered.Where(b => b.Bookmarked == true);
        }

        // Missing author filter
        if (missingAuthor == true)
        {
            filtered = filtered.Where(b =>
                b.Authors == null || b.Authors.Length == 0 ||
                b.Authors.All(a => string.IsNullOrWhiteSpace(a)));
        }

        // Missing cover filter
        if (missingCover == true)
        {
            filtered = filtered.Where(b => string.IsNullOrWhiteSpace(b.CoverUrl));
        }

        // Genre count filters
        if (genreCountLessThan.HasValue)
        {
            filtered = filtered.Where(b =>
            {
                var count = (b.Tags?.Length ?? 0) + (string.IsNullOrWhiteSpace(b.PrimaryGenre) ? 0 : 1);
                return count < genreCountLessThan.Value;
            });
        }
        if (genreCountMoreThan.HasValue)
        {
            filtered = filtered.Where(b =>
            {
                var count = (b.Tags?.Length ?? 0) + (string.IsNullOrWhiteSpace(b.PrimaryGenre) ? 0 : 1);
                return count > genreCountMoreThan.Value;
            });
        }

        // Sort-specific filters (series mode only shows books with series, stars mode only shows rated books)
        if (sortBy.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(b => !string.IsNullOrWhiteSpace(b.Series));
        }
        if (sortBy.Equals("stars", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(b => (b.PersonalRating ?? 0) >= 1);
        }
        if (sortBy.Equals("goodreads", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(b => (b.GoodreadsRating ?? 0) > 0);
        }

        // Materialize filtered list for count
        var filteredList = filtered.ToList();
        var totalCount = filteredList.Count;

        // Apply sorting
        IEnumerable<LibraryBookDto> sorted = sortBy.ToLowerInvariant() switch
        {
            "title" => sortDesc
                ? filteredList.OrderByDescending(b => b.Title, StringComparer.OrdinalIgnoreCase)
                : filteredList.OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase),
            "author" => sortDesc
                ? filteredList.OrderByDescending(b => b.Authors?.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase)
                : filteredList.OrderBy(b => b.Authors?.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase),
            "series" => sortDesc
                ? filteredList.OrderByDescending(b => b.Series ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(b => b.Title, StringComparer.OrdinalIgnoreCase)
                : filteredList.OrderBy(b => b.Series ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase),
            "stars" => sortDesc
                ? filteredList.OrderByDescending(b => b.PersonalRating ?? 0)
                    .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                : filteredList.OrderBy(b => b.PersonalRating ?? 0)
                    .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase),
            "goodreads" => sortDesc
                ? filteredList.OrderByDescending(b => b.GoodreadsRating ?? 0)
                    .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                : filteredList.OrderBy(b => b.GoodreadsRating ?? 0)
                    .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase),
            "date" or _ => sortDesc
                ? filteredList.OrderByDescending(b => b.SavedAt ?? DateTime.MinValue)
                : filteredList.OrderBy(b => b.SavedAt ?? DateTime.MinValue)
        };

        // Apply pagination
        var paginated = sorted.Skip(skip);
        if (take > 0)
        {
            paginated = paginated.Take(take);
        }

        return (paginated.ToList(), totalCount, availableGenres);
    }

    /// <summary>
    /// Normalizes cover URLs with the actual base URL.
    /// This is needed because the cache may be built before we know the base URL.
    /// </summary>
    private static List<LibraryBookDto> NormalizeCoverUrls(List<LibraryBookDto> books, string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return books;

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();

        return books.Select(book =>
        {
            // If cover URL is already absolute, return as-is
            if (book.CoverUrl?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true)
                return book;

            // If cover URL was built during cache warm-up with null baseUrl,
            // it will start with /api/library/cover/ - just prepend the baseUrl
            if (book.CoverUrl?.StartsWith("/api/library/cover/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var fullUrl = $"{baseUrl}{book.CoverUrl}";
                return book with { CoverUrl = fullUrl };
            }

            // Normalize the cover URL
            var normalizedUrl = LibraryHelpers.NormalizeLibraryCoverUrl(book.CoverUrl, baseUrl)
                ?? LibraryHelpers.FindLocalCoverUrl(libraryRoot, book.FileName, baseUrl);

            if (normalizedUrl == book.CoverUrl)
                return book;

            return book with { CoverUrl = normalizedUrl };
        }).ToList();
    }

    private List<LibraryBookDto> RebuildCache(string baseUrl)
    {
        lock (_lock)
        {
            // Double-check after acquiring lock
            if (_cachedBooks != null)
            {
                return _cachedBooks;
            }

            if (_isRebuilding)
            {
                // Return empty while rebuilding to avoid blocking
                return new List<LibraryBookDto>();
            }

            _isRebuilding = true;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Information("[LibraryIndexCache] Starting cache rebuild...");

        try
        {
            var books = BuildLibraryIndex(baseUrl);

            lock (_lock)
            {
                _cachedBooks = books;
                _lastBuildTime = DateTime.UtcNow;
                _isRebuilding = false;
            }

            sw.Stop();
            Log.Information("[LibraryIndexCache] Cache rebuilt in {ElapsedMs}ms with {Count} books",
                sw.ElapsedMilliseconds, books.Count);

            return books;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LibraryIndexCache] Failed to rebuild cache");
            lock (_lock)
            {
                _isRebuilding = false;
            }
            return new List<LibraryBookDto>();
        }
    }

    private static List<LibraryBookDto> BuildLibraryIndex(string baseUrl)
    {
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return new List<LibraryBookDto>();

        var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
        var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
        var books = new ConcurrentBag<LibraryBookDto>();
        var metaLookup = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Process meta files in parallel
        Parallel.ForEach(metaFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            metaFile =>
            {
                try
                {
                    var json = File.ReadAllText(metaFile);
                    var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                    if (meta == null)
                        return;

                    metaLookup.TryAdd(meta.FileName, true);
                    var coverUrl = LibraryHelpers.NormalizeLibraryCoverUrl(meta.CoverUrl, baseUrl)
                        ?? LibraryHelpers.FindLocalCoverUrl(libraryRoot, meta.FileName, baseUrl);

                    // Log first few cover URLs for debugging
                    if (books.Count < 5 && !string.IsNullOrEmpty(coverUrl))
                    {
                        Log.Information("[LibraryIndexCache] Sample coverUrl: meta.CoverUrl={MetaCoverUrl}, normalized={NormalizedUrl}",
                            meta.CoverUrl, coverUrl);
                    }

                    var genres = meta.Genres ?? Array.Empty<string>();
                    var tags = meta.Tags ?? genres;
                    var primaryGenre = meta.PrimaryGenre ?? genres.FirstOrDefault() ?? tags.FirstOrDefault();

                    books.Add(new LibraryBookDto(
                        meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName),
                        meta.Authors ?? Array.Empty<string>(),
                        meta.Format ?? Path.GetExtension(meta.FileName).TrimStart('.').ToUpperInvariant(),
                        meta.FileSize ?? "",
                        meta.FileName,
                        coverUrl,
                        meta.Source,
                        meta.Md5,
                        meta.SavedAt,
                        primaryGenre,
                        tags,
                        meta.Series,
                        genres,
                        meta.PublishedDate,
                        meta.Pages,
                        meta.GoodreadsRating,
                        meta.PersonalRating,
                        meta.ReaderEnabled,
                        meta.Bookmarked
                    ));
                }
                catch
                {
                    // Ignore malformed meta files
                }
            });

        // Process orphan book files (no meta)
        var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx", ".pobi", ".fb2" };

        foreach (var filePath in Directory.GetFiles(libraryRoot))
        {
            try
            {
                var ext = Path.GetExtension(filePath);
                if (!supportedExts.Contains(ext))
                    continue;

                var fileName = Path.GetFileName(filePath);
                if (string.IsNullOrWhiteSpace(fileName) || metaLookup.ContainsKey(fileName))
                    continue;

                var info = new FileInfo(filePath);
                books.Add(new LibraryBookDto(
                    Path.GetFileNameWithoutExtension(fileName),
                    Array.Empty<string>(),
                    ext.TrimStart('.').ToUpperInvariant(),
                    LibraryHelpers.FormatFileSize(info.Length),
                    fileName,
                    null,
                    null,
                    null,
                    info.LastWriteTimeUtc,
                    null,
                    Array.Empty<string>(),
                    null,
                    Array.Empty<string>(),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null
                ));
            }
            catch (Exception ex)
            {
                Log.Debug("[LibraryIndexCache] Skipping file {FilePath}: {Message}", filePath, ex.Message);
            }
        }

        return books
            .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Updates a single book in the cache without full rebuild.
    /// </summary>
    public void UpdateBook(LibraryBookDto updatedBook)
    {
        lock (_lock)
        {
            if (_cachedBooks == null)
                return;

            var index = _cachedBooks.FindIndex(b =>
                string.Equals(b.FileName, updatedBook.FileName, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                _cachedBooks[index] = updatedBook;
            }
            else
            {
                _cachedBooks.Add(updatedBook);
                _cachedBooks = _cachedBooks
                    .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Removes a book from the cache without full rebuild.
    /// </summary>
    public void RemoveBook(string fileName)
    {
        lock (_lock)
        {
            if (_cachedBooks == null)
                return;

            _cachedBooks.RemoveAll(b =>
                string.Equals(b.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public DateTime LastBuildTime => _lastBuildTime;
    public int CachedBookCount => _cachedBooks?.Count ?? 0;
    public bool IsCached => _cachedBooks != null;

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
