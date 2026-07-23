using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Storage stats for the app-wide footer. Deliberately reuses data sources
/// that already exist rather than adding a new disk-scanning service:
/// overall disk usage comes from the same physical pool the ebook library
/// already lives on (so no new volume mount is needed just for this),
/// Movies/TV sizes come from Radarr/Sonarr's own tracked sizeOnDisk figures
/// (they already know this — no reason to re-derive it ourselves).
/// </summary>
public static class SystemStatsEndpoints
{
    private const string StorageStatsCacheKey = "system-storage-stats";

    // Directory-size scanning (for the ebook library) and cross-service
    // calls aren't cheap to do on every page's footer load — a page nav
    // shouldn't trigger a full re-scan, and storage figures don't change
    // fast enough to need fresher-than-this data anyway.
    private static readonly TimeSpan StorageStatsCacheDuration = TimeSpan.FromMinutes(10);

    public static WebApplication MapSystemStatsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/storage", HandleGetStorageStats)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleGetStorageStats(
        ISonarrService sonarr, IRadarrService radarr, IMemoryCache cache)
    {
        if (cache.TryGetValue(StorageStatsCacheKey, out object? cached))
            return Results.Ok(cached);

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();

        long totalBytes = 0, freeBytes = 0;
        try
        {
            var drive = new DriveInfo(libraryRoot);
            totalBytes = drive.TotalSize;
            freeBytes = drive.AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            Log.Warning("[SystemStats] Could not read disk space for {Path}: {Message}", libraryRoot, ex.Message);
        }

        long booksBytes = 0;
        try
        {
            if (Directory.Exists(libraryRoot))
            {
                booksBytes = Directory.EnumerateFiles(libraryRoot, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[SystemStats] Could not sum library file sizes: {Message}", ex.Message);
        }

        long moviesBytes = 0;
        try
        {
            var movies = await radarr.GetAllMoviesAsync();
            moviesBytes = movies.Sum(m => (long?)m?["sizeOnDisk"] ?? 0);
        }
        catch (Exception ex)
        {
            Log.Warning("[SystemStats] Could not fetch Radarr movie sizes: {Message}", ex.Message);
        }

        long tvBytes = 0;
        try
        {
            var series = await sonarr.GetAllSeriesAsync();
            tvBytes = series.Sum(s => (long?)s?["statistics"]?["sizeOnDisk"] ?? 0);
        }
        catch (Exception ex)
        {
            Log.Warning("[SystemStats] Could not fetch Sonarr series sizes: {Message}", ex.Message);
        }

        var usedBytes = totalBytes - freeBytes;
        var percentFull = totalBytes > 0 ? Math.Round((double)usedBytes / totalBytes * 100, 1) : 0;

        var result = new
        {
            totalBytes,
            freeBytes,
            usedBytes,
            percentFull,
            moviesBytes,
            tvBytes,
            booksBytes
        };

        cache.Set(StorageStatsCacheKey, result, StorageStatsCacheDuration);
        return Results.Ok(result);
    }
}
