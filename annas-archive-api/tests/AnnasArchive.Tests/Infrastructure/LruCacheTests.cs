using AnnasArchive.API.Infrastructure;

namespace AnnasArchive.Tests.Infrastructure;

public class LruCacheTests
{
    [Fact]
    public void Constructor_WithValidCapacity_CreatesCache()
    {
        var cache = new LruCache<string, int>(10);

        cache.Capacity.Should().Be(10);
        cache.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new LruCache<string, int>(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WithNegativeCapacity_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new LruCache<string, int>(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Set_AddsItemToCache()
    {
        var cache = new LruCache<string, int>(10);

        cache.Set("key1", 42);

        cache.Count.Should().Be(1);
        cache.TryGetValue("key1", out var value).Should().BeTrue();
        value.Should().Be(42);
    }

    [Fact]
    public void Set_UpdatesExistingItem()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 42);

        cache.Set("key1", 100);

        cache.Count.Should().Be(1);
        cache.TryGetValue("key1", out var value).Should().BeTrue();
        value.Should().Be(100);
    }

    [Fact]
    public void Set_EvictsLeastRecentlyUsedWhenAtCapacity()
    {
        var cache = new LruCache<string, int>(3);
        cache.Set("key1", 1);
        cache.Set("key2", 2);
        cache.Set("key3", 3);

        // Add fourth item, should evict key1 (least recently used)
        cache.Set("key4", 4);

        cache.Count.Should().Be(3);
        cache.ContainsKey("key1").Should().BeFalse();
        cache.ContainsKey("key2").Should().BeTrue();
        cache.ContainsKey("key3").Should().BeTrue();
        cache.ContainsKey("key4").Should().BeTrue();
    }

    [Fact]
    public void TryGetValue_MovesItemToFront()
    {
        var cache = new LruCache<string, int>(3);
        cache.Set("key1", 1);
        cache.Set("key2", 2);
        cache.Set("key3", 3);

        // Access key1, making it most recently used
        cache.TryGetValue("key1", out _);

        // Add key4, should evict key2 (now least recently used)
        cache.Set("key4", 4);

        cache.ContainsKey("key1").Should().BeTrue();
        cache.ContainsKey("key2").Should().BeFalse();
        cache.ContainsKey("key3").Should().BeTrue();
        cache.ContainsKey("key4").Should().BeTrue();
    }

    [Fact]
    public void TryGetValue_ReturnsFalseForMissingKey()
    {
        var cache = new LruCache<string, int>(10);

        var result = cache.TryGetValue("missing", out var value);

        result.Should().BeFalse();
        value.Should().Be(default);
    }

    [Fact]
    public void Remove_RemovesExistingItem()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 42);

        var removed = cache.Remove("key1");

        removed.Should().BeTrue();
        cache.Count.Should().Be(0);
        cache.ContainsKey("key1").Should().BeFalse();
    }

    [Fact]
    public void Remove_ReturnsFalseForMissingKey()
    {
        var cache = new LruCache<string, int>(10);

        var removed = cache.Remove("missing");

        removed.Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 1);
        cache.Set("key2", 2);
        cache.Set("key3", 3);

        cache.Clear();

        cache.Count.Should().Be(0);
        cache.ContainsKey("key1").Should().BeFalse();
    }

    [Fact]
    public void Clear_ResetsStatistics()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 1);
        cache.TryGetValue("key1", out _);
        cache.TryGetValue("missing", out _);

        cache.Clear();

        cache.Hits.Should().Be(0);
        cache.Misses.Should().Be(0);
        cache.Evictions.Should().Be(0);
    }

    [Fact]
    public void Statistics_TracksHitsAndMisses()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 1);

        cache.TryGetValue("key1", out _); // Hit
        cache.TryGetValue("key1", out _); // Hit
        cache.TryGetValue("missing", out _); // Miss

        cache.Hits.Should().Be(2);
        cache.Misses.Should().Be(1);
        cache.HitRatio.Should().BeApproximately(2.0 / 3.0, 0.01);
    }

    [Fact]
    public void Statistics_TracksEvictions()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("key1", 1);
        cache.Set("key2", 2);
        cache.Set("key3", 3); // Evicts key1
        cache.Set("key4", 4); // Evicts key2

        cache.Evictions.Should().Be(2);
    }

    [Fact]
    public void GetStatistics_ReturnsAllStats()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 1);
        cache.TryGetValue("key1", out _);
        cache.TryGetValue("missing", out _);

        var stats = cache.GetStatistics();

        stats.Capacity.Should().Be(10);
        stats.Count.Should().Be(1);
        stats.Hits.Should().Be(1);
        stats.Misses.Should().Be(1);
        stats.HitRatio.Should().Be(0.5);
    }

    [Fact]
    public void GetOrAdd_ReturnsExistingValue()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 42);
        var factoryCalled = false;

        var result = cache.GetOrAdd("key1", _ =>
        {
            factoryCalled = true;
            return 100;
        });

        result.Should().Be(42);
        factoryCalled.Should().BeFalse();
    }

    [Fact]
    public void GetOrAdd_CreatesAndCachesNewValue()
    {
        var cache = new LruCache<string, int>(10);

        var result = cache.GetOrAdd("key1", _ => 42);

        result.Should().Be(42);
        cache.TryGetValue("key1", out var cached).Should().BeTrue();
        cached.Should().Be(42);
    }

    [Fact]
    public async Task GetOrAddAsync_ReturnsExistingValue()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 42);
        var factoryCalled = false;

        var result = await cache.GetOrAddAsync("key1", async _ =>
        {
            factoryCalled = true;
            await Task.Delay(1);
            return 100;
        });

        result.Should().Be(42);
        factoryCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrAddAsync_CreatesAndCachesNewValue()
    {
        var cache = new LruCache<string, int>(10);

        var result = await cache.GetOrAddAsync("key1", async _ =>
        {
            await Task.Delay(1);
            return 42;
        });

        result.Should().Be(42);
        cache.TryGetValue("key1", out var cached).Should().BeTrue();
        cached.Should().Be(42);
    }

    [Fact]
    public void Indexer_GetReturnsValueOrDefault()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 42);

        cache["key1"].Should().Be(42);
        cache["missing"].Should().Be(default);
    }

    [Fact]
    public void Indexer_SetAddsOrUpdatesValue()
    {
        var cache = new LruCache<string, int>(10);

        cache["key1"] = 42;

        cache.TryGetValue("key1", out var value).Should().BeTrue();
        value.Should().Be(42);
    }

    [Fact]
    public void Keys_ReturnsAllKeys()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("key1", 1);
        cache.Set("key2", 2);
        cache.Set("key3", 3);

        var keys = cache.Keys.ToList();

        keys.Should().HaveCount(3);
        keys.Should().Contain("key1");
        keys.Should().Contain("key2");
        keys.Should().Contain("key3");
    }

    [Fact]
    public void Cache_IsThreadSafe()
    {
        var cache = new LruCache<int, int>(100);
        var tasks = new List<Task>();

        // Multiple writers
        for (var i = 0; i < 10; i++)
        {
            var offset = i * 100;
            tasks.Add(Task.Run(() =>
            {
                for (var j = 0; j < 100; j++)
                {
                    cache.Set(offset + j, offset + j);
                }
            }));
        }

        // Multiple readers
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (var j = 0; j < 1000; j++)
                {
                    cache.TryGetValue(j % 200, out _);
                }
            }));
        }

        // Should complete without deadlock or exception
        Task.WaitAll(tasks.ToArray());

        // Cache should be in a consistent state
        cache.Count.Should().BeLessThanOrEqualTo(100);
    }
}
