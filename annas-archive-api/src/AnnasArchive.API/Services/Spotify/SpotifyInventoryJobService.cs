using System.Collections.Concurrent;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyInventoryJobService
{
    SpotifyInventoryStatusDto Start(string ownerKey);
    SpotifyInventoryStatusDto GetStatus(string ownerKey);
    Task CancelAsync(string ownerKey);
}

/// <summary>
/// Runs inventory scans outside the request lifetime. Progress is written to SQLite,
/// so closing/reloading the browser does not cancel or hide the scan.
/// </summary>
public sealed class SpotifyInventoryJobService(
    IServiceScopeFactory scopeFactory,
    ISpotifyInventoryStore store,
    TimeProvider timeProvider) : ISpotifyInventoryJobService
{
    private readonly ConcurrentDictionary<string, InventoryRun> _running = new(StringComparer.Ordinal);
    private readonly object _startLock = new();

    public SpotifyInventoryStatusDto Start(string ownerKey)
    {
        lock (_startLock)
        {
            if (_running.TryGetValue(ownerKey, out var existing) && !existing.Task.IsCompleted)
                return store.GetStatus(ownerKey);

            var now = timeProvider.GetUtcNow();
            var queued = new SpotifyInventoryStatusDto(
                Guid.NewGuid().ToString("N"), SpotifyInventoryJobState.Queued,
                0, 0, 0, 0, 0, now, now, null, store.GetLastInventoryAt(ownerKey),
                "Inventory refresh queued.");
            store.SaveStatus(ownerKey, queued);

            var cancellation = new CancellationTokenSource();
            var task = Task.Run(() => RunAsync(ownerKey, queued, cancellation.Token));
            _running[ownerKey] = new InventoryRun(task, cancellation);
            _ = task.ContinueWith(
                completedTask =>
                {
                    _running.TryRemove(ownerKey, out _);
                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return queued;
        }
    }

    public SpotifyInventoryStatusDto GetStatus(string ownerKey)
    {
        var status = store.GetStatus(ownerKey);
        if (status.State is SpotifyInventoryJobState.Queued or SpotifyInventoryJobState.Running &&
            (!_running.TryGetValue(ownerKey, out var run) || run.Task.IsCompleted) &&
            status.UpdatedAt < timeProvider.GetUtcNow().AddMinutes(-2))
        {
            status = status with
            {
                State = SpotifyInventoryJobState.Failed,
                UpdatedAt = timeProvider.GetUtcNow(),
                CompletedAt = timeProvider.GetUtcNow(),
                Message = "The previous inventory refresh was interrupted by a server restart. Start it again."
            };
            store.SaveStatus(ownerKey, status);
        }
        return status;
    }

    public async Task CancelAsync(string ownerKey)
    {
        if (!_running.TryGetValue(ownerKey, out var run))
            return;

        await run.Cancellation.CancelAsync();
        try
        {
            await run.Task;
        }
        catch (OperationCanceledException)
        {
            // Disconnect/account replacement clears the persisted state after the
            // detached task has stopped, so old account data cannot race back in.
        }
        finally
        {
            _running.TryRemove(ownerKey, out _);
            run.Cancellation.Dispose();
        }
    }

    private async Task RunAsync(
        string ownerKey,
        SpotifyInventoryStatusDto queued,
        CancellationToken token)
    {
        var sync = new object();
        var readable = 0;
        var partial = 0;
        var unreadable = 0;
        var current = queued with
        {
            State = SpotifyInventoryJobState.Running,
            UpdatedAt = timeProvider.GetUtcNow(),
            Message = "Reading Spotify playlist metadata."
        };
        store.SaveStatus(ownerKey, current);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<ISpotifyInventoryService>();
            var library = await inventory.RefreshForOwnerAsync(ownerKey, (processed, total, contents) =>
            {
                token.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (contents.Access == SpotifyContentsAccess.Available) readable++;
                    else if (contents.Access == SpotifyContentsAccess.Partial) partial++;
                    else unreadable++;

                    current = current with
                    {
                        TotalPlaylists = total,
                        ProcessedPlaylists = processed,
                        ReadablePlaylists = readable,
                        PartialPlaylists = partial,
                        UnreadablePlaylists = unreadable,
                        UpdatedAt = timeProvider.GetUtcNow(),
                        Message = $"Read {processed} of {total} playlists."
                    };
                    store.SaveStatus(ownerKey, current);
                    if (processed == total || processed % 10 == 0)
                    {
                        Log.Information(
                            "[Spotify] Inventory progress {Processed}/{Total}; readable {Readable}, partial {Partial}, unavailable {Unavailable}",
                            processed, total, readable, partial, unreadable);
                    }
                }
            }, token);

            token.ThrowIfCancellationRequested();
            var completedAt = timeProvider.GetUtcNow();
            store.MarkFullInventory(ownerKey, completedAt);
            var finalState = library.All(item => item.Access == SpotifyContentsAccess.Available)
                ? SpotifyInventoryJobState.Complete
                : SpotifyInventoryJobState.Partial;
            current = current with
            {
                State = finalState,
                TotalPlaylists = library.Count,
                ProcessedPlaylists = library.Count,
                UpdatedAt = completedAt,
                CompletedAt = completedAt,
                LastInventoryAt = store.GetLastInventoryAt(ownerKey),
                Message = finalState == SpotifyInventoryJobState.Complete
                    ? $"Inventory complete: {library.Count} playlists read."
                    : $"Inventory finished with limitations: {partial} partial and {unreadable} unreadable playlists."
            };
            store.SaveStatus(ownerKey, current);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Spotify] Inventory refresh failed");
            var failedAt = timeProvider.GetUtcNow();
            store.SaveStatus(ownerKey, current with
            {
                State = SpotifyInventoryJobState.Failed,
                UpdatedAt = failedAt,
                CompletedAt = failedAt,
                Message = ex is SpotifyConnectionException
                    ? ex.Message
                    : "Inventory refresh failed. Existing cached playlist contents were preserved."
            });
        }
    }

    private sealed record InventoryRun(Task Task, CancellationTokenSource Cancellation);
}
