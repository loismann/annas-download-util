namespace AnnasArchive.Core.Services;

/// <summary>
/// Provider for EPUB cache paths
/// </summary>
public interface IEpubCachePathProvider
{
    /// <summary>
    /// Gets the root directory for EPUB cache
    /// </summary>
    string GetCacheRoot();

    /// <summary>
    /// Computes a hash for the given path
    /// </summary>
    string ComputeHash(string value);
}
