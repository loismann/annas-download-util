using AnnasArchive.API.Data;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The run manifest from spec §6. Two things matter here beyond CRUD: a run must be
/// resumable after a partial failure (so per-item state is tracked independently),
/// and one household member must never be able to reach another's run by id.
/// </summary>
public sealed class PhotoPrintOrderStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"photo-print-{Guid.NewGuid():N}.db");
    private readonly PhotoPrintOrderStore _store;

    private const string Paul = "paul";
    private const string SomeoneElse = "other-household-member";

    public PhotoPrintOrderStoreTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _databasePath })
            .Build();
        _store = new PhotoPrintOrderStore(new AppDatabase(config));
    }

    private string NewRunWithItems(string owner = Paul)
    {
        var runId = _store.CreateRun(owner, "96813");
        _store.AddItem(owner, runId, "asset-1", "IMG_001.jpg", "4x6", 2);
        _store.AddItem(owner, runId, "asset-2", "IMG_002.jpg", "5x7", 1);
        return runId;
    }

    // ─── Run lifecycle ───────────────────────────────────────────────────

    [Fact]
    public void CreateRun_StartsAsDraft_AndIsRetrievable()
    {
        var runId = _store.CreateRun(Paul, "96813");

        var run = _store.GetRun(Paul, runId);
        run.Should().NotBeNull();
        run!.Status.Should().Be(PrintRunStatus.Draft);
        run.PickupZip.Should().Be("96813");
        run.OutputDirectory.Should().BeNull();
    }

    [Fact]
    public void RunProgressesThroughToAwaitingReview()
    {
        var runId = NewRunWithItems();

        _store.UpdateRunStatus(Paul, runId, PrintRunStatus.Preparing);
        _store.SetRunOutput(Paul, runId, "/data/print-ready/run-1");
        _store.UpdateRunStatus(Paul, runId, PrintRunStatus.Ready);
        _store.UpdateRunStatus(Paul, runId, PrintRunStatus.Submitting);
        _store.SetRunScreenshot(Paul, runId, "/data/print-ready/run-1/review.png");
        _store.UpdateRunStatus(Paul, runId, PrintRunStatus.AwaitingReview);

        var run = _store.GetRun(Paul, runId)!;
        run.Status.Should().Be(PrintRunStatus.AwaitingReview,
            "the automation stops here — the purchase click is Paul's");
        run.OutputDirectory.Should().Be("/data/print-ready/run-1");
        run.ScreenshotPath.Should().Be("/data/print-ready/run-1/review.png");
    }

    [Fact]
    public void FailedRun_RecordsTheError()
    {
        var runId = NewRunWithItems();

        _store.UpdateRunStatus(Paul, runId, PrintRunStatus.Failed, "CVS checkout selector not found");

        var run = _store.GetRun(Paul, runId)!;
        run.Status.Should().Be(PrintRunStatus.Failed);
        run.LastError.Should().Be("CVS checkout selector not found");
    }

    [Fact]
    public void ListRuns_IsNewestFirst_AndScopedToTheOwner()
    {
        _store.CreateRun(Paul, "96813");
        Thread.Sleep(10);
        var newest = _store.CreateRun(Paul, "96813");
        _store.CreateRun(SomeoneElse, "96813");

        var runs = _store.ListRuns(Paul);

        runs.Should().HaveCount(2);
        runs[0].RunId.Should().Be(newest);
    }

    // ─── Items ───────────────────────────────────────────────────────────

    [Fact]
    public void ItemsRoundTripWithTheirSizeAndQuantity()
    {
        var runId = NewRunWithItems();

        var items = _store.ListItems(Paul, runId);

        items.Should().HaveCount(2);
        items[0].SizeCode.Should().Be("4x6");
        items[0].Quantity.Should().Be(2);
        items[0].Status.Should().Be(PrintItemStatus.Pending);
        items[0].PreparedPath.Should().BeNull();
        items[1].ImmichAssetId.Should().Be("asset-2");
    }

    [Fact]
    public void PartialFailure_LeavesSucceededItemsPrepared()
    {
        // The resumability guarantee: one bad photo must not discard the render
        // work already done for the others.
        var runId = NewRunWithItems();
        var items = _store.ListItems(Paul, runId);

        _store.MarkItemPrepared(Paul, runId, items[0].ItemId, "/out/a.jpg", 300.0, false);
        _store.MarkItemFailed(Paul, runId, items[1].ItemId, "source image is corrupt");

        var after = _store.ListItems(Paul, runId);
        after[0].Status.Should().Be(PrintItemStatus.Prepared);
        after[0].PreparedPath.Should().Be("/out/a.jpg");
        after[0].EffectiveDpi.Should().Be(300.0);
        after[1].Status.Should().Be(PrintItemStatus.Failed);
        after[1].LastError.Should().Be("source image is corrupt");
    }

    [Fact]
    public void RetryingAPreviouslyFailedItem_ClearsItsError()
    {
        var runId = NewRunWithItems();
        var item = _store.ListItems(Paul, runId)[0];

        _store.MarkItemFailed(Paul, runId, item.ItemId, "transient read failure");
        _store.MarkItemPrepared(Paul, runId, item.ItemId, "/out/a.jpg", 280.0, false);

        var after = _store.ListItems(Paul, runId)[0];
        after.Status.Should().Be(PrintItemStatus.Prepared);
        after.LastError.Should().BeNull("a stale error next to a successful render is misleading");
    }

    [Fact]
    public void QualityFloorFlagIsPersisted()
    {
        var runId = NewRunWithItems();
        var item = _store.ListItems(Paul, runId)[0];

        _store.MarkItemPrepared(Paul, runId, item.ItemId, "/out/a.jpg", 96.0, belowQualityFloor: true);

        _store.ListItems(Paul, runId)[0].BelowQualityFloor.Should().BeTrue();
    }

    [Fact]
    public void RemoveItem_DropsItFromTheRun()
    {
        var runId = NewRunWithItems();
        var item = _store.ListItems(Paul, runId)[0];

        _store.RemoveItem(Paul, runId, item.ItemId);

        _store.ListItems(Paul, runId).Should().HaveCount(1);
    }

    [Fact]
    public void TotalPrintCount_SumsQuantities_NotRows()
    {
        // 2 + 1 — this is what the per-run ceiling is enforced against, because
        // quantity is what costs money.
        var runId = NewRunWithItems();

        _store.TotalPrintCount(Paul, runId).Should().Be(3);
    }

    [Fact]
    public void TotalPrintCount_IsZeroForAnEmptyRun() =>
        _store.TotalPrintCount(Paul, _store.CreateRun(Paul, null)).Should().Be(0);

    [Fact]
    public void AddItem_RejectsNonPositiveQuantity()
    {
        var runId = _store.CreateRun(Paul, null);

        var act = () => _store.AddItem(Paul, runId, "asset", "a.jpg", "4x6", 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── Owner isolation ─────────────────────────────────────────────────

    [Fact]
    public void AnotherUser_CannotReadTheRun_EvenKnowingItsId()
    {
        var runId = NewRunWithItems(Paul);

        _store.GetRun(SomeoneElse, runId).Should().BeNull();
    }

    [Fact]
    public void AnotherUser_CannotReachItemsOrMutateTheRun_EvenKnowingItsId()
    {
        var runId = NewRunWithItems(Paul);
        var itemId = _store.ListItems(Paul, runId)[0].ItemId;

        Action reads = () => _store.ListItems(SomeoneElse, runId);
        Action adds = () => _store.AddItem(SomeoneElse, runId, "x", "x.jpg", "4x6", 1);
        Action removes = () => _store.RemoveItem(SomeoneElse, runId, itemId);
        Action prepares = () => _store.MarkItemPrepared(SomeoneElse, runId, itemId, "/out/x.jpg", 300, false);
        Action fails = () => _store.MarkItemFailed(SomeoneElse, runId, itemId, "nope");
        Action counts = () => _store.TotalPrintCount(SomeoneElse, runId);
        Action statuses = () => _store.UpdateRunStatus(SomeoneElse, runId, PrintRunStatus.Cancelled);
        Action outputs = () => _store.SetRunOutput(SomeoneElse, runId, "/tmp");

        foreach (var act in new[] { reads, adds, removes, prepares, fails, counts, statuses, outputs })
            act.Should().Throw<KeyNotFoundException>();

        _store.ListItems(Paul, runId).Should().HaveCount(2, "the run is untouched");
    }

    [Fact]
    public void UnknownRunId_Throws()
    {
        var act = () => _store.UpdateRunStatus(Paul, "does-not-exist", PrintRunStatus.Ready);
        act.Should().Throw<KeyNotFoundException>();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
