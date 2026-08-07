using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Helpers;

namespace AnnasArchive.API.Services.Spotify;

public sealed record SpotifyKnownMusicOverride(string Kind, string Key, string DisplayName, bool IsKnown);

public interface ISpotifyInventoryStore
{
    void ClearOwner(string ownerKey);
    IReadOnlyList<SpotifyPlaylistDto>? GetMetadata(string ownerKey, TimeSpan maxAge);
    DateTimeOffset? GetLastInventoryAt(string ownerKey);
    void MarkFullInventory(string ownerKey, DateTimeOffset now);
    void SaveMetadata(string ownerKey, IReadOnlyList<SpotifyPlaylistDto> playlists, DateTimeOffset now);
    SpotifyPlaylistContents? GetCompleteContents(string ownerKey, SpotifyPlaylistDto playlist);
    void SaveContents(string ownerKey, SpotifyPlaylistContents contents, DateTimeOffset now);
    IReadOnlyList<SpotifyPlaylistContents> LoadLibrary(string ownerKey);
    SpotifyInventoryStatusDto GetStatus(string ownerKey);
    void SaveStatus(string ownerKey, SpotifyInventoryStatusDto status);
    T? GetSignal<T>(string ownerKey, string cacheKey, TimeSpan maxAge) where T : class;
    void SaveSignal<T>(string ownerKey, string cacheKey, T value, DateTimeOffset now) where T : class;
    IReadOnlyList<SpotifyKnownMusicOverride> GetKnownMusicOverrides(string ownerKey);
    void SaveKnownMusicOverride(string ownerKey, SpotifyKnownMusicOverride value, DateTimeOffset now);
}

/// <summary>
/// Tenant-keyed Spotify inventory state. Complete item lists are replaced only by
/// another complete read of the same snapshot; forbidden, unavailable and partial
/// reads update the access verdict without erasing previously known evidence.
/// </summary>
public sealed class SpotifyInventoryStore(AppDatabase database) : ISpotifyInventoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _writeLock = new();

    public void ClearOwner(string ownerKey)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var ownerHash = OwnerHash(ownerKey);

            foreach (var table in new[]
                     {
                         "spotify_inventory_meta",
                         "spotify_playlist_cache",
                         "spotify_inventory_job",
                         "spotify_known_music_override",
                         "spotify_signal_cache",
                         "spotify_discovery_draft"
                     })
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table} WHERE owner_hash = $owner";
                command.Parameters.AddWithValue("$owner", ownerHash);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<SpotifyPlaylistDto>? GetMetadata(string ownerKey, TimeSpan maxAge)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT playlists_json, last_inventory_at FROM spotify_inventory_meta WHERE owner_hash = $owner";
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !TryDate(reader.GetString(1), out var updatedAt) ||
            DateTimeOffset.UtcNow - updatedAt > maxAge)
            return null;

        return Deserialize<List<SpotifyPlaylistDto>>(reader.GetString(0));
    }

    public DateTimeOffset? GetLastInventoryAt(string ownerKey)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT full_inventory_at FROM spotify_inventory_meta WHERE owner_hash = $owner";
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        return command.ExecuteScalar() is string value && TryDate(value, out var result) ? result : null;
    }

    public void MarkFullInventory(string ownerKey, DateTimeOffset now)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE spotify_inventory_meta SET full_inventory_at = $now WHERE owner_hash = $owner";
            command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
            command.Parameters.AddWithValue("$now", now.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    public void SaveMetadata(string ownerKey, IReadOnlyList<SpotifyPlaylistDto> playlists, DateTimeOffset now)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var metadata = connection.CreateCommand())
            {
                metadata.Transaction = transaction;
                metadata.CommandText = """
                    INSERT INTO spotify_inventory_meta (owner_hash, playlists_json, last_inventory_at)
                    VALUES ($owner, $json, $now)
                    ON CONFLICT(owner_hash) DO UPDATE SET
                        playlists_json = $json, last_inventory_at = $now
                    """;
                metadata.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
                metadata.Parameters.AddWithValue("$json", JsonSerializer.Serialize(playlists, JsonOptions));
                metadata.Parameters.AddWithValue("$now", now.ToString("o"));
                metadata.ExecuteNonQuery();
            }

            foreach (var playlist in playlists)
            {
                using var row = connection.CreateCommand();
                row.Transaction = transaction;
                row.CommandText = """
                    INSERT INTO spotify_playlist_cache
                        (owner_hash, playlist_id, playlist_json, access, snapshot_id, inventory_at)
                    VALUES ($owner, $id, $playlist, $access, $snapshot, $now)
                    ON CONFLICT(owner_hash, playlist_id) DO UPDATE SET
                        playlist_json = $playlist,
                        snapshot_id = $snapshot,
                        inventory_at = $now
                    """;
                row.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
                row.Parameters.AddWithValue("$id", playlist.Id);
                row.Parameters.AddWithValue("$playlist", JsonSerializer.Serialize(playlist, JsonOptions));
                row.Parameters.AddWithValue("$access", playlist.ContentsAvailable
                    ? nameof(SpotifyContentsAccess.Available)
                    : nameof(SpotifyContentsAccess.Unavailable));
                row.Parameters.AddWithValue("$snapshot", (object?)playlist.SnapshotId ?? DBNull.Value);
                row.Parameters.AddWithValue("$now", now.ToString("o"));
                row.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public SpotifyPlaylistContents? GetCompleteContents(string ownerKey, SpotifyPlaylistDto playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist.SnapshotId))
            return null;

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT access, items_snapshot_id, items_json
            FROM spotify_playlist_cache
            WHERE owner_hash = $owner AND playlist_id = $id
            """;
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        command.Parameters.AddWithValue("$id", playlist.Id);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader["items_json"] is not string json ||
            reader["items_snapshot_id"] is not string cachedSnapshot ||
            !string.Equals(cachedSnapshot, playlist.SnapshotId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(0), nameof(SpotifyContentsAccess.Available), StringComparison.Ordinal))
            return null;

        var items = Deserialize<List<SpotifyPlaylistItemDto>>(json);
        return items == null ? null : new SpotifyPlaylistContents(
            playlist, items, SpotifyContentsAccess.Available, playlist.SnapshotId);
    }

    public void SaveContents(string ownerKey, SpotifyPlaylistContents contents, DateTimeOffset now)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();

            if (contents.Access == SpotifyContentsAccess.Available &&
                !string.IsNullOrWhiteSpace(contents.SnapshotId))
            {
                command.CommandText = """
                    INSERT INTO spotify_playlist_cache
                        (owner_hash, playlist_id, playlist_json, access, snapshot_id,
                         items_snapshot_id, items_json, inventory_at, items_updated_at)
                    VALUES ($owner, $id, $playlist, $access, $snapshot,
                            $snapshot, $items, $now, $now)
                    ON CONFLICT(owner_hash, playlist_id) DO UPDATE SET
                        playlist_json = $playlist, access = $access, snapshot_id = $snapshot,
                        items_snapshot_id = $snapshot, items_json = $items,
                        inventory_at = $now, items_updated_at = $now
                    """;
                command.Parameters.AddWithValue("$items", JsonSerializer.Serialize(contents.Items, JsonOptions));
            }
            else
            {
                // Deliberately leaves items_snapshot_id/items_json untouched.
                command.CommandText = """
                    INSERT INTO spotify_playlist_cache
                        (owner_hash, playlist_id, playlist_json, access, snapshot_id, inventory_at)
                    VALUES ($owner, $id, $playlist, $access, $snapshot, $now)
                    ON CONFLICT(owner_hash, playlist_id) DO UPDATE SET
                        playlist_json = $playlist, access = $access,
                        snapshot_id = $snapshot, inventory_at = $now
                    """;
            }

            command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
            command.Parameters.AddWithValue("$id", contents.Playlist.Id);
            command.Parameters.AddWithValue("$playlist", JsonSerializer.Serialize(contents.Playlist, JsonOptions));
            command.Parameters.AddWithValue("$access", contents.Access.ToString());
            command.Parameters.AddWithValue("$snapshot", (object?)contents.SnapshotId ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SpotifyPlaylistContents> LoadLibrary(string ownerKey)
    {
        var metadata = GetMetadata(ownerKey, TimeSpan.MaxValue);
        if (metadata == null)
            return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT playlist_json, access, snapshot_id, items_snapshot_id, items_json
            FROM spotify_playlist_cache WHERE owner_hash = $owner ORDER BY rowid
            """;
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, SpotifyPlaylistContents>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var playlist = Deserialize<SpotifyPlaylistDto>(reader.GetString(0));
            if (playlist == null)
                continue;

            var access = Enum.TryParse<SpotifyContentsAccess>(reader.GetString(1), out var parsed)
                ? parsed
                : SpotifyContentsAccess.Unavailable;
            var snapshot = reader["snapshot_id"] as string;
            var itemsSnapshot = reader["items_snapshot_id"] as string;
            var snapshotMatches = !string.IsNullOrWhiteSpace(snapshot) &&
                                  string.Equals(snapshot, itemsSnapshot, StringComparison.Ordinal);
            var items = access == SpotifyContentsAccess.Available && snapshotMatches &&
                        reader["items_json"] is string json
                ? Deserialize<List<SpotifyPlaylistItemDto>>(json) ?? []
                : [];
            if (access == SpotifyContentsAccess.Available && !snapshotMatches)
                access = SpotifyContentsAccess.Partial;

            rows[playlist.Id] = new SpotifyPlaylistContents(playlist, items, access, snapshot);
        }

        return metadata.Select(playlist => rows.TryGetValue(playlist.Id, out var contents)
                ? contents with { Playlist = playlist, SnapshotId = playlist.SnapshotId }
                : new SpotifyPlaylistContents(
                    playlist, [], SpotifyContentsAccess.Unavailable, playlist.SnapshotId))
            .ToList();
    }

    public SpotifyInventoryStatusDto GetStatus(string ownerKey)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM spotify_inventory_job WHERE owner_hash = $owner";
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new SpotifyInventoryStatusDto(null, SpotifyInventoryJobState.NotStarted,
                0, 0, 0, 0, 0, null, null, null, GetLastInventoryAt(ownerKey));

        var state = Enum.TryParse<SpotifyInventoryJobState>((string)reader["state"], out var parsed)
            ? parsed
            : SpotifyInventoryJobState.Failed;
        return new SpotifyInventoryStatusDto(
            reader["job_id"] as string,
            state,
            Convert.ToInt32(reader["total_playlists"]),
            Convert.ToInt32(reader["processed_playlists"]),
            Convert.ToInt32(reader["readable_playlists"]),
            Convert.ToInt32(reader["partial_playlists"]),
            Convert.ToInt32(reader["unreadable_playlists"]),
            Date(reader["started_at"]), Date(reader["updated_at"]), Date(reader["completed_at"]),
            GetLastInventoryAt(ownerKey),
            reader["message"] as string);
    }

    public void SaveStatus(string ownerKey, SpotifyInventoryStatusDto status)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO spotify_inventory_job
                    (owner_hash, job_id, state, total_playlists, processed_playlists,
                     readable_playlists, partial_playlists, unreadable_playlists,
                     started_at, updated_at, completed_at, message)
                VALUES ($owner, $job, $state, $total, $processed, $readable, $partial,
                        $unreadable, $started, $updated, $completed, $message)
                ON CONFLICT(owner_hash) DO UPDATE SET
                    job_id = $job, state = $state, total_playlists = $total,
                    processed_playlists = $processed, readable_playlists = $readable,
                    partial_playlists = $partial, unreadable_playlists = $unreadable,
                    started_at = $started, updated_at = $updated,
                    completed_at = $completed, message = $message
                """;
            command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
            command.Parameters.AddWithValue("$job", (object?)status.JobId ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", status.State.ToString());
            command.Parameters.AddWithValue("$total", status.TotalPlaylists);
            command.Parameters.AddWithValue("$processed", status.ProcessedPlaylists);
            command.Parameters.AddWithValue("$readable", status.ReadablePlaylists);
            command.Parameters.AddWithValue("$partial", status.PartialPlaylists);
            command.Parameters.AddWithValue("$unreadable", status.UnreadablePlaylists);
            command.Parameters.AddWithValue("$started", DbDate(status.StartedAt));
            command.Parameters.AddWithValue("$updated", DbDate(status.UpdatedAt));
            command.Parameters.AddWithValue("$completed", DbDate(status.CompletedAt));
            command.Parameters.AddWithValue("$message", (object?)status.Message ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public T? GetSignal<T>(string ownerKey, string cacheKey, TimeSpan maxAge) where T : class
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json, updated_at FROM spotify_signal_cache WHERE owner_hash = $owner AND cache_key = $key";
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        command.Parameters.AddWithValue("$key", cacheKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !TryDate(reader.GetString(1), out var updatedAt) || DateTimeOffset.UtcNow - updatedAt > maxAge)
            return null;
        return Deserialize<T>(reader.GetString(0));
    }

    public void SaveSignal<T>(string ownerKey, string cacheKey, T value, DateTimeOffset now) where T : class
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO spotify_signal_cache (owner_hash, cache_key, json, updated_at)
                VALUES ($owner, $key, $json, $now)
                ON CONFLICT(owner_hash, cache_key) DO UPDATE SET json = $json, updated_at = $now
                """;
            command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
            command.Parameters.AddWithValue("$key", cacheKey);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, JsonOptions));
            command.Parameters.AddWithValue("$now", now.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SpotifyKnownMusicOverride> GetKnownMusicOverrides(string ownerKey)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, normalized_key, display_name, is_known FROM spotify_known_music_override WHERE owner_hash = $owner";
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        using var reader = command.ExecuteReader();
        var results = new List<SpotifyKnownMusicOverride>();
        while (reader.Read())
            results.Add(new SpotifyKnownMusicOverride(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
        return results;
    }

    public void SaveKnownMusicOverride(string ownerKey, SpotifyKnownMusicOverride value, DateTimeOffset now)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO spotify_known_music_override
                    (owner_hash, kind, normalized_key, display_name, is_known, updated_at)
                VALUES ($owner, $kind, $key, $name, $known, $now)
                ON CONFLICT(owner_hash, kind, normalized_key) DO UPDATE SET
                    display_name = $name, is_known = $known, updated_at = $now
                """;
            command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
            command.Parameters.AddWithValue("$kind", value.Kind);
            command.Parameters.AddWithValue("$key", value.Key);
            command.Parameters.AddWithValue("$name", value.DisplayName);
            command.Parameters.AddWithValue("$known", value.IsKnown ? 1 : 0);
            command.Parameters.AddWithValue("$now", now.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    private static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return default; }
    }

    private static string OwnerHash(string ownerKey) =>
        HouseholdIdentity.OwnerHash(ownerKey);

    private static bool TryDate(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out result);

    private static DateTimeOffset? Date(object value) =>
        value is string text && TryDate(text, out var parsed) ? parsed : null;

    private static object DbDate(DateTimeOffset? value) =>
        value.HasValue ? value.Value.ToString("o") : DBNull.Value;
}
