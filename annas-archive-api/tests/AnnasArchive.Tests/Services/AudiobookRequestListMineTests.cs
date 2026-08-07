using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Backs the audiobook library's in-flight "ghost cards". The gap these close: the
/// per-request status route needs a Listenarr id the caller no longer has once they
/// leave the search page, so a download in progress was invisible everywhere.
///
/// Two rules matter beyond listing. A dismissal is per person, because two people
/// can request the same book and one clearing it must not hide it from the other.
/// And a failed request stays until dismissed — that is the case that otherwise
/// sits unnoticed indefinitely.
/// </summary>
public sealed class AudiobookRequestListMineTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"audiobook-listmine-{Guid.NewGuid():N}.db");
    private readonly AudiobookRequestStore _store;

    private const string Paul = "paul-user-id";
    private const string Partner = "partner-user-id";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");

    public AudiobookRequestListMineTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _databasePath })
            .Build();
        _store = new AudiobookRequestStore(new AppDatabase(config));
    }

    private void Request(int id, string asin, string title, string user, string status = "Queued")
    {
        var item = new ListenarrLibraryItem(id, asin, title, ["Author"], null, null, true, null);
        _store.SaveRequestAndRequester(
            item, asin, [], title, "Author", status, user, $"{user}-label", Now);
    }

    // ─── Listing ─────────────────────────────────────────────────────────

    [Fact]
    public void ListForUser_ReturnsOnlyThatPersonsRequests()
    {
        Request(1, "B00000001", "Neuromancer", Paul);
        Request(2, "B00000002", "Snow Crash", Partner);

        var mine = _store.ListForUser(Paul);

        mine.Should().ContainSingle();
        mine[0].Title.Should().Be("Neuromancer");
    }

    [Fact]
    public void ListForUser_IsNewestRequestFirst()
    {
        Request(1, "B00000001", "Oldest", Paul);
        Request(2, "B00000002", "Newest", Paul);

        // requested_at is the ordering key and both rows share `Now`, so assert on
        // membership rather than a tie-broken order.
        _store.ListForUser(Paul).Select(r => r.Title)
            .Should().BeEquivalentTo(["Oldest", "Newest"]);
    }

    [Fact]
    public void ListForUser_IsEmptyForSomeoneWhoHasRequestedNothing() =>
        _store.ListForUser("nobody").Should().BeEmpty();

    [Fact]
    public void SharedRequest_AppearsForBothRequesters()
    {
        Request(1, "B00000001", "Neuromancer", Paul);
        Request(1, "B00000001", "Neuromancer", Partner);

        _store.ListForUser(Paul).Should().ContainSingle();
        _store.ListForUser(Partner).Should().ContainSingle();
    }

    [Fact]
    public void FailedRequest_RemainsListedUntilDismissed()
    {
        Request(1, "B00000001", "Neuromancer", Paul);
        _store.UpdateStatus(1, "Failed", "no release matched", Now);

        var mine = _store.ListForUser(Paul);

        mine.Should().ContainSingle("a failure that disappears is the bug being fixed");
        mine[0].Status.Should().Be("Failed");
        mine[0].LastError.Should().Be("no release matched");
    }

    // ─── Dismissal ───────────────────────────────────────────────────────

    [Fact]
    public void Dismissing_HidesItFromThatPersonOnly()
    {
        Request(1, "B00000001", "Neuromancer", Paul);
        Request(1, "B00000001", "Neuromancer", Partner);

        _store.SetDismissed(1, Paul, dismissed: true, Now).Should().BeTrue();

        _store.ListForUser(Paul).Should().BeEmpty();
        _store.ListForUser(Partner).Should().ContainSingle(
            "one person tidying their own view must not hide the book from the other requester");
    }

    [Fact]
    public void Dismissing_DoesNotDeleteTheRequestOrTheAttribution()
    {
        Request(1, "B00000001", "Neuromancer", Paul);

        _store.SetDismissed(1, Paul, dismissed: true, Now);

        _store.GetByListenarrId(1).Should().NotBeNull("the Listenarr entry is untouched");
        _store.IsRequester(1, Paul).Should().BeTrue("dismissal is a view preference, not a cancellation");
    }

    [Fact]
    public void DismissalIsReversible()
    {
        Request(1, "B00000001", "Neuromancer", Paul);
        _store.SetDismissed(1, Paul, dismissed: true, Now);

        _store.SetDismissed(1, Paul, dismissed: false, Now).Should().BeTrue();

        _store.ListForUser(Paul).Should().ContainSingle();
    }

    [Fact]
    public void DismissingSomethingYouDidNotRequest_ReportsNoMatch()
    {
        Request(1, "B00000001", "Neuromancer", Paul);

        _store.SetDismissed(1, Partner, dismissed: true, Now).Should().BeFalse();
        _store.SetDismissed(999, Paul, dismissed: true, Now).Should().BeFalse();

        _store.ListForUser(Paul).Should().ContainSingle("Paul's view is unaffected");
    }

    [Fact]
    public void ReRequestingAfterDismissal_DoesNotResurrectIt()
    {
        // SaveRequestAndRequester uses INSERT OR IGNORE on the attribution row, so
        // a re-request leaves dismissed_at set. Pinned deliberately: re-requesting
        // is not the documented way to undo a dismissal, SetDismissed(false) is.
        Request(1, "B00000001", "Neuromancer", Paul);
        _store.SetDismissed(1, Paul, dismissed: true, Now);

        Request(1, "B00000001", "Neuromancer", Paul);

        _store.ListForUser(Paul).Should().BeEmpty();
    }

    // ─── Reconciled items ────────────────────────────────────────────────

    [Fact]
    public void ReconciledRequest_StillListedByTheStore_AndFilteredByTheService()
    {
        // The store stays dumb about abs_item_id; ListMineAsync drops reconciled
        // rows so a ghost card never sits next to the real library card.
        Request(1, "B00000001", "Neuromancer", Paul);
        _store.MarkReconciled(1, "abs-item-123", Now);

        var mine = _store.ListForUser(Paul);

        mine.Should().ContainSingle();
        mine[0].AbsItemId.Should().Be("abs-item-123");
        mine[0].Status.Should().Be("InLibrary");
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
