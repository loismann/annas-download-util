using System.Text.Json;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for video library operations.
/// </summary>
public static class VideoHelpers
{
    private static readonly string[] SupportedVideoExtensions = { ".mp4", ".mkv", ".webm", ".avi", ".mov" };

    /// <summary>
    /// Resolves the root directory path for the video library.
    /// Checks YOUTUBE_DOWNLOAD_ROOT env var, then Synology default.
    /// </summary>
    public static string ResolveVideoRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("YOUTUBE_DOWNLOAD_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot;

        const string synologyDefault = "/volume1/media/YouTube";
        if (Directory.Exists(synologyDefault))
            return synologyDefault;

        return Path.Combine(AppContext.BaseDirectory, "videos");
    }

    /// <summary>
    /// Creates JSON serializer options for video metadata files.
    /// </summary>
    public static JsonSerializerOptions CreateVideoJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    /// <summary>
    /// Checks if a file is a supported video format.
    /// </summary>
    public static bool IsSupportedVideoFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return SupportedVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Formats a duration in seconds to a human-readable string (e.g., "1:23:45").
    /// </summary>
    public static string FormatDuration(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    /// <summary>
    /// Formats a file size in bytes to a human-readable string.
    /// </summary>
    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
            return "0B";

        string[] units = { "B", "KB", "MB", "GB" };
        var size = (double)bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.0}{units[unitIndex]}";
    }

    /// <summary>
    /// Normalizes a thumbnail URL, converting relative paths to API URLs.
    /// </summary>
    public static string? NormalizeThumbnailUrl(string? thumbnailPath, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailPath))
            return null;

        if (thumbnailPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return thumbnailPath;

        var normalized = thumbnailPath.Replace("\\", "/").TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var encodedPath = string.Join("/", segments);

        return $"{baseUrl}/api/video-library/thumbnail/{encodedPath}";
    }

    /// <summary>
    /// Finds a local thumbnail file URL for a video.
    /// Looks for files like "video.jpg", "video.webp", "video.png" alongside the video.
    /// </summary>
    public static string? FindLocalThumbnailUrl(string videoRoot, string videoFileName, string baseUrl)
    {
        var baseName = Path.GetFileNameWithoutExtension(videoFileName);
        var thumbnailExtensions = new[] { ".jpg", ".jpeg", ".webp", ".png" };

        foreach (var ext in thumbnailExtensions)
        {
            var thumbPath = Path.Combine(videoRoot, baseName + ext);
            if (File.Exists(thumbPath))
            {
                var relativePath = Path.GetFileName(thumbPath);
                return NormalizeThumbnailUrl(relativePath, baseUrl);
            }
        }

        return null;
    }

    /// <summary>
    /// Writes video metadata to a JSON file alongside the video.
    /// </summary>
    public static async Task WriteVideoMetadataAsync(string videoRoot, VideoMeta meta)
    {
        var metaPath = Path.Combine(videoRoot, $"{meta.FileName}.meta.json");
        var jsonOptions = CreateVideoJsonOptions();
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, jsonOptions));
    }

    /// <summary>
    /// Reads video metadata from a JSON file.
    /// </summary>
    public static async Task<VideoMeta?> ReadVideoMetadataAsync(string metaPath)
    {
        if (!File.Exists(metaPath))
            return null;

        var jsonOptions = CreateVideoJsonOptions();
        var json = await File.ReadAllTextAsync(metaPath);
        return JsonSerializer.Deserialize<VideoMeta>(json, jsonOptions);
    }

    /// <summary>
    /// Gets the format string (extension without dot) for a video file.
    /// </summary>
    public static string GetVideoFormat(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? "unknown" : ext.TrimStart('.').ToUpperInvariant();
    }
}
