using AnnasArchive.API.Services;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Deletes a video and its traces: the file, its metadata sidecar, and its
/// thumbnail. The counterpart to <see cref="LibraryBookDeletionHelper"/>, extracted
/// from the endpoint for the same reason — a private handler that deletes files off
/// disk cannot be tested without standing up the whole application.
///
/// <para>Unlike a book's covers, a thumbnail is addressed by the video's <b>base
/// name</b>, not its full file name: <c>Movie.mp4</c>'s thumbnail is
/// <c>Movie.jpg</c>. <see cref="VideoHelpers.FindLocalThumbnailUrl"/> resolves it the
/// same way, so the convention is consistent — but it does mean two videos differing
/// only in container share one thumbnail, and deleting either takes it from both.
/// That is a property of the naming scheme, not of this method; changing it would
/// orphan every thumbnail already on disk.</para>
/// </summary>
public static class VideoDeletionHelper
{
    /// <summary>Extensions a thumbnail may use, in the order
    /// <see cref="VideoHelpers.FindLocalThumbnailUrl"/> searches them.</summary>
    private static readonly string[] ThumbnailExtensions = { ".jpg", ".jpeg", ".webp", ".png" };

    /// <summary>Whether there was a video here to delete.</summary>
    public static bool DeleteVideoCompletely(string fileName, VideoIndexCache cache)
    {
        var safeFileName = Path.GetFileName(fileName);
        var videoRoot = VideoHelpers.ResolveVideoRoot();

        var videoPath = Path.Combine(videoRoot, safeFileName);
        var metaPath = Path.Combine(videoRoot, safeFileName + ".meta.json");

        // Every matching thumbnail, not just the first. Discovery stops at the first
        // extension it finds, so deleting only that one left any other format orphaned
        // — where it silently became the thumbnail for the next video to take that base
        // name, which is how a deleted film's artwork turns up on a different one.
        var baseName = Path.GetFileNameWithoutExtension(safeFileName);
        var thumbnails = ThumbnailExtensions
            .Select(ext => Path.Combine(videoRoot, baseName + ext))
            .Where(File.Exists)
            .ToList();

        var videoExists = File.Exists(videoPath);
        var metaExists = File.Exists(metaPath);

        if (!videoExists && !metaExists && thumbnails.Count == 0)
            return false;

        if (videoExists)
            File.Delete(videoPath);

        if (metaExists)
            File.Delete(metaPath);

        foreach (var thumbnail in thumbnails)
        {
            try
            {
                File.Delete(thumbnail);
            }
            catch (Exception ex)
            {
                // Logged rather than reported, as with book covers: a thumbnail that
                // will not delete leaves a stray file, not a video the viewer can
                // still play. Per file, so one stuck thumbnail does not strand another.
                Log.Warning(ex, "[VideoDeletion] Failed to delete thumbnail {Thumbnail}", thumbnail);
            }
        }

        Log.Information("[VideoDeletion] {FileName}: video={Video} sidecar={Sidecar} thumbnail={Thumbnail}",
            safeFileName, videoExists, metaExists, thumbnails.Count);

        cache.RemoveVideo(safeFileName);
        return true;
    }
}
