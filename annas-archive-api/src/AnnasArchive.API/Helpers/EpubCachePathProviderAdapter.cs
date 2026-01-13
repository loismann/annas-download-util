using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Adapter that implements IEpubCachePathProvider using DropboxEpubCache.
/// </summary>
public class EpubCachePathProviderAdapter : IEpubCachePathProvider
{
    public string GetCacheRoot() => DropboxEpubCache.GetCacheRoot();
    public string ComputeHash(string value) => DropboxEpubCache.ComputeHashPublic(value);
}
