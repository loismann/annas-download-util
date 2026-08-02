using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyPlanStore
{
    SpotifyChangePlan? Get(string ownerKey, Guid planId);
    IReadOnlyList<SpotifyChangePlan> List(string ownerKey, int limit = 50);
    void Save(string ownerKey, SpotifyChangePlan plan);
}

/// <summary>
/// Plans live in SQLite keyed by owner, following the same shape as the discovery
/// and connection stores.
///
/// Owner-scoping every read is the access boundary, not a convenience: a plan ID is
/// a guessable-shaped GUID, and without the owner in the WHERE clause knowing one
/// would be enough to confirm or cancel somebody else's pending change.
/// </summary>
public sealed class SpotifyPlanStore(AppDatabase database) : ISpotifyPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _writeLock = new();

    public SpotifyChangePlan? Get(string ownerKey, Guid planId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT json FROM spotify_change_plan WHERE owner_hash = $owner AND plan_id = $id";
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        command.Parameters.AddWithValue("$id", planId.ToString());

        return command.ExecuteScalar() is string json ? Deserialize(json) : null;
    }

    public IReadOnlyList<SpotifyChangePlan> List(string ownerKey, int limit = 50)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json FROM spotify_change_plan
            WHERE owner_hash = $owner
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        using var reader = command.ExecuteReader();
        var plans = new List<SpotifyChangePlan>();
        while (reader.Read())
        {
            if (Deserialize(reader.GetString(0)) is { } plan)
                plans.Add(plan);
        }

        return plans;
    }

    public void Save(string ownerKey, SpotifyChangePlan plan)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO spotify_change_plan (owner_hash, plan_id, status, json, created_at, updated_at)
                VALUES ($owner, $id, $status, $json, $created, $updated)
                ON CONFLICT(owner_hash, plan_id) DO UPDATE SET
                    status = $status, json = $json, updated_at = $updated
                """;
            command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
            command.Parameters.AddWithValue("$id", plan.Id.ToString());
            command.Parameters.AddWithValue("$status", plan.Status.ToString());
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan, JsonOptions));
            command.Parameters.AddWithValue("$created", plan.CreatedAtUtc.ToString("o"));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    private static string OwnerHash(string ownerKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)));

    private static SpotifyChangePlan? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<SpotifyChangePlan>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}

public interface ISpotifyAuditService
{
    void Record(string ownerKey, SpotifyAuditEvent auditEvent);
    IReadOnlyList<SpotifyAuditEvent> List(string ownerKey, Guid? planId = null, int limit = 100);
}

/// <summary>
/// Append-only history. There is deliberately no update or delete: the point of an
/// audit trail is that it still says what happened after someone wishes it did not.
/// Purging cached Spotify data must not take these with it.
/// </summary>
public sealed class SpotifyAuditService(AppDatabase database) : ISpotifyAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _writeLock = new();

    public void Record(string ownerKey, SpotifyAuditEvent auditEvent)
    {
        lock (_writeLock)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO spotify_audit_event (owner_hash, event_id, plan_id, kind, at_utc, json)
                VALUES ($owner, $id, $plan, $kind, $at, $json)
                """;
            command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
            command.Parameters.AddWithValue("$id", auditEvent.Id.ToString());
            command.Parameters.AddWithValue("$plan", auditEvent.PlanId.ToString());
            command.Parameters.AddWithValue("$kind", auditEvent.Kind.ToString());
            command.Parameters.AddWithValue("$at", auditEvent.AtUtc.ToString("o"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(auditEvent, JsonOptions));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SpotifyAuditEvent> List(string ownerKey, Guid? planId = null, int limit = 100)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = planId is null
            ? "SELECT json FROM spotify_audit_event WHERE owner_hash = $owner ORDER BY at_utc DESC LIMIT $limit"
            : "SELECT json FROM spotify_audit_event WHERE owner_hash = $owner AND plan_id = $plan ORDER BY at_utc DESC LIMIT $limit";

        command.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        if (planId is not null)
            command.Parameters.AddWithValue("$plan", planId.Value.ToString());

        using var reader = command.ExecuteReader();
        var events = new List<SpotifyAuditEvent>();
        while (reader.Read())
        {
            try
            {
                if (JsonSerializer.Deserialize<SpotifyAuditEvent>(reader.GetString(0), JsonOptions) is { } e)
                    events.Add(e);
            }
            catch (JsonException) { /* one unreadable row must not hide the rest of the history */ }
        }

        return events;
    }

    private static string OwnerHash(string ownerKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)));
}
