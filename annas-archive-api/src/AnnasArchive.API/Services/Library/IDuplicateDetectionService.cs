namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Service for detecting and managing duplicate book files in the library.
/// </summary>
public interface IDuplicateDetectionService
{
    /// <summary>
    /// Finds duplicate files in the library using a two-pass algorithm:
    /// Pass 1: Same-format duplicates by normalized title
    /// Pass 2: Cross-format duplicates when title + author match
    /// </summary>
    /// <param name="libraryRoot">The library root directory.</param>
    /// <param name="files">List of file paths to check for duplicates.</param>
    /// <returns>Set of file paths that are duplicates (should be deleted).</returns>
    HashSet<string> FindDuplicates(string libraryRoot, List<string> files);

    /// <summary>
    /// Deletes a library file and its associated artifacts (metadata file, cover image).
    /// </summary>
    /// <param name="libraryRoot">The library root directory.</param>
    /// <param name="filePath">Path to the file to delete.</param>
    void DeleteLibraryArtifacts(string libraryRoot, string filePath);

    /// <summary>
    /// Checks if a file would be a duplicate of an existing library book before import.
    /// Uses title and author matching against existing .meta.json files.
    /// </summary>
    /// <param name="libraryRoot">The library root directory.</param>
    /// <param name="title">Title of the book to check.</param>
    /// <param name="authors">Authors of the book to check.</param>
    /// <returns>Path to existing duplicate if found, null otherwise.</returns>
    string? FindExistingDuplicate(string libraryRoot, string title, string[] authors);

    /// <summary>
    /// Builds an index of existing library books for fast duplicate checking.
    /// Call this before processing a batch of imports.
    /// </summary>
    /// <param name="libraryRoot">The library root directory.</param>
    void BuildLibraryIndex(string libraryRoot);
}
