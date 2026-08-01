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
/// <param name="CoverUrl">(Audiobooks only) filename of a user-picked cover override,
/// relative to StoragePaths.AudiobookCoverOverrideRoot() — null means show whatever
/// the owning tool (Audiobookshelf) reports.</param>
/// <param name="Title">(Audiobooks only) a user-picked title override — null means
/// show whatever Audiobookshelf reports. TV/movie titles are never overridable.</param>
public record MediaItemMetadata(
    List<string> Owners,
    List<string> Genres,
    List<string>? Favorites = null,
    Dictionary<string, AudiobookProgress>? Progress = null,
    string? CoverUrl = null,
    string? Title = null)
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
    void SetCoverUrl(string type, string id, string? relativeCoverPath);
    /// <summary>Drops the whole record — used when the underlying item itself is
    /// deleted, so a future item that happens to reuse the same id (Sonarr/Radarr
    /// ids especially — Audiobookshelf's are UUIDs so this is only theoretical there)
    /// never inherits a deleted item's owners/favorites/overrides.</summary>
    void Delete(string type, string id);
    MediaItemMetadata? Get(string type, string id);
    IReadOnlyDictionary<string, MediaItemMetadata> GetAll();
}

public class MediaMetadataService : IMediaMetadataService
{
    private const string StateKey = "media-metadata";

    private readonly Data.AppDatabase _db;
    private readonly string? _legacyFilePath;
    private readonly object _fileLock = new();

    /// <summary>
    /// Persists to the SQLite app_state table (persistent /app/state mount). The legacy
    /// JSON file path, if given, is imported once on first read and then left alone —
    /// this data lived on the ephemeral container filesystem before 2026-07-23 and was
    /// wiped by every deploy (DOCS/reference/PROJECT_AUDIT.md §8.6b).
    /// </summary>
    public MediaMetadataService(Data.AppDatabase db, string? legacyFilePath = null)
    {
        _db = db;
        _legacyFilePath = legacyFilePath;
    }

    /// <summary>Full replace of Owners/Genres (e.g. removing a genre by omitting it
    /// genuinely clears it) — but Favorites/Progress/CoverUrl/Title are each managed
    /// independently by their own Set*/cover-endpoint calls, and every caller here
    /// only ever constructs `metadata` from Owners/Genres, leaving those at their
    /// default empty/null. A plain overwrite would therefore silently erase whatever
    /// was already there — e.g. saving a genre edit on a favorited show would wipe
    /// the favorite. Merge them forward from the existing record instead.</summary>
    public void Set(string type, string id, MediaItemMetadata metadata)
    {
        var key = $"{type}:{id}";
        lock (_fileLock)
        {
            var data = LoadUnsafe();
            var existing = data.GetValueOrDefault(key);
            var merged = metadata with
            {
                Favorites = existing?.Favorites ?? metadata.Favorites,
                Progress = existing?.Progress ?? metadata.Progress,
                CoverUrl = metadata.CoverUrl ?? existing?.CoverUrl,
                Title = metadata.Title ?? existing?.Title
            };

            if (IsEmpty(merged))
                data.Remove(key);
            else
                data[key] = merged;

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

    public void SetCoverUrl(string type, string id, string? relativeCoverPath)
    {
        var key = $"{type}:{id}";
        lock (_fileLock)
        {
            var data = LoadUnsafe();
            var existing = data.GetValueOrDefault(key) ?? new MediaItemMetadata(new List<string>(), new List<string>());
            existing = existing with { CoverUrl = relativeCoverPath };

            if (IsEmpty(existing))
                data.Remove(key);
            else
                data[key] = existing;

            SaveUnsafe(data);
        }
    }

    public void Delete(string type, string id)
    {
        var key = $"{type}:{id}";
        lock (_fileLock)
        {
            var data = LoadUnsafe();
            if (data.Remove(key))
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
        metadata.Favorites.Count == 0 && metadata.Progress.Count == 0 &&
        string.IsNullOrEmpty(metadata.CoverUrl) && string.IsNullOrEmpty(metadata.Title);

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
            var json = _db.GetState(StateKey);

            // One-time carry-over from the pre-SQLite JSON file, if present.
            if (json == null && _legacyFilePath != null && File.Exists(_legacyFilePath))
                json = File.ReadAllText(_legacyFilePath);

            if (json == null)
                return new Dictionary<string, MediaItemMetadata>();

            return JsonSerializer.Deserialize<Dictionary<string, MediaItemMetadata>>(json)
                ?? new Dictionary<string, MediaItemMetadata>();
        }
        catch (Exception ex)
        {
            Log.Warning("[MediaMetadata] Failed to load media metadata state: {Message}", ex.Message);
            return new Dictionary<string, MediaItemMetadata>();
        }
    }

    /// <summary>Persists the metadata, letting any I/O failure propagate to
    /// the caller. A swallowed write failure here previously meant a save could
    /// report success to the client (204) while never actually landing on
    /// disk — silently invisible until the next fetch quietly showed the old
    /// data, looking exactly like the edit had "vanished" hours later.</summary>
    private void SaveUnsafe(Dictionary<string, MediaItemMetadata> data)
    {
        try
        {
            _db.SetState(StateKey, JsonSerializer.Serialize(data));
        }
        catch (Exception ex)
        {
            Log.Warning("[MediaMetadata] Failed to save media metadata state: {Message}", ex.Message);
            throw;
        }
    }
}
