namespace AnnasArchive.API.Infrastructure;

/// <summary>
/// Configuration options for application caches.
/// </summary>
public class CacheConfiguration
{
    public const string SectionName = "Caching";

    /// <summary>
    /// Maximum number of chapter contents to cache in memory.
    /// Default: 100 chapters
    /// </summary>
    public int ChapterContentCacheSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of author suggestion responses to cache.
    /// Default: 500 queries
    /// </summary>
    public int AuthorSuggestionCacheSize { get; set; } = 500;
}
