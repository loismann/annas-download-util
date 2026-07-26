using System.Collections.Concurrent;
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
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly string _name;
    private readonly string _rootPath;
    private readonly ConcurrentQueue<string> _pendingChanges = new();
    private List<TDto>? _cached;
    private DateTime _lastBuildTime = DateTime.MinValue;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _isRebuilding;

    protected MetaIndexCache(string name, string rootPath)
    {
        _name = name;
        _rootPath = rootPath;
        InitializeWatcher();
    }

    /// <summary>Builds the full index from disk. baseUrl is null during startup warm-up.</summary>
    protected abstract List<TDto> BuildIndex(string? baseUrl);

    /// <summary>Rewrites relative asset URLs (covers/thumbnails) against the request's base URL.</summary>
    protected abstract List<TDto> NormalizeUrls(List<TDto> items, string baseUrl);

    /// <summary>The unique key (file name) used by update/remove.</summary>
    protected abstract string KeyOf(TDto item);

    /// <summary>Re-applies the index's canonical sort order after an incremental add.</summary>
    protected abstract List<TDto> SortIndex(IEnumerable<TDto> items);

    /// <summary>
    /// Warm the cache on application startup (in the background so startup isn't blocked).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Log.Information("[{Name}] Warming cache on startup...", _name);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var items = BuildIndex(baseUrl: null);

                lock (_lock)
                {
                    _cached = items;
                    _lastBuildTime = DateTime.UtcNow;
                }

                sw.Stop();
                Log.Information("[{Name}] Cache warmed on startup in {ElapsedMs}ms with {Count} items",
                    _name, sw.ElapsedMilliseconds, items.Count);
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
            _watcher.Renamed += OnFileRenamed;

            Log.Information("[{Name}] FileSystemWatcher initialized for {RootPath}", _name, _rootPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[{Name}] Failed to initialize FileSystemWatcher", _name);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _pendingChanges.Enqueue(e.FullPath);
        ScheduleRebuild();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _pendingChanges.Enqueue(e.FullPath);
        ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => InvalidateCache(), null, DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void InvalidateCache()
    {
        lock (_lock)
        {
            _cached = null;
            while (_pendingChanges.TryDequeue(out _)) { }
            Log.Information("[{Name}] Cache invalidated", _name);
        }
    }

    /// <summary>
    /// Gets the cached items, rebuilding the cache if necessary.
    /// </summary>
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
            // Double-check after acquiring lock
            if (_cached != null)
            {
                return _cached;
            }

            if (_isRebuilding)
            {
                // Return empty while rebuilding to avoid blocking
                return new List<TDto>();
            }

            _isRebuilding = true;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Information("[{Name}] Starting cache rebuild...", _name);

        try
        {
            var items = BuildIndex(baseUrl);

            lock (_lock)
            {
                _cached = items;
                _lastBuildTime = DateTime.UtcNow;
                _isRebuilding = false;
            }

            sw.Stop();
            Log.Information("[{Name}] Cache rebuilt in {ElapsedMs}ms with {Count} items",
                _name, sw.ElapsedMilliseconds, items.Count);

            return items;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[{Name}] Failed to rebuild cache", _name);
            lock (_lock)
            {
                _isRebuilding = false;
            }
            return new List<TDto>();
        }
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
