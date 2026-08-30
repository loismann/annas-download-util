namespace AnnasArchive.API.Helpers;

/// <summary>
/// Single source for resolving every content-root directory. These were
/// previously duplicated verbatim across helpers, services, and endpoints
/// (library root ×2, audiobooks root ×2, EPUB cache root ×2) — a change to
/// one resolution rule silently missed its twin.
///
/// Resolution order is always: env var → known deployment default → local
/// fallback. The env vars are what docker-compose sets; the Synology/docker
/// defaults keep old deployments working; the local fallbacks keep `dotnet
/// run` on a dev machine functional.
/// </summary>
public static class StoragePaths
{
    public static string LibraryRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("LIBRARY_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot;

        const string synologyDefault = "/volume1/books/Library";
        if (Directory.Exists(synologyDefault))
            return synologyDefault;

        return Path.Combine(AppContext.BaseDirectory, "library");
    }

    public static string VideoRoot()
    {
        // docker-compose sets the .NET-config-style YouTube__DownloadRoot, not
        // YOUTUBE_DOWNLOAD_ROOT; both spellings stay accepted. The name is now
        // historical — the downloader that wrote here was deleted, and this is
        // simply where the existing video files live for the browse/metadata side.
        var envRoot = Environment.GetEnvironmentVariable("YOUTUBE_DOWNLOAD_ROOT")
            ?? Environment.GetEnvironmentVariable("YouTube__DownloadRoot");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot;

        const string synologyDefault = "/volume1/media/YouTube";
        if (Directory.Exists(synologyDefault))
            return synologyDefault;

        return Path.Combine(AppContext.BaseDirectory, "videos");
    }

    public static string AudiobooksRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("AUDIOBOOKS_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot;

        const string dockerDefault = "/audiobooks";
        return Directory.Exists(dockerDefault) ? dockerDefault : Path.Combine(AppContext.BaseDirectory, "audiobooks");
    }

    /// <summary>Where user-picked audiobook cover overrides are saved (see
    /// AudiobookLibraryEndpoints' cover endpoints). Deliberately NOT under
    /// AudiobooksRoot() — that path is the enrichment/rename service's staging
    /// folder for files Audiobookshelf will later scan, and dropping unrelated
    /// image files into it risks confusing that pipeline. This lives under the
    /// same persistent /app/state mount as the SQLite database instead, fully
    /// separate from both Audiobookshelf's and the enrichment service's files.</summary>
    public static string AudiobookCoverOverrideRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("AUDIOBOOK_COVERS_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot;

        return Directory.Exists("/app/state")
            ? "/app/state/audiobook-covers"
            : Path.Combine(AppContext.BaseDirectory, "state", "audiobook-covers");
    }

}
