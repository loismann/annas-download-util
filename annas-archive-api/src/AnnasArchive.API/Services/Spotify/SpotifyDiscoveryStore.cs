using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyDiscoveryStore
{
    SpotifyDiscoveryDraft? Get(string ownerKey, string draftId);
    void Save(string ownerKey, SpotifyDiscoveryDraft draft);
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
        try { return JsonSerializer.Deserialize<SpotifyDiscoveryDraft>(json, JsonOptions); }
        catch (JsonException) { return null; }
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

    private static string OwnerHash(string ownerKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)));
}
