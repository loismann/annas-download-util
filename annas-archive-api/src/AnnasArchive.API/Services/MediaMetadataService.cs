using System.Text.Json;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>A household member's last playback position in an audiobook.</summary>
public record AudiobookProgress(double PositionSeconds, DateTime UpdatedAt);

/// <summary>Per-item editable metadata — who requested a Sonarr series / Radarr
/// movie, user-created genre tags (independent of whatever genres
/// Sonarr/Radarr themselves report from TheTVDB/TMDB), which household
/// member(s) have favorited it, and (audiobooks only) each member's resume
/// position.</summary>
public record MediaItemMetadata(List<string> Owners, List<string> Genres, List<string>? Favorites = null, Dictionary<string, AudiobookProgress>? Progress = null)
{
    public List<string> Favorites { get; set; } = Favorites ?? new List<string>();
    public Dictionary<string, AudiobookProgress> Progress { get; set; } = Progress ?? new Dictionary<string, AudiobookProgress>();
}

/// <summary>
/// Tracks owner(s) ("Paul"/"Mom"/"Dad", one or more), free-form genre tags,
/// favorites, and (audiobooks) resume position for a given Sonarr series /
/// Radarr movie / Audiobookshelf library item, the same way the ebook
/// library tags books by owner and genre. These tools own the actual media
/// files (and reorganize/rename them on import), so this is kept
/// out-of-band here — a small JSON file keyed by each tool's own record ID
/// — rather than tagging the files themselves.
///
/// <c>id</c> is a plain string because Audiobookshelf's library item ids are
/// UUIDs, unlike Sonarr/Radarr's integer ids — callers with an int id just
/// pass its ToString(); the stored key format ("{type}:{id}") is unchanged
/// either way, so no data migration was needed when this widened from int.
/// </summary>
public interface IMediaMetadataService
{
    void Set(string type, string id, MediaItemMetadata metadata);
    void AddOwner(string type, string id, string owner);
    void SetFavorite(string type, string id, string owner, bool favorited);
    void SetProgress(string type, string id, string owner, double positionSeconds);
    MediaItemMetadata? Get(string type, string id);
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

    public void Set(string type, string id, MediaItemMetadata metadata)
    {
        var key = $"{type}:{id}";
        lock (_fileLock)
        {
            var data = LoadUnsafe();
            if (IsEmpty(metadata))
                data.Remove(key);
            else
                data[key] = metadata;

            SaveUnsafe(data);
        }
    }

    public void AddOwner(string type, string id, string owner)
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

    public void SetFavorite(string type, string id, string owner, bool favorited)
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

            if (IsEmpty(existing))
                data.Remove(key);
            else
                data[key] = existing;

            SaveUnsafe(data);
        }
    }

    public void SetProgress(string type, string id, string owner, double positionSeconds)
    {
        var key = $"{type}:{id}";
        lock (_fileLock)
        {
            var data = LoadUnsafe();
            var existing = data.GetValueOrDefault(key) ?? new MediaItemMetadata(new List<string>(), new List<string>());
            existing.Progress[owner] = new AudiobookProgress(positionSeconds, DateTime.UtcNow);

            // Progress alone (no owner/genre/favorite) is still meaningful —
            // never auto-remove an entry that has it, unlike Set/SetFavorite.
            data[key] = existing;
            SaveUnsafe(data);
        }
    }

    public MediaItemMetadata? Get(string type, string id)
    {
        lock (_fileLock)
        {
            return LoadUnsafe().GetValueOrDefault($"{type}:{id}");
        }
    }

    private static bool IsEmpty(MediaItemMetadata metadata) =>
        metadata.Owners.Count == 0 && metadata.Genres.Count == 0 &&
        metadata.Favorites.Count == 0 && metadata.Progress.Count == 0;

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
