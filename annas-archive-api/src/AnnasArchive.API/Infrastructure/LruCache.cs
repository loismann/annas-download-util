using System.Collections.Concurrent;

namespace AnnasArchive.API.Infrastructure;

/// <summary>
/// Thread-safe LRU (Least Recently Used) cache with configurable capacity.
/// Automatically evicts least recently accessed items when capacity is exceeded.
/// </summary>
/// <typeparam name="TKey">Type of cache keys</typeparam>
/// <typeparam name="TValue">Type of cached values</typeparam>
public class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<TKey, LinkedListNode<CacheEntry>> _cache;
    private readonly LinkedList<CacheEntry> _lruList;
    private readonly object _lock = new();

    // Statistics
    private long _hits;
    private long _misses;
    private long _evictions;

    private class CacheEntry
    {
        public TKey Key { get; }
        public TValue Value { get; set; }
        public DateTime CreatedAt { get; }
        public DateTime LastAccessedAt { get; set; }

        public CacheEntry(TKey key, TValue value)
        {
            Key = key;
            Value = value;
            CreatedAt = DateTime.UtcNow;
            LastAccessedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Creates a new LRU cache with the specified capacity.
    /// </summary>
    /// <param name="capacity">Maximum number of items to store. Must be at least 1.</param>
    public LruCache(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1");

        _capacity = capacity;
        _cache = new ConcurrentDictionary<TKey, LinkedListNode<CacheEntry>>();
        _lruList = new LinkedList<CacheEntry>();
    }

    /// <summary>
    /// Maximum number of items the cache can hold.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Current number of items in the cache.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Number of cache hits (successful lookups).
    /// </summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>
    /// Number of cache misses (failed lookups).
    /// </summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Number of items evicted due to capacity limits.
    /// </summary>
    public long Evictions => Interlocked.Read(ref _evictions);

    /// <summary>
    /// Cache hit ratio (0.0 to 1.0). Returns 0 if no lookups have occurred.
    /// </summary>
    public double HitRatio
    {
        get
        {
            var total = Hits + Misses;
            return total > 0 ? (double)Hits / total : 0;
        }
    }

    /// <summary>
    /// Gets or sets a value in the cache.
    /// </summary>
    public TValue? this[TKey key]
    {
        get => TryGetValue(key, out var value) ? value : default;
        set
        {
            if (value is not null)
                Set(key, value);
        }
    }

    /// <summary>
    /// Attempts to get a value from the cache.
    /// </summary>
    /// <param name="key">The key to look up</param>
    /// <param name="value">The cached value if found</param>
    /// <returns>True if the key was found, false otherwise</returns>
    public bool TryGetValue(TKey key, out TValue? value)
    {
        if (_cache.TryGetValue(key, out var node))
        {
            lock (_lock)
            {
                // Move to front (most recently used)
                node.Value.LastAccessedAt = DateTime.UtcNow;
                _lruList.Remove(node);
                _lruList.AddFirst(node);
            }
            Interlocked.Increment(ref _hits);
            value = node.Value.Value;
            return true;
        }

        Interlocked.Increment(ref _misses);
        value = default;
        return false;
    }

    /// <summary>
    /// Adds or updates a value in the cache.
    /// If capacity is exceeded, evicts the least recently used item.
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="value">The value to cache</param>
    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            // If key exists, update it and move to front
            if (_cache.TryGetValue(key, out var existingNode))
            {
                existingNode.Value.Value = value;
                existingNode.Value.LastAccessedAt = DateTime.UtcNow;
                _lruList.Remove(existingNode);
                _lruList.AddFirst(existingNode);
                return;
            }

            // Evict if at capacity
            while (_cache.Count >= _capacity && _lruList.Last != null)
            {
                var lru = _lruList.Last;
                _lruList.RemoveLast();
                _cache.TryRemove(lru.Value.Key, out _);
                Interlocked.Increment(ref _evictions);
            }

            // Add new entry
            var entry = new CacheEntry(key, value);
            var node = new LinkedListNode<CacheEntry>(entry);
            _lruList.AddFirst(node);
            _cache[key] = node;
        }
    }

    /// <summary>
    /// Gets an existing value or adds a new one using the provided factory.
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="valueFactory">Factory to create the value if not found</param>
    /// <returns>The cached or newly created value</returns>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        if (TryGetValue(key, out var existing) && existing is not null)
            return existing;

        var value = valueFactory(key);
        Set(key, value);
        return value;
    }

    /// <summary>
    /// Gets an existing value or adds a new one using the provided async factory.
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="valueFactory">Async factory to create the value if not found</param>
    /// <returns>The cached or newly created value</returns>
    public async Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> valueFactory)
    {
        if (TryGetValue(key, out var existing) && existing is not null)
            return existing;

        var value = await valueFactory(key);
        Set(key, value);
        return value;
    }

    /// <summary>
    /// Removes an item from the cache.
    /// </summary>
    /// <param name="key">The key to remove</param>
    /// <returns>True if the item was removed, false if not found</returns>
    public bool Remove(TKey key)
    {
        lock (_lock)
        {
            if (_cache.TryRemove(key, out var node))
            {
                _lruList.Remove(node);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Checks if the cache contains the specified key.
    /// Note: Does not affect LRU ordering or statistics.
    /// </summary>
    public bool ContainsKey(TKey key) => _cache.ContainsKey(key);

    /// <summary>
    /// Clears all items from the cache and resets statistics.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
            Interlocked.Exchange(ref _hits, 0);
            Interlocked.Exchange(ref _misses, 0);
            Interlocked.Exchange(ref _evictions, 0);
        }
    }

    /// <summary>
    /// Gets statistics about the cache.
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        return new CacheStatistics
        {
            Capacity = _capacity,
            Count = Count,
            Hits = Hits,
            Misses = Misses,
            Evictions = Evictions,
            HitRatio = HitRatio
        };
    }

    /// <summary>
    /// Gets all keys in the cache (snapshot at time of call).
    /// </summary>
    public IEnumerable<TKey> Keys => _cache.Keys;
}

/// <summary>
/// Statistics about an LRU cache.
/// </summary>
public record CacheStatistics
{
    public int Capacity { get; init; }
    public int Count { get; init; }
    public long Hits { get; init; }
    public long Misses { get; init; }
    public long Evictions { get; init; }
    public double HitRatio { get; init; }
}
