namespace AnnasArchive.API.Helpers;

/// <summary>
/// One mutual-exclusion lock per key, with the entry removed once nobody holds
/// or wants it.
///
/// The obvious implementation — <c>ConcurrentDictionary&lt;string, SemaphoreSlim&gt;</c>
/// plus <c>GetOrAdd</c> — is what this replaces, and it never removes anything.
/// One <see cref="SemaphoreSlim"/> stays behind for every key ever seen: every
/// book opened in the reader, every item streamed. Small individually, unbounded
/// over the life of a process that is expected to run for months.
///
/// A capacity limit cannot fix it. Evicting a lock somebody is currently holding
/// hands the next caller a *different* semaphore for the same key, so both run at
/// once — the exact thing the lock exists to prevent — and the evicted holder
/// then releases a semaphore nothing is waiting on. So the count has to be exact:
/// an entry is removed only when the last interested caller has gone.
/// </summary>
public sealed class KeyedLocks
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        /// <summary>Callers holding the lock or queued for it. Incremented
        /// before waiting, so an entry is never collected out from under
        /// somebody still on their way in.</summary>
        public int Interested;
    }

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly object _gate = new();

    /// <summary>Live entries. For tests and diagnostics — the number this type
    /// exists to keep from growing.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// Waits for exclusive access to <paramref name="key"/>. Dispose the result
    /// to release it; a <c>using</c> is the only safe way to call this, since a
    /// missed release leaks the key permanently and blocks every later caller.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        var entry = Reserve(key);

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            // Cancelled while queued: give up the reservation, or the key stays
            // forever precisely because the caller never got in.
            Forget(key, entry);
            throw;
        }

        return new Lease(this, key, entry);
    }

    private Entry Reserve(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Interested++;
            return entry;
        }
    }

    private void Forget(string key, Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Interested > 0) return;

            // The reference check matters: a later caller may already have
            // replaced a removed entry under the same key, and disposing that
            // one would break a lock somebody is currently holding.
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Lease(KeyedLocks owner, string key, Entry entry) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            // Disposing twice must not release the semaphore twice — that would
            // let two callers in at once.
            if (_released) return;
            _released = true;

            entry.Semaphore.Release();
            owner.Forget(key, entry);
        }
    }
}
