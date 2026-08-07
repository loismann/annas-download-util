using AnnasArchive.API.Data;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Helpers;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Moves owner-scoped data onto the current owner id when the id a person is
/// keyed by changes — which happens exactly twice per member: once when the JWT
/// stopped carrying the access code (see <see cref="HouseholdIdentity"/>), and
/// again if an explicit <c>Auth:AccessCodes[].Id</c> is later configured.
///
/// Without this, changing the id is indistinguishable from deleting everything
/// that person owned: no error, no empty-database check fires, their Spotify
/// connection and plans and requests and AI spend simply stop existing.
///
/// Runs in <see cref="StartAsync"/>, not a background loop, so it completes
/// before the first request can read a now-stale key. Every statement is an
/// UPDATE narrowed to the old hash, so a second run touches zero rows — no
/// completion marker needed, and no risk of a marker claiming the work was done
/// against a database that has since been restored from an older backup.
/// </summary>
public sealed class HouseholdIdentityMigration(
    AppDatabase database,
    IConfiguration configuration) : IHostedService
{
    /// <summary>Every table keyed by <see cref="HouseholdIdentity.OwnerHash"/>,
    /// with the column that holds it. <c>audiobook_request_user</c> is the odd
    /// one out only in naming — <c>app_user_id</c> stores the same digest.</summary>
    private static readonly (string Table, string Column)[] OwnerScopedTables =
    [
        ("spotify_inventory_meta", "owner_hash"),
        ("spotify_playlist_cache", "owner_hash"),
        ("spotify_inventory_job", "owner_hash"),
        ("spotify_known_music_override", "owner_hash"),
        ("spotify_signal_cache", "owner_hash"),
        ("spotify_change_plan", "owner_hash"),
        ("spotify_audit_event", "owner_hash"),
        ("spotify_discovery_draft", "owner_hash"),
        ("photo_print_run", "owner_hash"),
        ("audiobook_request_user", "app_user_id")
    ];

    /// <summary>The Spotify connection is the one owner-scoped record that lives
    /// in the app_state key/value table rather than a table of its own; its key
    /// embeds the same digest. Must match SpotifyConnectionStore.StatePrefix.</summary>
    private const string ConnectionStatePrefix = "spotify.connection.v1:";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Migrate();
        }
        catch (Exception ex)
        {
            // A failed migration leaves data under the old key, which reads as
            // "everything is gone" rather than corrupting anything. Never block
            // startup over it — the whole app would be down instead of one panel.
            Log.Error(ex, "[Identity] Owner-key migration failed; owner-scoped data may look empty");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Migrate()
    {
        foreach (var member in HouseholdIdentity.Members(configuration))
        {
            var currentId = HouseholdIdentity.ResolveId(member);

            foreach (var priorKey in HouseholdIdentity.PriorKeys(member))
            {
                var moved = MoveDatabaseRows(priorKey, currentId);
                moved += MoveConnection(priorKey, currentId);
                moved += MoveUsageFile(priorKey, currentId) ? 1 : 0;

                if (moved > 0)
                {
                    Log.Information(
                        "[Identity] Moved {Count} owner-scoped record(s) for {Member} onto {OwnerId}",
                        moved, member.Name, currentId);
                }
            }
        }
    }

    private int MoveDatabaseRows(string priorKey, string currentId)
    {
        var oldHash = HouseholdIdentity.OwnerHash(priorKey);
        var newHash = HouseholdIdentity.OwnerHash(currentId);

        using var conn = database.OpenConnection();
        using var transaction = conn.BeginTransaction();
        var moved = 0;

        foreach (var (table, column) in OwnerScopedTables)
        {
            if (!TableExists(conn, transaction, table))
                continue;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            // Table and column names are compile-time constants above, never input.
            cmd.CommandText =
                $"UPDATE OR IGNORE {table} SET {column} = $new WHERE {column} = $old";
            cmd.Parameters.AddWithValue("$new", newHash);
            cmd.Parameters.AddWithValue("$old", oldHash);
            moved += cmd.ExecuteNonQuery();
        }

        // OR IGNORE above: a row already filed under the new id wins, because it
        // is the newer of the two. Whatever it collided with is stale by
        // definition and is dropped here rather than left to shadow the live row.
        foreach (var (table, column) in OwnerScopedTables)
        {
            if (!TableExists(conn, transaction, table))
                continue;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {table} WHERE {column} = $old";
            cmd.Parameters.AddWithValue("$old", oldHash);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return moved;
    }

    private int MoveConnection(string priorKey, string currentId)
    {
        var oldKey = ConnectionStatePrefix + HouseholdIdentity.OwnerHash(priorKey);
        var newKey = ConnectionStatePrefix + HouseholdIdentity.OwnerHash(currentId);

        var stored = database.GetState(oldKey);
        if (stored is null)
            return 0;

        // An existing connection under the new id is the live one; the old row is
        // only cleaned up. The token blob is DPAPI-protected but not owner-bound,
        // so moving it verbatim keeps the person signed in to Spotify.
        if (database.GetState(newKey) is null)
            database.SetState(newKey, stored);

        database.DeleteState(oldKey);
        return 1;
    }

    /// <summary>
    /// The per-person AI spend file is named after the owner key. Renaming it is
    /// what stops a deploy from silently handing everyone a fresh monthly
    /// allowance — the same failure TokenUsageService's own comment warns about.
    /// </summary>
    private bool MoveUsageFile(string priorKey, string currentId)
    {
        var directory = configuration.GetValue<string>("TokenUsage:StoragePath");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".annas-archive",
                "ai-usage");
        }

        var source = Path.Combine(directory, $"{SafeFileName.ForKey(priorKey)}.json");
        var destination = Path.Combine(directory, $"{SafeFileName.ForKey(currentId)}.json");

        if (!File.Exists(source) || File.Exists(destination))
        {
            // Both present means an earlier run already moved it and the person
            // has spent against the new id since; the old file is the stale one.
            if (File.Exists(source) && File.Exists(destination))
                File.Delete(source);

            return false;
        }

        File.Move(source, destination);
        return true;
    }

    private static bool TableExists(SqliteConnection conn, SqliteTransaction transaction, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() is not null;
    }
}
