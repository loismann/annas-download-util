using System.Diagnostics;
using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The caching scaffolding under both library indexes: warm-on-startup, the
/// FileSystemWatcher and its debounce, the single-rebuild guard, and incremental
/// update/remove.
///
/// <para><c>LibraryIndexCache</c>'s search was covered; this base class was not, and
/// it is where the expensive behaviour lives. Two of its decisions are load-bearing
/// and neither is obvious from the outside: <b>a caller arriving during a rebuild is
/// answered with an empty list rather than made to wait</b>, and <b>the index is
/// stored with relative URLs</b> so no single request's hostname can leak into state
/// everyone shares.</para>
///
/// <para>Driven through a test double rather than the real thing: <c>BuildIndex</c>
/// can be counted, blocked and made to throw on demand, so every test here settles by
/// a signal or a polled condition rather than a sleep.</para>
///
/// <para><b>The watcher and the debounce are tested apart, on purpose.</b> Two tests
/// use real file writes and poll for the result — they prove the watcher is wired up
/// and filtered. The debounce itself is driven through <c>ScheduleRebuild</c>, because
/// the watcher's event latency is tens of milliseconds and swamps any timing assertion
/// made through a file write: a burst-of-writes test passes identically whether or not
/// the debounce exists, which was verified by removing it and watching the test still
/// pass. Splitting them is what makes each one fail for its own reason.</para>
/// </summary>
public sealed class MetaIndexCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "metaindex-tests", Guid.NewGuid().ToString("N"));

    private readonly List<TestCache> _caches = new();

    public MetaIndexCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var cache in _caches) cache.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>One indexed thing. A relative <see cref="Url"/> is the stored form;
    /// normalization is what makes it absolute, per caller.</summary>
    private sealed record Item(string Key, string Url);

    /// <summary>
    /// A cache whose build is fully under the test's control — countable, blockable,
    /// and able to fail. Everything else is the production class.
    /// </summary>
    private sealed class TestCache : MetaIndexCache<Item>
    {
        private readonly Func<List<Item>> _build;

        public TestCache(string root, Func<List<Item>> build, TimeSpan? debounce = null)
            : base("TestCache", root, debounce)
        {
            _build = build;
        }

        public int Builds;

        protected override List<Item> BuildIndex()
        {
            Interlocked.Increment(ref Builds);
            return _build();
        }

        /// <summary>Mirrors the real caches: an empty base URL means "leave it relative".</summary>
        protected override List<Item> NormalizeUrls(List<Item> items, string baseUrl) =>
            string.IsNullOrEmpty(baseUrl)
                ? items
                : items.Select(i => i with { Url = baseUrl + i.Url }).ToList();

        protected override string KeyOf(Item item) => item.Key;

        protected override List<Item> SortIndex(IEnumerable<Item> items) =>
            items.OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToList();

        public List<Item> Get(string baseUrl = "") => GetItems(baseUrl);
        public void Update(Item item) => UpdateItem(item);
        public bool TryUpdate(string key, Func<Item, Item> change) => TryUpdateItem(key, change);
        public void Remove(string key) => RemoveItem(key);

        /// <summary>A change event, without waiting on the filesystem to deliver one.</summary>
        public void SignalChange() => ScheduleRebuild();
    }

    private TestCache Cache(Func<List<Item>> build, TimeSpan? debounce = null)
    {
        var cache = new TestCache(_dir, build, debounce);
        _caches.Add(cache);
        return cache;
    }

    private TestCache Cache(params Item[] items) => Cache(() => items.ToList());

    private static Item Thing(string key = "a") => new(key, $"/covers/{key}.jpg");

    /// <summary>
    /// Waits for a condition rather than for a duration. A fixed sleep sized on an
    /// idle machine is the thing that turns a suite red under load; this is late only
    /// when the condition genuinely never arrives.
    /// </summary>
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(5);
        }
    }

    // ─── warming ──────────────────────────────────────────────────────────

    /// <summary>
    /// Startup must not block on the index. A cold library is thousands of files;
    /// building it inline would hold the whole app's startup behind disk I/O.
    /// </summary>
    [Fact]
    public async Task Warming_does_not_block_startup_and_leaves_the_cache_ready()
    {
        var cache = Cache(Thing());

        var started = Stopwatch.StartNew();
        await cache.StartAsync(CancellationToken.None);
        started.Stop();

        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "StartAsync hands the build to a background task and returns");

        WaitUntil(() => cache.IsCached, "the warm-up to finish");
        cache.LastBuildTime.Should().BeAfter(DateTime.MinValue);
    }

    /// <summary>A warm cache is not rebuilt by the first reader.</summary>
    [Fact]
    public async Task A_warmed_cache_serves_readers_without_building_again()
    {
        var cache = Cache(Thing());
        await cache.StartAsync(CancellationToken.None);
        WaitUntil(() => cache.IsCached, "the warm-up to finish");

        cache.Get();
        cache.Get();

        cache.Builds.Should().Be(1);
    }

    /// <summary>
    /// A failed warm-up must leave the cache cold, not empty-but-cached — the
    /// difference between "the next reader rebuilds" and "the library looks deleted
    /// until someone restarts the app".
    /// </summary>
    [Fact]
    public async Task A_warm_up_that_throws_leaves_the_cache_cold_rather_than_empty()
    {
        var cache = Cache(() => throw new IOException("the library is not mounted"));

        var start = async () => await cache.StartAsync(CancellationToken.None);

        await start.Should().NotThrowAsync("a failed warm-up must not take the app down");
        WaitUntil(() => cache.Builds == 1, "the warm-up to fail");
        cache.IsCached.Should().BeFalse();
    }

    // ─── the rebuild guard ────────────────────────────────────────────────

    /// <summary>
    /// The decision this class is really built on: <b>while one rebuild is in flight,
    /// everyone else is told the index is empty</b> rather than being made to wait.
    ///
    /// <para>It keeps a slow rebuild from stacking up blocked requests, and it is the
    /// reason a full invalidation is expensive enough to be worth avoiding — on a
    /// large library, anyone who loads the page mid-rebuild sees zero books. That
    /// tradeoff is deliberate, so it is pinned rather than left to be rediscovered.</para>
    /// </summary>
    [Fact]
    public void A_reader_arriving_during_a_rebuild_is_told_empty_rather_than_made_to_wait()
    {
        using var buildStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var cache = Cache(() =>
        {
            buildStarted.Set();
            release.Wait();
            return new List<Item> { Thing() };
        });

        var slow = Task.Run(() => cache.Get());
        buildStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the build should have begun");

        // The second read runs on its own task with a deadline. Without the guard it
        // would enter the blocked build and never return — and a test that hangs is
        // worse than one that fails, because it takes the whole suite with it.
        var second = Task.Run(() => cache.Get());
        second.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "the guard answers immediately instead of joining the rebuild");
        second.Result.Should().BeEmpty();

        release.Set();
        slow.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        slow.Result.Should().ContainSingle("the caller that triggered the rebuild gets the real index");
        cache.Builds.Should().Be(1, "the second caller must not start a rebuild of its own");
    }

    /// <summary>
    /// A build that throws must clear the in-flight flag. Leaving it set wedges the
    /// cache permanently: every later caller takes the "already rebuilding" branch and
    /// is answered empty forever, with no rebuild ever running to fix it. Only a
    /// restart would clear it, and the logs would show one failure and then silence.
    /// </summary>
    [Fact]
    public void A_build_that_throws_does_not_wedge_the_cache_into_answering_empty()
    {
        var fail = true;
        var cache = Cache(() => fail
            ? throw new IOException("the library went away")
            : new List<Item> { Thing() });

        cache.Get().Should().BeEmpty("the first build failed");

        fail = false;
        cache.Get().Should().ContainSingle("the next caller must be able to rebuild");
        cache.Builds.Should().Be(2);
    }

    /// <summary>
    /// The host is applied per caller, never stored. Two readers on different
    /// hostnames each get their own — the bug this class was reshaped to prevent is
    /// one request's host being baked into state everyone shares.
    /// </summary>
    [Fact]
    public void Each_reader_gets_its_own_host_applied_to_the_shared_index()
    {
        var cache = Cache(Thing());

        cache.Get("https://first.example").Single().Url
            .Should().Be("https://first.example/covers/a.jpg");
        cache.Get("https://second.example").Single().Url
            .Should().Be("https://second.example/covers/a.jpg");

        cache.Builds.Should().Be(1, "one index served two hosts");
    }

    // ─── the watcher and its debounce ─────────────────────────────────────

    /// <summary>
    /// A meta file appearing on disk invalidates the index. This is what makes a book
    /// imported by another process show up without a restart.
    /// </summary>
    [Fact]
    public void A_meta_file_appearing_invalidates_the_index()
    {
        var cache = Cache(() => new List<Item> { Thing() }, TimeSpan.FromMilliseconds(50));
        cache.Get();
        cache.IsCached.Should().BeTrue();

        File.WriteAllText(Path.Combine(_dir, "new-book.meta.json"), "{}");

        WaitUntil(() => !cache.IsCached, "the watcher to invalidate the cache");
    }

    /// <summary>
    /// What the debounce actually buys, which is narrower than it first looks.
    ///
    /// <para>Invalidating does not rebuild — the <i>next read</i> does. So a burst of
    /// writes with nobody reading costs one rebuild either way, and counting builds
    /// across a quiet burst cannot tell a debounced watcher from an undebounced one.
    /// The cost appears when reads interleave with the writes: an import running while
    /// somebody browses the library. Undebounced, every write drops the cache and the
    /// next read pays for a full rebuild — hundreds across one import.</para>
    ///
    /// <para>Driven through <c>ScheduleRebuild</c> rather than real file writes. The
    /// watcher delivers events with tens of milliseconds of OS latency, so a loop of
    /// writes finishes long before any event arrives and the cache is still warm
    /// whether or not anything is debounced — an assertion that passes for a reason
    /// unrelated to what it claims to test.</para>
    /// </summary>
    [Fact]
    public void A_burst_of_changes_keeps_a_readers_cache_until_the_writing_stops()
    {
        var cache = Cache(() => new List<Item> { Thing() }, TimeSpan.FromSeconds(5));
        cache.Get();

        for (var i = 0; i < 10; i++)
        {
            cache.SignalChange();
            cache.Get();
        }

        cache.IsCached.Should().BeTrue("each change restarts the window; none has closed yet");
        cache.Builds.Should().Be(1, "the reader never lost its cache, so never rebuilt");
    }

    /// <summary>Once the writing stops, the window closes and the index does get
    /// invalidated — the debounce delays the invalidation, it does not cancel it.</summary>
    [Fact]
    public void When_the_writing_stops_the_invalidation_still_arrives()
    {
        var cache = Cache(() => new List<Item> { Thing() }, TimeSpan.FromMilliseconds(50));
        cache.Get();

        for (var i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_dir, $"book{i}.meta.json"), "{}");

        WaitUntil(() => !cache.IsCached, "the debounced invalidation to fire");
    }

    /// <summary>Files the index does not read must not cost a rebuild. The watcher is
    /// filtered to *.meta.json; the book files themselves land in the same folder.</summary>
    [Fact]
    public void A_file_that_is_not_a_meta_file_is_ignored()
    {
        var cache = Cache(() => new List<Item> { Thing() }, TimeSpan.FromMilliseconds(50));
        cache.Get();

        File.WriteAllText(Path.Combine(_dir, "Some Book.epub"), "not metadata");
        Thread.Sleep(250);

        cache.IsCached.Should().BeTrue("only *.meta.json changes concern the index");
    }

    // ─── incremental maintenance ──────────────────────────────────────────

    /// <summary>
    /// Every incremental operation is a no-op on a cold cache — there is nothing to
    /// patch, and the next read rebuilds from the source of truth anyway. The
    /// alternative, materialising a cache from a single item, would publish an index
    /// containing one book.
    /// </summary>
    [Fact]
    public void Incremental_changes_to_a_cold_cache_do_nothing_rather_than_inventing_one()
    {
        var cache = Cache(Thing("a"), Thing("b"));

        cache.Update(Thing("c"));
        cache.Remove("a");
        cache.TryUpdate("a", i => i).Should().BeFalse("nothing is cached to patch");

        cache.IsCached.Should().BeFalse();
        cache.Get().Should().HaveCount(2, "the rebuild is what decides the contents");
    }

    /// <summary>An update for a key already present replaces it in place; a new key is
    /// added and the index re-sorted, so incremental adds do not drift out of order.</summary>
    [Fact]
    public void An_update_replaces_a_known_item_and_sorts_an_unknown_one_in()
    {
        var cache = Cache(Thing("b"));
        cache.Get();

        cache.Update(new Item("b", "/covers/changed.jpg"));
        cache.Update(Thing("a"));

        cache.Get().Select(i => i.Key).Should().Equal("a", "b");
        cache.Get().Single(i => i.Key == "b").Url.Should().Be("/covers/changed.jpg");
        cache.Builds.Should().Be(1, "incremental maintenance must not trigger a rebuild");
    }

    /// <summary>Keys are matched case-insensitively, the way file names are treated
    /// everywhere else in the library.</summary>
    [Theory]
    [InlineData("a")]
    [InlineData("A")]
    public void Incremental_changes_match_a_key_regardless_of_casing(string key)
    {
        var cache = Cache(Thing("a"));
        cache.Get();

        cache.TryUpdate(key, i => i with { Url = "/covers/patched.jpg" }).Should().BeTrue();
        cache.Get().Single().Url.Should().Be("/covers/patched.jpg");

        cache.Remove(key);
        cache.Get(). Should().BeEmpty();
    }
}
