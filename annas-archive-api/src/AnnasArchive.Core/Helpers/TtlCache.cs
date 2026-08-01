// AnnasArchive.Core has neither implicit usings nor a nullable context enabled.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AnnasArchive.Core.Helpers;

/// <summary>
/// A small thread-safe cache with both a time-to-live and a hard capacity.
///
/// This replaced three hand-rolled copies of the same shape — a static
/// <c>Dictionary&lt;string, (DateTime fetchedAt, T value)&gt;</c>, a companion
/// lock object, and an inline expiry check at every read. Two of the three had
/// no capacity at all and grew for the life of the process; the third "bounded"
/// itself by calling <c>Clear()</c> on every entry the moment it filled up,
/// throwing away 2,000 live entries to make room for one.
///
/// Eviction is oldest-first by insertion time rather than true LRU. These are
/// best-effort lookup caches for third-party metadata, so the cost of evicting
/// a still-useful entry is one extra HTTP call — not worth the bookkeeping a
/// real LRU needs. Expired entries are purged first, so eviction usually costs
/// nothing at all.
/// </summary>
public sealed class TtlCache<TValue>
{
    private readonly struct Entry
    {
        public Entry(TValue value, DateTime storedAt)
        {
            Value = value;
            StoredAt = storedAt;
        }

        public TValue Value { get; }
        public DateTime StoredAt { get; }
    }

    private readonly Dictionary<string, Entry> _entries;
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    /// <param name="capacity">Maximum live entries. Must be positive.</param>
    /// <param name="ttl">How long an entry stays valid after being stored.</param>
    /// <param name="keyComparer">
    /// Defaults to ordinal case-insensitive, since every current caller was
    /// lower-casing its keys by hand before looking them up.
    /// </param>
    public TtlCache(int capacity, TimeSpan ttl, IEqualityComparer<string>? keyComparer = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be positive.");

        _capacity = capacity;
        _ttl = ttl;
        _entries = new Dictionary<string, Entry>(keyComparer ?? StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Live (non-expired entries are not distinguished) entry count.</summary>
    public int Count
    {
        get { lock (_lock) { return _entries.Count; } }
    }

    /// <summary>
    /// Returns the cached value when present and unexpired. An expired entry is
    /// dropped on the way out, so a miss never leaves stale data behind.
    /// </summary>
    public bool TryGet(string key, out TValue value)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (DateTime.UtcNow - entry.StoredAt <= _ttl)
                {
                    value = entry.Value;
                    return true;
                }

                _entries.Remove(key);
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Stores a value, making room first if the cache is full.
    /// </summary>
    public void Set(string key, TValue value)
    {
        lock (_lock)
        {
            if (_entries.Count >= _capacity && !_entries.ContainsKey(key))
                MakeRoom();

            _entries[key] = new Entry(value, DateTime.UtcNow);
        }
    }

    public void Clear()
    {
        lock (_lock) { _entries.Clear(); }
    }

    /// <summary>
    /// Caller must hold <see cref="_lock"/>. Drops everything already expired;
    /// only if that freed nothing does it evict the single oldest live entry.
    /// </summary>
    private void MakeRoom()
    {
        var cutoff = DateTime.UtcNow - _ttl;

        var expired = _entries
            .Where(kvp => kvp.Value.StoredAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
            _entries.Remove(key);

        if (_entries.Count < _capacity)
            return;

        var oldest = _entries.OrderBy(kvp => kvp.Value.StoredAt).First().Key;
        _entries.Remove(oldest);
    }
}
