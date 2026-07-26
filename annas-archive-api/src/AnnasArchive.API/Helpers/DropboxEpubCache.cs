using AnnasArchive.API.Models;
using Dropbox.Api;
using Dropbox.Api.Files;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Dropbox facade over the shared <see cref="EpubChapterCache"/> engine:
/// supplies EPUB bytes by downloading from Dropbox, plus Dropbox folder listing.
/// </summary>
public static class DropboxEpubCache
{
    private static EpubSource SourceFor(DropboxClient dropbox, string dropboxPath) => new(
        CacheKey: dropboxPath,
        Label: $"dropbox:{dropboxPath}",
        TitlePath: dropboxPath,
        FetchBytes: () => DownloadBytesAsync(dropbox, dropboxPath));

    private static async Task<byte[]> DownloadBytesAsync(DropboxClient dropbox, string dropboxPath)
    {
        var download = await dropbox.Files.DownloadAsync(dropboxPath);
        await using var dropboxStream = await download.GetContentAsStreamAsync();
        using var ms = new MemoryStream();
        await dropboxStream.CopyToAsync(ms);
        return ms.ToArray();
    }

    public static async Task<List<DropboxEpubFileDto>> ListDropboxEpubsAsync(
        DropboxClient dropbox,
        string folderPath)
    {
        var epubs = new List<DropboxEpubFileDto>();

        async Task ListLoop(ListFolderResult result)
        {
            foreach (var entry in result.Entries.OfType<FileMetadata>())
            {
                if (!entry.Name.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                    continue;

                epubs.Add(new DropboxEpubFileDto(
                    entry.Id,
                    entry.Name,
                    entry.PathDisplay ?? entry.PathLower ?? entry.Name,
                    (long)entry.Size,
                    entry.ServerModified));
            }

            if (result.HasMore)
            {
                var next = await dropbox.Files.ListFolderContinueAsync(result.Cursor);
                await ListLoop(next);
            }
        }

        var initial = await dropbox.Files.ListFolderAsync(
            new ListFolderArg(folderPath ?? string.Empty, recursive: true));
        await ListLoop(initial);

        return epubs
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Task<(CachedChapterIndex Index, string CacheDir)> GetOrBuildChapterIndexAsync(
        DropboxClient dropbox,
        string dropboxPath) =>
        EpubChapterCache.GetOrBuildChapterIndexAsync(SourceFor(dropbox, dropboxPath));

    public static Task EnsureCacheBuildAsync(DropboxClient dropbox, string dropboxPath, string cacheDir) =>
        EpubChapterCache.EnsureCacheBuildAsync(SourceFor(dropbox, dropboxPath), cacheDir);

    public static Task<DropboxCacheStatusDto> GetCacheStatusAsync(
        DropboxClient dropbox,
        string dropboxPath) =>
        EpubChapterCache.GetCacheStatusAsync(SourceFor(dropbox, dropboxPath));

    public static bool DeleteCache(string dropboxPath) =>
        EpubChapterCache.DeleteCache(dropboxPath);

    public static Task<List<DropboxSearchMatchDto>> SearchAsync(
        DropboxClient dropbox,
        string dropboxPath,
        string query) =>
        EpubChapterCache.SearchAsync(SourceFor(dropbox, dropboxPath), query);

    public static string GetCacheRoot() => EpubChapterCache.GetCacheRoot();
    public static string ComputeHashPublic(string value) => EpubChapterCache.ComputeHash(value);
}
