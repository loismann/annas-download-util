using AnnasArchive.API.Helpers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnnasArchive.API.HealthChecks;

/// <summary>
/// Health check for file system access (library storage and cache directories).
/// Verifies that required directories exist and are accessible.
/// </summary>
public class FileSystemHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var issues = new List<string>();
            var data = new Dictionary<string, object>();

            // Check library root
            var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
            data["libraryRoot"] = libraryRoot;

            if (!Directory.Exists(libraryRoot))
            {
                try
                {
                    Directory.CreateDirectory(libraryRoot);
                    data["libraryRootCreated"] = true;
                }
                catch (Exception ex)
                {
                    issues.Add($"Cannot create library root: {ex.Message}");
                }
            }

            if (Directory.Exists(libraryRoot))
            {
                // Verify write access by checking if we can list files
                try
                {
                    var fileCount = Directory.GetFiles(libraryRoot, "*.meta.json").Length;
                    data["libraryBookCount"] = fileCount;
                }
                catch (UnauthorizedAccessException)
                {
                    issues.Add("Library root is not readable");
                }
            }

            // Check EPUB cache root
            var epubCacheRoot = DropboxEpubCache.GetCacheRoot();
            data["epubCacheRoot"] = epubCacheRoot;

            if (!Directory.Exists(epubCacheRoot))
            {
                try
                {
                    Directory.CreateDirectory(epubCacheRoot);
                    data["epubCacheRootCreated"] = true;
                }
                catch (Exception ex)
                {
                    issues.Add($"Cannot create EPUB cache root: {ex.Message}");
                }
            }

            // Check covers directory
            var coversDir = Path.Combine(libraryRoot, "_covers");
            data["coversDir"] = coversDir;

            if (!Directory.Exists(coversDir))
            {
                try
                {
                    Directory.CreateDirectory(coversDir);
                    data["coversDirCreated"] = true;
                }
                catch (Exception ex)
                {
                    issues.Add($"Cannot create covers directory: {ex.Message}");
                }
            }

            // Determine overall health
            if (issues.Count > 0)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"File system issues: {string.Join("; ", issues)}",
                    data: data));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                "File system access verified",
                data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"File system health check failed: {ex.Message}",
                ex));
        }
    }
}
