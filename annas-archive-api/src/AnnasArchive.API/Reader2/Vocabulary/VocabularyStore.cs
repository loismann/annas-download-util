using AnnasArchive.API.Data;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Storage;
using Microsoft.Data.Sqlite;

namespace AnnasArchive.API.Reader2.Vocabulary;

/// <summary>Where a term stands for one reader.</summary>
/// <remarks>
/// Serialised by name. Saving already worked — the request carries a string and
/// is parsed by hand — but the list came back numbered, so the panel's own
/// <c>state === 'Known'</c> filter emptied both lists. See the note on
/// <see cref="Story.ActorTier"/>.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum TermState
{
    /// <summary>Excluded from generated definitions. The reader is done with it.</summary>
    Known,

    /// <summary>Still shown, still defined. The reader is working on it.</summary>
    Studying
}

/// <summary>One term a reader has filed.</summary>
/// <param name="Term">As the reader saw it, diacritics and capitals intact.</param>
/// <param name="TermNorm">What it is keyed by. See <see cref="Vocabulary.TermNorm"/>.</param>
public sealed record VocabularyTerm(
    string Term,
    string TermNorm,
    TermState State,
    string? Definition,
    string? FirstSeenBookId,
    DateTime UpdatedAtUtc);

/// <summary>
/// A reader's known and studying words, across every book.
///
/// <para><b>Per user and not per book, with no foreign key to
/// <c>r2_book</c>.</b> A term the reader has learned must survive un-enrolling
/// the book they first met it in — a word does not become unknown again because
/// you finished the novel. That is also why this is its own table rather than an
/// artifact: artifacts describe a book and are shared across the household,
/// while this describes a person and is theirs.</para>
/// </summary>
public sealed class VocabularyStore(AppDatabase db)
{
    public async Task<IReadOnlyList<VocabularyTerm>> ListAsync(
        string userId, TermState? state = null, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT term_display, term_norm, state, definition, first_seen_book_id, updated_at
            FROM r2_vocabulary
            WHERE user_id = $user AND ($state IS NULL OR state = $state)
            ORDER BY term_norm
            """;
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$state", state is null ? DBNull.Value : Wire(state.Value));

        var terms = new List<VocabularyTerm>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) terms.Add(Read(reader));

        return terms;
    }

    /// <summary>
    /// Files a term, or moves one that is already filed.
    ///
    /// <para>Moving known↔studying is the same operation as adding: the reader
    /// clicking "I'm still working on this" and the reader meeting the word for
    /// the first time both end with one row in the state they chose.</para>
    /// </summary>
    public async Task SaveAsync(
        string userId, string term, TermState state, string? definition = null,
        BookRef? firstSeenIn = null, CancellationToken ct = default)
    {
        var norm = TermNorm.Of(term);
        if (norm.Length == 0) throw new ArgumentException("A term is required.", nameof(term));

        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO r2_vocabulary
                (user_id, term_norm, term_display, state, definition, first_seen_book_id, updated_at)
            VALUES ($user, $norm, $display, $state, $definition, $book, $now)
            ON CONFLICT(user_id, term_norm) DO UPDATE SET
                term_display = excluded.term_display,
                state        = excluded.state,
                -- Keep an existing definition when the caller has none: moving a
                -- term to "known" must not erase what it means.
                definition   = COALESCE(excluded.definition, r2_vocabulary.definition),
                updated_at   = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$norm", norm);
        cmd.Parameters.AddWithValue("$display", term.Trim());
        cmd.Parameters.AddWithValue("$state", Wire(state));
        cmd.Parameters.AddWithValue("$definition", (object?)definition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$book", (object?)firstSeenIn?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", StorageConventions.NowIso());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RemoveAsync(string userId, string term, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM r2_vocabulary WHERE user_id = $user AND term_norm = $norm";
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$norm", TermNorm.Of(term));

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Clears every term, or only those in one state. Returns the count.</summary>
    public async Task<int> ClearAsync(string userId, TermState? state = null, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM r2_vocabulary
            WHERE user_id = $user AND ($state IS NULL OR state = $state)
            """;
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$state", state is null ? DBNull.Value : Wire(state.Value));

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Forgets which book a term was met in, without forgetting the term.
    ///
    /// <para>What <c>DELETE /books/{id}/vocabulary</c> does. Un-enrolling a book
    /// should take the book's provenance with it and leave the reader's
    /// vocabulary alone, because the two are not the same thing.</para>
    /// </summary>
    public async Task<int> ForgetBookAsync(string userId, BookRef book, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE r2_vocabulary SET first_seen_book_id = NULL
            WHERE user_id = $user AND first_seen_book_id = $book
            """;
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$book", book.Value);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The normalised terms a reader already knows.
    ///
    /// <para>The exclusion list. This is what makes definitions personal rather
    /// than exhaustive — a product rule, not a tuning knob. Studying terms are
    /// deliberately absent: the reader is still working on those and wants to
    /// keep seeing them.</para>
    /// </summary>
    public async Task<IReadOnlySet<string>> KnownAsync(string userId, CancellationToken ct = default) =>
        (await ListAsync(userId, TermState.Known, ct))
        .Select(t => t.TermNorm)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every filed term, for hiding what the reader has already dealt with.</summary>
    public async Task<IReadOnlySet<string>> FiledAsync(string userId, CancellationToken ct = default) =>
        (await ListAsync(userId, null, ct))
        .Select(t => t.TermNorm)
        .ToHashSet(StringComparer.Ordinal);

    private static string Wire(TermState state) => state == TermState.Known ? "known" : "studying";

    private static VocabularyTerm Read(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2) == "known" ? TermState.Known : TermState.Studying,
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        StorageConventions.ParseUtc(r.GetString(5)));
}
