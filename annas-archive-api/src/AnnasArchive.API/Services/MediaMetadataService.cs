using System.Text.Json;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>Per-item editable metadata — who requested a Sonarr series / Radarr
/// movie, user-created genre tags (independent of whatever genres
/// Sonarr/Radarr themselves report from TheTVDB/TMDB), and which household
/// member(s) have favorited it.</summary>
public record MediaItemMetadata(List<string> Owners, List<string> Genres, List<string>? Favorites = null)
{
    public List<string> Favorites { get; set; } = Favorites ?? new List<string>();
}

/// <summary>
/// Tracks owner(s) ("Paul"/"Mom"/"Dad", one or more) and free-form genre tags
/// for a given Sonarr series / Radarr movie, the same way the ebook library
/// tags books by owner and genre. Sonarr/Radarr own the actual media files
/// (and reorganize/rename them on import), so this is kept out-of-band here —
/// a small JSON file keyed by Sonarr/Radarr's own record ID — rather than
/// tagging the files themselves.
/// </summary>
public interface IMediaMetadataService
{
    void Set(string type, int id, MediaItemMetadata metadata);
    void AddOwner(string type, int id, string owner);
    void SetFavorite(string type, int id, string owner, bool favorited);
    MediaItemMetadata? Get(string type, int id);
    IReadOnlyDictionary<string, MediaItemMetadata> GetAll();
}

public class MediaMetadataService : IMediaMetadataService
{
    private readonly string _storagePath;
    private readonly object _fileLock = new();

    public MediaMetadataService(string storagePath)
    {
        _storagePath = storagePath;
    }

    public void Set(string type, int id, MediaItemMetadata metadata)
    {
        var key = $"{type}:{id}";
        lock (_fileLock)
        {
            var data = LoadUnsafe();
            if (metadata.Owners.Count == 0 && metadata.Genres.Count == 0 && metadata.Favorites.Count == 0)
                data.Remove(key);
            else
                data[key] = metadata;

            SaveUnsafe(data);
        }
    }

    public void AddOwner(string type, int id, string owner)
    {
        var key = $"{type}:{id}";
        lock (_fileLock)
        {
            var data = LoadUnsafe();
            var existing = data.GetValueOrDefault(key) ?? new MediaItemMetadata(new List<string>(), new List<string>());
            if (!existing.Owners.Contains(owner, StringComparer.OrdinalIgnoreCase))
                existing.Owners.Add(owner);

            data[key] = existing;
            SaveUnsafe(data);
        }
    }

    public void SetFavorite(string type, int id, string owner, bool favorited)
    {
        var key = $"{type}:{id}";
        lock (_fileLock)
        {
            var data = LoadUnsafe();
            var existing = data.GetValueOrDefault(key) ?? new MediaItemMetadata(new List<string>(), new List<string>());

            if (favorited)
            {
                if (!existing.Favorites.Contains(owner, StringComparer.OrdinalIgnoreCase))
                    existing.Favorites.Add(owner);
            }
            else
            {
                existing.Favorites.RemoveAll(o => string.Equals(o, owner, StringComparison.OrdinalIgnoreCase));
            }

            if (existing.Owners.Count == 0 && existing.Genres.Count == 0 && existing.Favorites.Count == 0)
                data.Remove(key);
            else
                data[key] = existing;

            SaveUnsafe(data);
        }
    }

    public MediaItemMetadata? Get(string type, int id)
    {
        lock (_fileLock)
        {
            return LoadUnsafe().GetValueOrDefault($"{type}:{id}");
        }
    }

    public IReadOnlyDictionary<string, MediaItemMetadata> GetAll()
    {
        lock (_fileLock)
        {
            return LoadUnsafe();
        }
    }

    private Dictionary<string, MediaItemMetadata> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_storagePath))
                return new Dictionary<string, MediaItemMetadata>();

            var json = File.ReadAllText(_storagePath);
            return JsonSerializer.Deserialize<Dictionary<string, MediaItemMetadata>>(json)
                ?? new Dictionary<string, MediaItemMetadata>();
        }
        catch (Exception ex)
        {
            Log.Warning("[MediaMetadata] Failed to load {Path}: {Message}", _storagePath, ex.Message);
            return new Dictionary<string, MediaItemMetadata>();
        }
    }

    /// <summary>Writes the metadata file, letting any I/O failure propagate to
    /// the caller. A swallowed write failure here previously meant a save could
    /// report success to the client (204) while never actually landing on
    /// disk — silently invisible until the next fetch quietly showed the old
    /// data, looking exactly like the edit had "vanished" hours later.</summary>
    private void SaveUnsafe(Dictionary<string, MediaItemMetadata> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_storagePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warning("[MediaMetadata] Failed to save {Path}: {Message}", _storagePath, ex.Message);
            throw;
        }
    }
}
