using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnnasArchive.Core.Helpers;

/// <summary>
/// Thread-safe LRU (Least Recently Used) cache with configurable capacity and an
/// optional time-to-live.
/// </summary>
/// <remarks>
/// Lives in Core rather than the API project so both layers can share one bounded
/// cache: this replaced a second, near-identical implementation (TtlCache) that
/// existed only because Core cannot reference the API assembly.
///
/// Expiry is lazy — an expired entry is dropped when it is next looked up, and
/// expired entries are preferred over live ones when making room. There is no
/// background sweeper, so a key that is never read again occupies a slot until
/// eviction pressure reaches it. That is deliberate: these caches are small and
/// bounded, and a timer per cache would cost more than the slot.
/// </remarks>
/// <typeparam name="TKey">Type of cache keys</typeparam>
/// <typeparam name="TValue">Type of cached values</typeparam>
public class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly TimeSpan? _ttl;
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
        public DateTime CreatedAt { get; set; }
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
    /// Creates a new LRU cache.
    /// </summary>
    /// <param name="capacity">Maximum number of items to store. Must be at least 1.</param>
    /// <param name="ttl">
    /// How long an entry stays valid after it is written. Null means entries never
    /// expire and are only removed by LRU eviction.
    /// </param>
    /// <param name="keyComparer">
    /// Key comparison. Pass <see cref="StringComparer.OrdinalIgnoreCase"/> for caches
    /// keyed on user-facing text, where "Dune" and "dune" must share an entry.
    /// </param>
    public LruCache(int capacity, TimeSpan? ttl = null, IEqualityComparer<TKey>? keyComparer = null)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1");
        if (ttl is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive when supplied");

        _capacity = capacity;
        _ttl = ttl;
        _cache = keyComparer is null
            ? new ConcurrentDictionary<TKey, LinkedListNode<CacheEntry>>()
            : new ConcurrentDictionary<TKey, LinkedListNode<CacheEntry>>(keyComparer);
        _lruList = new LinkedList<CacheEntry>();
    }

    /// <summary>
    /// Maximum number of items the cache can hold.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Current number of items in the cache, including any that have expired but
    /// have not yet been looked up or evicted.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Number of cache hits (successful lookups).
    /// </summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>
    /// Number of cache misses (failed lookups). An expired entry counts as a miss.
    /// </summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Number of items evicted due to capacity limits or expiry.
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
    /// Attempts to get a value from the cache. An entry past its TTL is removed and
    /// reported as a miss, so a lookup never returns stale data.
    /// </summary>
    /// <param name="key">The key to look up</param>
    /// <param name="value">The cached value if found</param>
    /// <returns>True if the key was found and is still valid, false otherwise</returns>
    public bool TryGetValue(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                if (IsExpired(node.Value))
                {
                    RemoveNode(node);
                    Interlocked.Increment(ref _evictions);
                }
                else
                {
                    // Move to front (most recently used)
                    // Verify node is still in the list (could have been removed by another thread before lock)
                    if (node.List == _lruList)
                    {
                        node.Value.LastAccessedAt = DateTime.UtcNow;
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);
                    }
                    Interlocked.Increment(ref _hits);
                    value = node.Value.Value;
                    return true;
                }
            }
        }

        Interlocked.Increment(ref _misses);
        value = default;
        return false;
    }

    /// <summary>
    /// Adds or updates a value in the cache.
    /// If capacity is exceeded, expired entries are dropped first and only then is
    /// the least recently used live entry evicted.
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="value">The value to cache</param>
    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            // If key exists, update it and move to front. Resetting CreatedAt keeps
            // "TTL from last write", which is what every caller expects.
            if (_cache.TryGetValue(key, out var existingNode))
            {
                existingNode.Value.Value = value;
                existingNode.Value.CreatedAt = DateTime.UtcNow;
                existingNode.Value.LastAccessedAt = DateTime.UtcNow;
                _lruList.Remove(existingNode);
                _lruList.AddFirst(existingNode);
                return;
            }

            if (_cache.Count >= _capacity)
                PurgeExpired();

            // Evict if still at capacity
            while (_cache.Count >= _capacity && _lruList.Last != null)
            {
                RemoveNode(_lruList.Last);
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
    /// Note: Does not affect LRU ordering or statistics, and does not consider TTL.
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

    private bool IsExpired(CacheEntry entry) =>
        _ttl is { } ttl && DateTime.UtcNow - entry.CreatedAt > ttl;

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void PurgeExpired()
    {
        if (_ttl is null) return;

        var node = _lruList.Last;
        while (node is not null)
        {
            var previous = node.Previous;
            if (IsExpired(node.Value))
            {
                RemoveNode(node);
                Interlocked.Increment(ref _evictions);
            }
            node = previous;
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void RemoveNode(LinkedListNode<CacheEntry> node)
    {
        _cache.TryRemove(node.Value.Key, out _);
        if (node.List == _lruList)
            _lruList.Remove(node);
    }
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
