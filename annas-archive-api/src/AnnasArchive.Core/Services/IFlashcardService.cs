using System.Collections.Generic;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for managing flashcard persistence
/// </summary>
public interface IFlashcardService
{
    /// <summary>
    /// Gets the flashcard file path for a given Dropbox path
    /// </summary>
    (string cacheDir, string filePath) GetFlashcardPath(string dropboxPath);

    /// <summary>
    /// Loads flashcards from disk for a given Dropbox path
    /// </summary>
    List<FlashcardItem> LoadFlashcards(string dropboxPath);

    /// <summary>
    /// Saves flashcards to disk for a given Dropbox path
    /// </summary>
    void SaveFlashcards(string dropboxPath, List<FlashcardItem> cards);
}

/// <summary>
/// Flashcard item model
/// </summary>
public record FlashcardItem(string Term, string Definition, string Etymology, List<string> UsageExamples, string? Notes);
