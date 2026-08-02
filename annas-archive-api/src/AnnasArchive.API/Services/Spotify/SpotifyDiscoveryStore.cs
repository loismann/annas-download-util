using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyDiscoveryStore
{
    SpotifyDiscoveryDraft? Get(string ownerKey, string draftId);
    IReadOnlyList<SpotifyDiscoveryDraft> List(string ownerKey);
    void Save(string ownerKey, SpotifyDiscoveryDraft draft);

    /// <summary>Returns false when the draft did not belong to this owner.</summary>
    bool Delete(string ownerKey, string draftId);
}

public sealed class SpotifyDiscoveryStore(AppDatabase database) : ISpotifyDiscoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _writeLock = new();

    public SpotifyDiscoveryDraft? Get(string ownerKey, string draftId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM spotify_discovery_draft WHERE owner_hash = $owner AND draft_id = $id";
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        command.Parameters.AddWithValue("$id", draftId);
        var json = command.ExecuteScalar() as string;
        if (json == null) return null;
        return Deserialize(json);
    }

    public IReadOnlyList<SpotifyDiscoveryDraft> List(string ownerKey)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM spotify_discovery_draft WHERE owner_hash = $owner ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        using var reader = command.ExecuteReader();
        var drafts = new List<SpotifyDiscoveryDraft>();
        while (reader.Read())
        {
            var draft = Deserialize(reader.GetString(0));
            if (draft != null) drafts.Add(draft);
        }
        return drafts;
    }

    public void Save(string ownerKey, SpotifyDiscoveryDraft draft)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO spotify_discovery_draft (owner_hash, draft_id, json, created_at, updated_at)
                VALUES ($owner, $id, $json, $created, $updated)
                ON CONFLICT(owner_hash, draft_id) DO UPDATE SET
                    json = $json, updated_at = $updated
                """;
            command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
            command.Parameters.AddWithValue("$id", draft.Id);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(draft, JsonOptions));
            command.Parameters.AddWithValue("$created", draft.CreatedAt.ToString("o"));
            command.Parameters.AddWithValue("$updated", draft.UpdatedAt.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Owner-scoped, like every other read here: a draft ID alone must not be
    /// enough to throw away someone else's work. A draft holds no Spotify state,
    /// so deleting it really is a delete — nothing on Spotify is touched.
    /// </summary>
    public bool Delete(string ownerKey, string draftId)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM spotify_discovery_draft WHERE owner_hash = $owner AND draft_id = $id";
            command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
            command.Parameters.AddWithValue("$id", draftId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    private static string OwnerHash(string ownerKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)));

    private static SpotifyDiscoveryDraft? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<SpotifyDiscoveryDraft>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
