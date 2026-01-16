namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Service for classifying books into standard genres based on subject keywords.
/// </summary>
public interface IGenreClassificationService
{
    /// <summary>
    /// Maps a collection of subject keywords to a standard genre.
    /// </summary>
    /// <param name="subjects">Subject keywords from OpenLibrary or similar sources.</param>
    /// <returns>The best matching standard genre, or "Uncategorized" if no match.</returns>
    string ClassifyGenre(IEnumerable<string>? subjects);

    /// <summary>
    /// Extracts useful tags from subjects, excluding generic terms and the primary genre.
    /// </summary>
    /// <param name="subjects">Subject keywords from OpenLibrary or similar sources.</param>
    /// <param name="primaryGenre">The primary genre to exclude from tags.</param>
    /// <param name="limit">Maximum number of tags to return.</param>
    /// <returns>Array of relevant tags.</returns>
    string[] ExtractTags(IEnumerable<string>? subjects, string? primaryGenre, int limit = 5);
}
