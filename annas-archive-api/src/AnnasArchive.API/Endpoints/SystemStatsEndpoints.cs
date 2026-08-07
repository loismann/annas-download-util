using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.PhotoPrint;
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
        // AdminOnly, not merely authenticated: server capacity is Paul's
        // operational detail, and hiding the panel in the UI would otherwise
        // leave the endpoint itself readable by any signed-in household member.
        app.MapGet("/api/system/storage", HandleGetStorageStats)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleGetStorageStats(
        ISonarrService sonarr,
        IRadarrService radarr,
        IAudiobookshelfService audiobookshelf,
        IImmichService immich,
        IMemoryCache cache)
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

        // Audiobookshelf tracks its own library total, so this is one cheap call
        // rather than summing ~1000 items — and the app container has no mount
        // for the audiobook folder to scan even if we wanted to.
        long audiobooksBytes = 0;
        try
        {
            audiobooksBytes = await audiobookshelf.GetLibrarySizeBytesAsync();
        }
        catch (Exception ex)
        {
            Log.Warning("[SystemStats] Could not fetch Audiobookshelf library size: {Message}", ex.Message);
        }

        // Immich likewise reports its own usage; it already swallows its failures
        // and returns 0, for the same "one dead service must not blank the whole
        // panel" reason as every other category here.
        var photosBytes = await immich.GetLibrarySizeBytesAsync();

        var usedBytes = totalBytes - freeBytes;
        var percentFull = totalBytes > 0 ? Math.Round((double)usedBytes / totalBytes * 100, 1) : 0;

        // Whatever is on the disk that no category above claims: downloads in
        // flight, Docker images, backups, the containers' own data. Derived by
        // subtraction rather than measured, so a category failing to report
        // inflates "Other" instead of silently shrinking the total — clamped at
        // zero because these figures come from five independent sources and need
        // not sum to exactly the disk's used bytes.
        var categorised = moviesBytes + tvBytes + booksBytes + audiobooksBytes + photosBytes;
        var otherBytes = Math.Max(0, usedBytes - categorised);

        var result = new
        {
            totalBytes,
            freeBytes,
            usedBytes,
            percentFull,
            moviesBytes,
            tvBytes,
            booksBytes,
            audiobooksBytes,
            photosBytes,
            otherBytes
        };

        cache.Set(StorageStatsCacheKey, result, StorageStatsCacheDuration);
        return Results.Ok(result);
    }
}
