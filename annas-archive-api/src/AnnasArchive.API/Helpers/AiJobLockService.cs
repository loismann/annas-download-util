using System.Collections.Concurrent;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Service for managing AI job locks to prevent duplicate concurrent operations.
/// </summary>
public interface IAiJobLockService
{
    /// <summary>
    /// Attempts to start an AI job with the given key. Returns true if lock acquired, false if job already in progress.
    /// </summary>
    bool TryStartJob(string key);

    /// <summary>
    /// Ends an AI job and releases the lock for the given key.
    /// </summary>
    void EndJob(string key);
}

/// <summary>
/// Thread-safe implementation of AI job lock service using ConcurrentDictionary.
/// </summary>
public class AiJobLockService : IAiJobLockService
{
    private static readonly ConcurrentDictionary<string, byte> _jobLocks = new();

    public bool TryStartJob(string key) => _jobLocks.TryAdd(key, 0);

    public void EndJob(string key) => _jobLocks.TryRemove(key, out _);
}
