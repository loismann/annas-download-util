using System.Text.Json;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Helpers.Cache;

/// <summary>
/// Caching for character relationship graphs.
/// </summary>
public static class CharacterGraphCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetCharacterGraphCachePath(string dropboxPath)
    {
        var root = AiCacheBase.GetCacheRoot();
        var bookFolder = AiCacheBase.SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "character-graphs", bookFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "graph.json");
    }

    public static void SaveCharacterGraph(string dropboxPath, CharacterGraphResponse graph)
    {
        var path = GetCharacterGraphCachePath(dropboxPath);
        var json = JsonSerializer.Serialize(graph, JsonOptions);
        File.WriteAllText(path, json);
        Console.WriteLine($"Saved character graph to: {path}");
    }

    public static CharacterGraphResponse? LoadCharacterGraph(string dropboxPath)
    {
        var path = GetCharacterGraphCachePath(dropboxPath);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CharacterGraphResponse>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load character graph from {path}: {ex.Message}");
            return null;
        }
    }

    public static bool CharacterGraphExists(string dropboxPath)
    {
        var path = GetCharacterGraphCachePath(dropboxPath);
        return File.Exists(path);
    }
}
