using System.Text.Json;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Data;

/// <summary>
/// Durable app-owned bridge between a Listenarr audiobook and the household
/// members who requested it. It intentionally stores no release URLs, API keys,
/// downloader credentials, or copy of Listenarr's queue.
/// </summary>
public sealed class AudiobookRequestStore(AppDatabase database)
{
    public AudiobookRequestRecord? GetByAsin(string asin)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT listenarr_id, asin, title_snapshot, author_snapshot,
                   last_observed_status, abs_item_id, last_error, created_at, updated_at
              FROM audiobook_request
             WHERE asin = $asin
            """;
        command.Parameters.AddWithValue("$asin", NormalizeAsin(asin));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRequest(reader) : null;
    }

    public AudiobookRequestRecord? GetByListenarrId(int listenarrId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT listenarr_id, asin, title_snapshot, author_snapshot,
                   last_observed_status, abs_item_id, last_error, created_at, updated_at
              FROM audiobook_request
             WHERE listenarr_id = $listenarrId
            """;
        command.Parameters.AddWithValue("$listenarrId", listenarrId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRequest(reader) : null;
    }

    public bool SaveRequestAndRequester(
        ListenarrLibraryItem item,
        string asin,
        IReadOnlyList<string> isbn,
        string title,
        string author,
        string status,
        string appUserId,
        string ownerLabel,
        DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var request = connection.CreateCommand())
        {
            request.Transaction = transaction;
            request.CommandText = """
                INSERT INTO audiobook_request (
                    listenarr_id, asin, isbn_json, title_snapshot, author_snapshot,
                    last_observed_status, created_at, updated_at)
                VALUES ($id, $asin, $isbn, $title, $author, $status, $now, $now)
                ON CONFLICT(asin) DO UPDATE SET
                    listenarr_id = excluded.listenarr_id,
                    isbn_json = excluded.isbn_json,
                    title_snapshot = excluded.title_snapshot,
                    author_snapshot = excluded.author_snapshot,
                    last_observed_status = excluded.last_observed_status,
                    updated_at = excluded.updated_at
                """;
            request.Parameters.AddWithValue("$id", item.Id);
            request.Parameters.AddWithValue("$asin", NormalizeAsin(asin));
            request.Parameters.AddWithValue("$isbn", JsonSerializer.Serialize(isbn));
            request.Parameters.AddWithValue("$title", title);
            request.Parameters.AddWithValue("$author", author);
            request.Parameters.AddWithValue("$status", status);
            request.Parameters.AddWithValue("$now", now.ToString("O"));
            request.ExecuteNonQuery();
        }

        int added;
        using (var requester = connection.CreateCommand())
        {
            requester.Transaction = transaction;
            requester.CommandText = """
                INSERT OR IGNORE INTO audiobook_request_user (
                    listenarr_id, app_user_id, owner_label, requested_at)
                VALUES ($id, $user, $label, $now)
                """;
            requester.Parameters.AddWithValue("$id", item.Id);
            requester.Parameters.AddWithValue("$user", appUserId);
            requester.Parameters.AddWithValue("$label", ownerLabel);
            requester.Parameters.AddWithValue("$now", now.ToString("O"));
            added = requester.ExecuteNonQuery();
        }

        transaction.Commit();
        return added == 1;
    }

    public void UpdateStatus(int listenarrId, string status, string? error, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE audiobook_request
               SET last_observed_status = $status,
                   last_error = $error,
                   updated_at = $now
             WHERE listenarr_id = $id
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", listenarrId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Every request this person asked for and has not dismissed, newest first.
    /// This is what lets the audiobook library show in-flight titles at all — the
    /// per-request status route needs an id the caller no longer has once they
    /// leave the search page.
    /// </summary>
    public IReadOnlyList<AudiobookRequestRecord> ListForUser(string appUserId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.listenarr_id, r.asin, r.title_snapshot, r.author_snapshot,
                   r.last_observed_status, r.abs_item_id, r.last_error,
                   r.created_at, r.updated_at
              FROM audiobook_request r
              JOIN audiobook_request_user u ON u.listenarr_id = r.listenarr_id
             WHERE u.app_user_id = $user AND u.dismissed_at IS NULL
             ORDER BY u.requested_at DESC
            """;
        command.Parameters.AddWithValue("$user", appUserId);
        using var reader = command.ExecuteReader();

        var records = new List<AudiobookRequestRecord>();
        while (reader.Read()) records.Add(ReadRequest(reader));
        return records;
    }

    /// <summary>
    /// Hides one request from one person's library view. Deliberately not a
    /// delete: a dismissed failure stays in Listenarr and stays visible to the
    /// other requesters, and dismissing is reversible by re-requesting.
    /// </summary>
    public bool SetDismissed(int listenarrId, string appUserId, bool dismissed, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE audiobook_request_user
               SET dismissed_at = $at
             WHERE listenarr_id = $id AND app_user_id = $user
            """;
        command.Parameters.AddWithValue("$at", dismissed ? now.ToString("O") : (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", listenarrId);
        command.Parameters.AddWithValue("$user", appUserId);
        return command.ExecuteNonQuery() == 1;
    }

    public bool IsRequester(int listenarrId, string appUserId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM audiobook_request_user
                 WHERE listenarr_id = $id AND app_user_id = $user)
            """;
        command.Parameters.AddWithValue("$id", listenarrId);
        command.Parameters.AddWithValue("$user", appUserId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public IReadOnlyList<string> GetOwnerLabels(int listenarrId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_label FROM audiobook_request_user
             WHERE listenarr_id = $id
             ORDER BY requested_at
            """;
        command.Parameters.AddWithValue("$id", listenarrId);
        using var reader = command.ExecuteReader();
        var labels = new List<string>();
        while (reader.Read()) labels.Add(reader.GetString(0));
        return labels;
    }

    /// <summary>Drops one person's claim on a request. Returns how many
    /// requesters remain, so the caller can decide whether the Listenarr entry
    /// itself should go — one person changing their mind must not cancel the
    /// book for everyone else who asked for it.</summary>
    public int RemoveRequester(int listenarrId, string appUserId)
    {
        using var connection = database.OpenConnection();
        using (var remove = connection.CreateCommand())
        {
            remove.CommandText = """
                DELETE FROM audiobook_request_user
                 WHERE listenarr_id = $id AND app_user_id = $user
                """;
            remove.Parameters.AddWithValue("$id", listenarrId);
            remove.Parameters.AddWithValue("$user", appUserId);
            remove.ExecuteNonQuery();
        }

        using var count = connection.CreateCommand();
        count.CommandText =
            "SELECT COUNT(*) FROM audiobook_request_user WHERE listenarr_id = $id";
        count.Parameters.AddWithValue("$id", listenarrId);
        return Convert.ToInt32(count.ExecuteScalar());
    }

    /// <summary>Deletes the request row and every remaining attribution.</summary>
    public void DeleteRequest(int listenarrId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var table in new[] { "audiobook_request_user", "audiobook_request" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE listenarr_id = $id";
            command.Parameters.AddWithValue("$id", listenarrId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void MarkReconciled(int listenarrId, string absItemId, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE audiobook_request
               SET abs_item_id = $absItemId,
                   last_observed_status = 'InLibrary',
                   last_error = NULL,
                   updated_at = $now
             WHERE listenarr_id = $id
            """;
        command.Parameters.AddWithValue("$absItemId", absItemId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", listenarrId);
        command.ExecuteNonQuery();
    }

    private static AudiobookRequestRecord ReadRequest(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        DateTimeOffset.Parse(reader.GetString(7)),
        DateTimeOffset.Parse(reader.GetString(8)));

    private static string NormalizeAsin(string asin) => asin.Trim().ToUpperInvariant();
}
