using Microsoft.Extensions.Hosting;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Shared scaffolding for the in-memory *.meta.json index caches
/// (LibraryIndexCache / VideoIndexCache): warm-on-startup hosted service,
/// FileSystemWatcher invalidation with debounce, single-rebuild guard, and
/// incremental update/remove. Derived classes supply the domain-specific
/// index build, per-request URL normalization, item key, and sort order.
/// </summary>
public abstract class MetaIndexCache<TDto> : IHostedService, IDisposable where TDto : class
{
    private readonly object _lock = new();
    private readonly string _name;
    private readonly string _rootPath;
    private readonly TimeSpan _debounceDelay;
    private List<TDto>? _cached;
    private DateTime _lastBuildTime = DateTime.MinValue;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _isRebuilding;

    /// <param name="debounceDelay">How long the watcher waits for the writes to stop
    /// before invalidating. An import writes a file per book, and each one raises
    /// several events, so reacting per event would rebuild the whole index hundreds of
    /// times. Two seconds in production; tests pass a short one rather than sleeping
    /// through the real thing.</param>
    protected MetaIndexCache(string name, string rootPath, TimeSpan? debounceDelay = null)
    {
        _name = name;
        _rootPath = rootPath;
        _debounceDelay = debounceDelay ?? TimeSpan.FromSeconds(2);
        InitializeWatcher();
    }

    /// <summary>
    /// Builds the full index from disk, with asset URLs left <b>relative</b>.
    ///
    /// <para>Deliberately takes no base URL. It used to take the calling request's,
    /// which meant the first request after an invalidation baked its own hostname
    /// into the shared cache — and <see cref="NormalizeUrls"/> returns anything
    /// already absolute untouched, so every other caller was then served covers
    /// pointing at that host until the next rebuild. Only the startup warm-up was
    /// safe, because it passed null. Removing the parameter makes the whole class
    /// of bug unrepresentable rather than merely absent.</para>
    /// </summary>
    protected abstract List<TDto> BuildIndex();

    /// <summary>Rewrites relative asset URLs (covers/thumbnails) against the request's base URL.</summary>
    protected abstract List<TDto> NormalizeUrls(List<TDto> items, string baseUrl);

    /// <summary>The unique key (file name) used by update/remove.</summary>
    protected abstract string KeyOf(TDto item);

    /// <summary>Re-applies the index's canonical sort order after an incremental add.</summary>
    protected abstract List<TDto> SortIndex(IEnumerable<TDto> items);

    /// <summary>Warms the cache on startup, in the background so startup isn't blocked.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Log.Information("[{Name}] Warming cache on startup...", _name);
                BuildAndStore();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[{Name}] Failed to warm cache on startup", _name);
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void InitializeWatcher()
    {
        try
        {
            if (!Directory.Exists(_rootPath))
            {
                Log.Warning("[{Name}] Root does not exist: {RootPath}", _name, _rootPath);
                return;
            }

            _watcher = new FileSystemWatcher(_rootPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                Filter = "*.meta.json",
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            _watcher.Created += OnFileChanged;
            _watcher.Changed += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            // Renamed too: RenamedEventArgs is a FileSystemEventArgs, and the only
            // thing either arm ever read was FullPath.
            _watcher.Renamed += OnFileChanged;

            Log.Information("[{Name}] FileSystemWatcher initialized for {RootPath}", _name, _rootPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[{Name}] Failed to initialize FileSystemWatcher", _name);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        ScheduleRebuild();
    }

    /// <summary>Restarts the debounce window. Protected so a test can drive the
    /// debounce directly: the watcher delivers events with OS latency measured in
    /// tens of milliseconds, which is enough to swamp any timing assertion made
    /// through a real file write.</summary>
    protected void ScheduleRebuild()
    {
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => InvalidateCache(), null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void InvalidateCache()
    {
        lock (_lock)
        {
            _cached = null;
            Log.Information("[{Name}] Cache invalidated", _name);
        }
    }

    /// <summary>Gets the cached items, rebuilding first if the cache is cold.</summary>
    protected List<TDto> GetItems(string baseUrl)
    {
        lock (_lock)
        {
            if (_cached != null)
            {
                return NormalizeUrls(_cached, baseUrl);
            }
        }

        // Build outside the lock to allow concurrent reads during rebuild
        return RebuildCache(baseUrl);
    }

    private List<TDto> RebuildCache(string baseUrl)
    {
        lock (_lock)
        {
            // Double-check after acquiring lock. Normalised on the way out, the
            // same as the fast path above — returning the raw cache here handed the
            // loser of a cold-cache race a set of relative, unusable URLs.
            if (_cached != null)
            {
                return NormalizeUrls(_cached, baseUrl);
            }

            if (_isRebuilding)
            {
                // Return empty while rebuilding to avoid blocking
                return new List<TDto>();
            }

            _isRebuilding = true;
        }

        try
        {
            var items = BuildAndStore();

            // The cache is host-agnostic; the host is applied per request.
            return NormalizeUrls(items, baseUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[{Name}] Failed to rebuild cache", _name);
            return new List<TDto>();
        }
        finally
        {
            // In a finally, not in both arms: a build that throws and leaves this set
            // wedges the cache into answering every future caller with an empty list,
            // because RebuildCache would take the "already rebuilding" branch forever.
            lock (_lock)
            {
                _isRebuilding = false;
            }
        }
    }

    /// <summary>Builds the index and publishes it, timing the work. Shared by the
    /// startup warm-up and the on-demand rebuild, which differ only in what they do
    /// with the result.</summary>
    private List<TDto> BuildAndStore()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = BuildIndex();

        lock (_lock)
        {
            _cached = items;
            _lastBuildTime = DateTime.UtcNow;
        }

        Log.Information("[{Name}] Index built in {ElapsedMs}ms with {Count} items",
            _name, sw.ElapsedMilliseconds, items.Count);
        return items;
    }

    /// <summary>
    /// Updates a single item in the cache without full rebuild.
    /// </summary>
    protected void UpdateItem(TDto updated)
    {
        lock (_lock)
        {
            if (_cached == null)
                return;

            var index = _cached.FindIndex(item =>
                string.Equals(KeyOf(item), KeyOf(updated), StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                _cached[index] = updated;
            }
            else
            {
                _cached.Add(updated);
                _cached = SortIndex(_cached);
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the one cached item with this key and
    /// leaves the rest of the index alone. Returns <c>false</c> if the cache is cold
    /// or does not hold the key, so the caller can decide whether that is a harmless
    /// no-op or needs an <see cref="InvalidateCache"/> to stop the index and the
    /// item's store disagreeing.
    ///
    /// <para>The change runs against the <b>stored</b> item, whose asset URLs are
    /// relative. Taking an item back out of <see cref="GetItems"/> and passing it to
    /// <see cref="UpdateItem"/> instead would write one request's hostname into the
    /// shared cache — the bug <see cref="BuildIndex"/> was reshaped to prevent.</para>
    /// </summary>
    protected bool TryUpdateItem(string key, Func<TDto, TDto> change)
    {
        lock (_lock)
        {
            var index = _cached?.FindIndex(item =>
                string.Equals(KeyOf(item), key, StringComparison.OrdinalIgnoreCase)) ?? -1;

            if (index < 0)
                return false;

            _cached![index] = change(_cached[index]);
            return true;
        }
    }

    /// <summary>
    /// Removes an item from the cache without full rebuild.
    /// </summary>
    protected void RemoveItem(string key)
    {
        lock (_lock)
        {
            if (_cached == null)
                return;

            _cached.RemoveAll(item =>
                string.Equals(KeyOf(item), key, StringComparison.OrdinalIgnoreCase));
        }
    }

    public DateTime LastBuildTime => _lastBuildTime;
    public bool IsCached => _cached != null;

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
