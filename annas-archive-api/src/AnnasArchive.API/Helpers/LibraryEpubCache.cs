using AnnasArchive.API.Infrastructure;
using AnnasArchive.Core.Helpers;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Library facade over the shared <see cref="EpubChapterCache"/> engine:
/// supplies EPUB bytes from local library files (keyed by reader key), plus an
/// in-memory LRU cache for individual chapter contents.
/// </summary>
static class LibraryEpubCache
{
    // LRU cache for chapter content with configurable capacity
    private static LruCache<string, string> _chapterContentCache = new(100);

    private static EpubSource SourceFor(string filePath, string readerKey) => new(
        CacheKey: readerKey,
        Label: $"library:{filePath}",
        TitlePath: filePath,
        FetchBytes: () => File.ReadAllBytesAsync(filePath));

    public static Task<(CachedChapterIndex Index, string CacheDir)> GetOrBuildChapterIndexAsync(
        string filePath,
        string readerKey) =>
        EpubChapterCache.GetOrBuildChapterIndexAsync(SourceFor(filePath, readerKey));

    public static Task<(CachedChapterIndex Index, string CacheDir)> GetOrBuildChapterIndexQuickAsync(
        string filePath,
        string readerKey) =>
        EpubChapterCache.GetOrBuildChapterIndexQuickAsync(SourceFor(filePath, readerKey));

    public static Task EnsureCacheBuildAsync(string filePath, string readerKey, string cacheDir) =>
        EpubChapterCache.EnsureCacheBuildAsync(SourceFor(filePath, readerKey), cacheDir);

    public static Task<DropboxCacheStatusDto> GetCacheStatusAsync(
        string filePath,
        string readerKey) =>
        EpubChapterCache.GetCacheStatusAsync(SourceFor(filePath, readerKey));

    public static bool DeleteCache(string readerKey) =>
        EpubChapterCache.DeleteCache(readerKey);

    public static Task<List<DropboxSearchMatchDto>> SearchAsync(
        string filePath,
        string readerKey,
        string query) =>
        EpubChapterCache.SearchAsync(SourceFor(filePath, readerKey), query);

    public static string GetCacheRoot() => EpubChapterCache.GetCacheRoot();
    public static string ComputeHashPublic(string value) => EpubChapterCache.ComputeHash(value);

    public static async Task<string?> ReadChapterContentAsync(string filePath, int chapterId)
    {
        try
        {
            var sourceBytes = await File.ReadAllBytesAsync(filePath);
            var (_, flatChapters) = await EpubChapterCache.GetFlatChaptersAsync(sourceBytes, $"library:{filePath}", filePath);
            var target = flatChapters.FirstOrDefault(ch => ch.Id == chapterId);
            return target?.PlainText;
        }
        catch (ArgumentException ex)
        {
            Log.Information("[library] Invalid argument reading chapter {ChapterId} from {FilePath}: {ParamName}", chapterId, filePath, ex.ParamName);
            return null;
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to read chapter {ChapterId} from {FilePath}: {ErrorMessage}", chapterId, filePath, ex.Message);
            return null;
        }
    }

    public static async Task<string?> ReadChapterContentCachedAsync(string filePath, int chapterId)
    {
        var cacheKey = $"{filePath}::{chapterId}";
        if (_chapterContentCache.TryGetValue(cacheKey, out var cached) && cached != null)
            return cached;

        var content = await ReadChapterContentAsync(filePath, chapterId);
        if (content != null)
        {
            _chapterContentCache.Set(cacheKey, content);
        }
        return content;
    }

    /// <summary>
    /// Configures the chapter content cache with a new capacity.
    /// Called during application startup.
    /// </summary>
    /// <param name="capacity">Maximum number of chapters to cache</param>
    public static void ConfigureCache(int capacity)
    {
        if (capacity > 0)
        {
            _chapterContentCache = new LruCache<string, string>(capacity);
            Log.Information("[LibraryEpubCache] Chapter content cache configured with capacity {Capacity}", capacity);
        }
    }

    /// <summary>
    /// Gets the LRU cache for chapter content.
    /// Used for cache registry integration.
    /// </summary>
    public static LruCache<string, string> ChapterContentCache => _chapterContentCache;

    /// <summary>
    /// Gets statistics about the chapter content cache.
    /// </summary>
    public static CacheStatistics GetCacheStatistics() => _chapterContentCache.GetStatistics();

    /// <summary>
    /// Clears the chapter content cache.
    /// </summary>
    public static void ClearCache() => _chapterContentCache.Clear();
}
