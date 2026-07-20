using System.Collections.Concurrent;

namespace AnnasArchive.API.Infrastructure;

/// <summary>
/// Centralized registry for all application caches.
/// Provides unified access to cache statistics and management operations.
/// </summary>
public class CacheRegistry
{
    private readonly ConcurrentDictionary<string, ICacheInfo> _caches = new();

    /// <summary>
    /// Registers a cache with the registry.
    /// </summary>
    /// <param name="name">Unique name for the cache</param>
    /// <param name="cache">Cache info provider</param>
    public void Register(string name, ICacheInfo cache)
    {
        _caches[name] = cache;
    }

    /// <summary>
    /// Gets statistics for all registered caches.
    /// </summary>
    public Dictionary<string, CacheStatistics> GetAllStatistics()
    {
        return _caches.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.GetStatistics()
        );
    }

    /// <summary>
    /// Clears all registered caches.
    /// </summary>
    public void ClearAll()
    {
        foreach (var cache in _caches.Values)
        {
            cache.Clear();
        }
    }

    /// <summary>
    /// Clears a specific cache by name.
    /// </summary>
    /// <param name="name">Name of the cache to clear</param>
    /// <returns>True if cache was found and cleared, false otherwise</returns>
    public bool Clear(string name)
    {
        if (_caches.TryGetValue(name, out var cache))
        {
            cache.Clear();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets statistics for a specific cache.
    /// </summary>
    /// <param name="name">Name of the cache</param>
    /// <returns>Statistics if found, null otherwise</returns>
    public CacheStatistics? GetStatistics(string name)
    {
        return _caches.TryGetValue(name, out var cache) ? cache.GetStatistics() : null;
    }

    /// <summary>
    /// Gets the names of all registered caches.
    /// </summary>
    public IEnumerable<string> CacheNames => _caches.Keys;
}

/// <summary>
/// Interface for cache info providers.
/// </summary>
public interface ICacheInfo
{
    CacheStatistics GetStatistics();
    void Clear();
}

/// <summary>
/// Wrapper to adapt LruCache to ICacheInfo.
/// </summary>
public class LruCacheInfo<TKey, TValue> : ICacheInfo where TKey : notnull
{
    private readonly LruCache<TKey, TValue> _cache;

    public LruCacheInfo(LruCache<TKey, TValue> cache)
    {
        _cache = cache;
    }

    public CacheStatistics GetStatistics() => _cache.GetStatistics();
    public void Clear() => _cache.Clear();
}
