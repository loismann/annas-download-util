using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for managing flashcard persistence
/// </summary>
public class FlashcardService : IFlashcardService
{
    private readonly IEpubCachePathProvider _cachePathProvider;

    public FlashcardService(IEpubCachePathProvider cachePathProvider)
    {
        _cachePathProvider = cachePathProvider;
    }

    /// <summary>
    /// Gets the flashcard file path for a given Dropbox path
    /// </summary>
    public (string cacheDir, string filePath) GetFlashcardPath(string dropboxPath)
    {
        var cacheDir = Path.Combine(_cachePathProvider.GetCacheRoot(), _cachePathProvider.ComputeHash(dropboxPath));
        Directory.CreateDirectory(cacheDir);
        var filePath = Path.Combine(cacheDir, "flashcards.json");
        return (cacheDir, filePath);
    }

    /// <summary>
    /// Loads flashcards from disk for a given Dropbox path
    /// </summary>
    public List<FlashcardItem> LoadFlashcards(string dropboxPath)
    {
        try
        {
            var (_, filePath) = GetFlashcardPath(dropboxPath);
            if (!File.Exists(filePath)) return new List<FlashcardItem>();
            var json = File.ReadAllText(filePath);
            var cards = JsonSerializer.Deserialize<List<FlashcardItem>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return cards ?? new List<FlashcardItem>();
        }
        catch
        {
            return new List<FlashcardItem>();
        }
    }

    /// <summary>
    /// Saves flashcards to disk for a given Dropbox path
    /// </summary>
    public void SaveFlashcards(string dropboxPath, List<FlashcardItem> cards)
    {
        var (cacheDir, filePath) = GetFlashcardPath(dropboxPath);
        Directory.CreateDirectory(cacheDir);
        var json = JsonSerializer.Serialize(cards, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }
}
