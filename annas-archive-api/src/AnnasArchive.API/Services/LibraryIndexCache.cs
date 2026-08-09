using System.Collections.Concurrent;
using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Caches the library book index in memory to avoid reading thousands of files on each request.
/// Caching/watcher scaffolding lives in <see cref="MetaIndexCache{TDto}"/>; this class owns
/// the book-specific index build (including the personalization-overlay merge), cover URL
/// normalization, and server-side search.
/// </summary>
public class LibraryIndexCache : MetaIndexCache<LibraryBookDto>
{
    private readonly Data.BookPersonalizationStore? _personalization;

    // The personalization store is optional only so tests can construct the cache
    // without a database; in the real app DI always supplies it.
    public LibraryIndexCache(Data.BookPersonalizationStore? personalization = null)
        : base("LibraryIndexCache", LibraryHelpers.ResolveLibraryRoot())
    {
        _personalization = personalization;
    }

    /// <summary>
    /// Gets the cached library books, rebuilding the cache if necessary.
    /// </summary>
    public List<LibraryBookDto> GetBooks(string baseUrl) => GetItems(baseUrl);

    /// <summary>
    /// Updates a single book in the cache without full rebuild.
    /// </summary>
    public void UpdateBook(LibraryBookDto updatedBook) => UpdateItem(updatedBook);

    /// <summary>
    /// Removes a book from the cache without full rebuild.
    /// </summary>
    public void RemoveBook(string fileName) => RemoveItem(fileName);

    protected override string KeyOf(LibraryBookDto item) => item.FileName;

    protected override List<LibraryBookDto> SortIndex(IEnumerable<LibraryBookDto> items) =>
        items.OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase).ToList();

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
        bool favoritesOnly = false,
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
                        !Constants.HouseholdOwners.IsBookOwnerTag(g))
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

        // Favorites filter — cross-referenced against whichever owner tags are currently
        // active (matches the currently active owner filter buttons); if no owner filter is
        // active, anything favorited by any of the three household members counts.
        if (favoritesOnly)
        {
            var favoriteOwnerNames = (ownerTags ?? Array.Empty<string>())
                .Select(OwnerTagToName)
                .Where(n => n != null)
                .Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            filtered = filtered.Where(b =>
            {
                var favoritedBy = b.FavoritedBy ?? Array.Empty<string>();
                if (favoritedBy.Length == 0) return false;
                return favoriteOwnerNames.Count == 0 || favoritedBy.Any(favoriteOwnerNames.Contains);
            });
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

    /// <summary>Maps a book owner tag ("Paul's Books") to the bare household-member name ("Paul") used by FavoritedBy.</summary>
    private static string? OwnerTagToName(string tag) => Constants.HouseholdOwners.NameForBookTag(tag);

    /// <summary>
    /// Normalizes cover URLs with the actual base URL.
    /// This is needed because the cache may be built before we know the base URL.
    /// </summary>
    protected override List<LibraryBookDto> NormalizeUrls(List<LibraryBookDto> items, string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return items;

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();

        return items.Select(book =>
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

    protected override List<LibraryBookDto> BuildIndex(string? baseUrl)
    {
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return new List<LibraryBookDto>();

        // User edits live in SQLite (see BookPersonalizationStore); the sidecars hold
        // enrichment facts. The index is where the two views get merged — DB wins
        // per-field wherever the user has expressed an opinion.
        _personalization?.ImportFromMetaFilesIfNeeded(libraryRoot);
        var overlays = _personalization?.LoadAll()
            ?? new Dictionary<string, Data.BookPersonalization>(StringComparer.OrdinalIgnoreCase);

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
                    // An absent base URL means "emit relative", which is what both
                    // helpers already do with an empty string — see NormalizeUrls.
                    var coverUrl = LibraryHelpers.NormalizeLibraryCoverUrl(meta.CoverUrl, baseUrl ?? string.Empty)
                        ?? LibraryHelpers.FindLocalCoverUrl(libraryRoot, meta.FileName, baseUrl ?? string.Empty);

                    var p = overlays.GetValueOrDefault(meta.FileName);
                    var genres = meta.Genres ?? Array.Empty<string>();
                    var tags = p?.Tags ?? meta.Tags ?? genres;
                    var primaryGenre = Data.BookPersonalizationStore.OverrideString(
                        p?.PrimaryGenre,
                        meta.PrimaryGenre ?? genres.FirstOrDefault() ?? tags.FirstOrDefault());

                    books.Add(new LibraryBookDto(
                        p?.Title ?? meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName),
                        p?.Authors ?? meta.Authors ?? Array.Empty<string>(),
                        meta.Format ?? Path.GetExtension(meta.FileName).TrimStart('.').ToUpperInvariant(),
                        meta.FileSize ?? "",
                        meta.FileName,
                        coverUrl,
                        meta.Source,
                        meta.Md5,
                        meta.SavedAt,
                        primaryGenre,
                        tags,
                        Data.BookPersonalizationStore.OverrideString(p?.Series, meta.Series),
                        genres,
                        meta.PublishedDate,
                        meta.Pages,
                        p?.GoodreadsRating ?? meta.GoodreadsRating,
                        p?.PersonalRating ?? meta.PersonalRating,
                        p?.ReaderEnabled ?? meta.ReaderEnabled,
                        p?.FavoritedBy ?? meta.FavoritedBy ?? Array.Empty<string>(),
                        p?.CullReviewedAt ?? meta.CullReviewedAt
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
                var p = overlays.GetValueOrDefault(fileName);
                books.Add(new LibraryBookDto(
                    p?.Title ?? Path.GetFileNameWithoutExtension(fileName),
                    p?.Authors ?? Array.Empty<string>(),
                    ext.TrimStart('.').ToUpperInvariant(),
                    LibraryHelpers.FormatFileSize(info.Length),
                    fileName,
                    null,
                    null,
                    null,
                    info.LastWriteTimeUtc,
                    Data.BookPersonalizationStore.OverrideString(p?.PrimaryGenre, null),
                    p?.Tags ?? Array.Empty<string>(),
                    Data.BookPersonalizationStore.OverrideString(p?.Series, null),
                    Array.Empty<string>(),
                    null,
                    null,
                    p?.GoodreadsRating,
                    p?.PersonalRating,
                    p?.ReaderEnabled,
                    p?.FavoritedBy ?? Array.Empty<string>(),
                    p?.CullReviewedAt
                ));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[LibraryIndexCache] Skipping file {FilePath}", filePath);
            }
        }

        return SortIndex(books);
    }
}
