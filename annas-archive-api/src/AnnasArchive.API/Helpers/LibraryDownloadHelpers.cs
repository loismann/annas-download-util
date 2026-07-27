namespace AnnasArchive.API.Helpers;

/// <summary>
/// Shared file-write logic for "send to library" downloads (Anna's Archive and
/// LibGen paths both use this).
/// </summary>
public static class LibraryDownloadHelpers
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destinationPath"/>, reporting
    /// progress via <paramref name="onProgress"/> as it goes. Writes to a ".partial"
    /// sibling file first and only renames it into place on success — a copy that fails
    /// or is aborted partway leaves no file at <paramref name="destinationPath"/>, so a
    /// retry can't be fooled by the existing-file check into skipping a corrupt download.
    /// </summary>
    public static async Task CopyToLibraryAtomicallyAsync(
        Stream source,
        string destinationPath,
        long? totalBytes,
        Action<long, long?> onProgress,
        CancellationToken cancellationToken = default)
    {
        var partialPath = destinationPath + ".partial";
        try
        {
            await using (var outStream = File.Create(partialPath))
            {
                var buffer = new byte[BufferSize];
                long totalRead = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await outStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;
                    onProgress(totalRead, totalBytes);
                }
            }

            File.Move(partialPath, destinationPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                try { File.Delete(partialPath); } catch { /* best-effort cleanup */ }
            }
            throw;
        }
    }
}
