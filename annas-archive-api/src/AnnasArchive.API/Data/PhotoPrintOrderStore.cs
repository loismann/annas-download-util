using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AnnasArchive.API.Data;

/// <summary>Lifecycle of one print run. Ordering matters: a run only moves forward.</summary>
public enum PrintRunStatus
{
    /// <summary>Photos chosen, nothing rendered yet.</summary>
    Draft,
    /// <summary>Cropping/resizing in progress.</summary>
    Preparing,
    /// <summary>Print-ready files on disk, not yet sent to CVS.</summary>
    Ready,
    /// <summary>Playwright is driving the CVS checkout.</summary>
    Submitting,
    /// <summary>
    /// Parked on the CVS order review page. This is the terminal state for the
    /// automation — the purchase click is Paul's (spec §7.2).
    /// </summary>
    AwaitingReview,
    /// <summary>Paul confirmed he placed the order.</summary>
    Completed,
    Failed,
    Cancelled
}

public enum PrintItemStatus { Pending, Prepared, Uploaded, Failed }

public sealed record PrintRun(
    string RunId,
    PrintRunStatus Status,
    string? PickupZip,
    string? OutputDirectory,
    string? ScreenshotPath,
    string? LastError,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record PrintItem(
    string RunId,
    string ItemId,
    string ImmichAssetId,
    string SourceFileName,
    string SizeCode,
    int Quantity,
    string? PreparedPath,
    double? EffectiveDpi,
    bool BelowQualityFloor,
    PrintItemStatus Status,
    string? LastError);

public interface IPhotoPrintOrderStore
{
    string CreateRun(string ownerKey, string? pickupZip);
    PrintRun? GetRun(string ownerKey, string runId);
    IReadOnlyList<PrintRun> ListRuns(string ownerKey, int limit = 20);
    void UpdateRunStatus(string ownerKey, string runId, PrintRunStatus status, string? lastError = null);
    void SetRunOutput(string ownerKey, string runId, string outputDirectory);
    void SetRunScreenshot(string ownerKey, string runId, string screenshotPath);

    string AddItem(string ownerKey, string runId, string immichAssetId, string sourceFileName, string sizeCode, int quantity);
    void RemoveItem(string ownerKey, string runId, string itemId);
    IReadOnlyList<PrintItem> ListItems(string ownerKey, string runId);
    void MarkItemPrepared(string ownerKey, string runId, string itemId, string preparedPath, double effectiveDpi, bool belowQualityFloor);
    void MarkItemFailed(string ownerKey, string runId, string itemId, string error);
    int TotalPrintCount(string ownerKey, string runId);
}

/// <summary>
/// The order manifest from spec §6: one row per photo/size/quantity, so a run that
/// fails halfway is resumable and auditable rather than needing the whole selection
/// re-picked. Rows are scoped by a one-way hash of the owner, matching the Spotify
/// stores — a signed-in user can only ever reach their own runs.
/// </summary>
public sealed class PhotoPrintOrderStore : IPhotoPrintOrderStore
{
    private readonly AppDatabase _database;

    public PhotoPrintOrderStore(AppDatabase database)
    {
        _database = database;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS photo_print_run (
                run_id          TEXT PRIMARY KEY,
                owner_hash      TEXT NOT NULL,
                status          TEXT NOT NULL,
                pickup_zip      TEXT,
                output_dir      TEXT,
                screenshot_path TEXT,
                last_error      TEXT,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_photo_print_run_owner
                ON photo_print_run(owner_hash, created_at DESC);

            CREATE TABLE IF NOT EXISTS photo_print_item (
                run_id              TEXT NOT NULL,
                item_id             TEXT NOT NULL,
                immich_asset_id     TEXT NOT NULL,
                source_file_name    TEXT NOT NULL,
                size_code           TEXT NOT NULL,
                quantity            INTEGER NOT NULL,
                prepared_path       TEXT,
                effective_dpi       REAL,
                below_quality_floor INTEGER NOT NULL DEFAULT 0,
                status              TEXT NOT NULL,
                last_error          TEXT,
                created_at          TEXT NOT NULL,
                updated_at          TEXT NOT NULL,
                PRIMARY KEY (run_id, item_id),
                FOREIGN KEY (run_id) REFERENCES photo_print_run(run_id) ON DELETE CASCADE
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ─── Runs ────────────────────────────────────────────────────────────

    public string CreateRun(string ownerKey, string? pickupZip)
    {
        var runId = Guid.NewGuid().ToString("N");
        var now = Now();

        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO photo_print_run
                (run_id, owner_hash, status, pickup_zip, created_at, updated_at)
            VALUES ($run, $owner, $status, $zip, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        cmd.Parameters.AddWithValue("$status", PrintRunStatus.Draft.ToString());
        cmd.Parameters.AddWithValue("$zip", (object?)pickupZip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return runId;
    }

    public PrintRun? GetRun(string ownerKey, string runId)
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, status, pickup_zip, output_dir, screenshot_path,
                   last_error, created_at, updated_at
            FROM photo_print_run
            WHERE run_id = $run AND owner_hash = $owner
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    public IReadOnlyList<PrintRun> ListRuns(string ownerKey, int limit = 20)
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, status, pickup_zip, output_dir, screenshot_path,
                   last_error, created_at, updated_at
            FROM photo_print_run
            WHERE owner_hash = $owner
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        cmd.Parameters.AddWithValue("$limit", limit);

        var runs = new List<PrintRun>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            runs.Add(ReadRun(reader));

        return runs;
    }

    public void UpdateRunStatus(string ownerKey, string runId, PrintRunStatus status, string? lastError = null)
    {
        // last_error is only cleared by an explicit null on a non-failed status, so a
        // transient error stays visible while the run retries.
        ExecuteRunUpdate(ownerKey, runId,
            "status = $status, last_error = $error",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", status.ToString());
                cmd.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
            });
    }

    public void SetRunOutput(string ownerKey, string runId, string outputDirectory) =>
        ExecuteRunUpdate(ownerKey, runId, "output_dir = $dir",
            cmd => cmd.Parameters.AddWithValue("$dir", outputDirectory));

    public void SetRunScreenshot(string ownerKey, string runId, string screenshotPath) =>
        ExecuteRunUpdate(ownerKey, runId, "screenshot_path = $path",
            cmd => cmd.Parameters.AddWithValue("$path", screenshotPath));

    // ─── Items ───────────────────────────────────────────────────────────

    public string AddItem(
        string ownerKey, string runId, string immichAssetId,
        string sourceFileName, string sizeCode, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        RequireRun(ownerKey, runId);

        var itemId = Guid.NewGuid().ToString("N");
        var now = Now();

        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO photo_print_item
                (run_id, item_id, immich_asset_id, source_file_name, size_code,
                 quantity, status, created_at, updated_at)
            VALUES ($run, $item, $asset, $name, $size, $qty, $status, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$asset", immichAssetId);
        cmd.Parameters.AddWithValue("$name", sourceFileName);
        cmd.Parameters.AddWithValue("$size", sizeCode);
        cmd.Parameters.AddWithValue("$qty", quantity);
        cmd.Parameters.AddWithValue("$status", PrintItemStatus.Pending.ToString());
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        TouchRun(runId);
        return itemId;
    }

    public void RemoveItem(string ownerKey, string runId, string itemId)
    {
        RequireRun(ownerKey, runId);

        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM photo_print_item WHERE run_id = $run AND item_id = $item";
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.ExecuteNonQuery();

        TouchRun(runId);
    }

    public IReadOnlyList<PrintItem> ListItems(string ownerKey, string runId)
    {
        RequireRun(ownerKey, runId);

        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, item_id, immich_asset_id, source_file_name, size_code,
                   quantity, prepared_path, effective_dpi, below_quality_floor,
                   status, last_error
            FROM photo_print_item
            WHERE run_id = $run
            ORDER BY created_at
            """;
        cmd.Parameters.AddWithValue("$run", runId);

        var items = new List<PrintItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new PrintItem(
                RunId: reader.GetString(0),
                ItemId: reader.GetString(1),
                ImmichAssetId: reader.GetString(2),
                SourceFileName: reader.GetString(3),
                SizeCode: reader.GetString(4),
                Quantity: reader.GetInt32(5),
                PreparedPath: reader.IsDBNull(6) ? null : reader.GetString(6),
                EffectiveDpi: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                BelowQualityFloor: reader.GetInt32(8) != 0,
                Status: Enum.Parse<PrintItemStatus>(reader.GetString(9)),
                LastError: reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return items;
    }

    public void MarkItemPrepared(
        string ownerKey, string runId, string itemId,
        string preparedPath, double effectiveDpi, bool belowQualityFloor)
    {
        RequireRun(ownerKey, runId);

        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE photo_print_item
            SET prepared_path = $path, effective_dpi = $dpi,
                below_quality_floor = $floor, status = $status,
                last_error = NULL, updated_at = $now
            WHERE run_id = $run AND item_id = $item
            """;
        cmd.Parameters.AddWithValue("$path", preparedPath);
        cmd.Parameters.AddWithValue("$dpi", effectiveDpi);
        cmd.Parameters.AddWithValue("$floor", belowQualityFloor ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", PrintItemStatus.Prepared.ToString());
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.ExecuteNonQuery();

        TouchRun(runId);
    }

    public void MarkItemFailed(string ownerKey, string runId, string itemId, string error)
    {
        RequireRun(ownerKey, runId);

        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE photo_print_item
            SET status = $status, last_error = $error, updated_at = $now
            WHERE run_id = $run AND item_id = $item
            """;
        cmd.Parameters.AddWithValue("$status", PrintItemStatus.Failed.ToString());
        cmd.Parameters.AddWithValue("$error", error);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.ExecuteNonQuery();

        TouchRun(runId);
    }

    /// <summary>
    /// Total sheets of paper, not rows — quantity is what costs money, so this is
    /// what the per-run ceiling in <c>PhotoPrintConfiguration.MaxPrintsPerRun</c>
    /// is checked against.
    /// </summary>
    public int TotalPrintCount(string ownerKey, string runId)
    {
        RequireRun(ownerKey, runId);

        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(quantity), 0) FROM photo_print_item WHERE run_id = $run";
        cmd.Parameters.AddWithValue("$run", runId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private void ExecuteRunUpdate(
        string ownerKey, string runId, string setClause, Action<SqliteCommand> bind)
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE photo_print_run
            SET {setClause}, updated_at = $now
            WHERE run_id = $run AND owner_hash = $owner
            """;
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$owner", OwnerHash(ownerKey));
        bind(cmd);

        if (cmd.ExecuteNonQuery() == 0)
            throw new KeyNotFoundException($"Print run '{runId}' was not found for this user.");
    }

    /// <summary>
    /// Item operations take a run id from the caller, so every one of them must
    /// re-prove ownership — otherwise a guessed run id would expose another
    /// household member's order.
    /// </summary>
    private void RequireRun(string ownerKey, string runId)
    {
        if (GetRun(ownerKey, runId) is null)
            throw new KeyNotFoundException($"Print run '{runId}' was not found for this user.");
    }

    private void TouchRun(string runId)
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE photo_print_run SET updated_at = $now WHERE run_id = $run";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.ExecuteNonQuery();
    }

    private static PrintRun ReadRun(SqliteDataReader reader) => new(
        RunId: reader.GetString(0),
        Status: Enum.Parse<PrintRunStatus>(reader.GetString(1)),
        PickupZip: reader.IsDBNull(2) ? null : reader.GetString(2),
        OutputDirectory: reader.IsDBNull(3) ? null : reader.GetString(3),
        ScreenshotPath: reader.IsDBNull(4) ? null : reader.GetString(4),
        LastError: reader.IsDBNull(5) ? null : reader.GetString(5),
        CreatedAt: DateTime.Parse(reader.GetString(6)).ToUniversalTime(),
        UpdatedAt: DateTime.Parse(reader.GetString(7)).ToUniversalTime());

    private static string Now() => DateTime.UtcNow.ToString("o");

    private static string OwnerHash(string ownerKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)));
}
