using AnnasArchive.API.Constants;
using AnnasArchive.API.Data;
using AnnasArchive.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Ownership assignment, which used to be three independent implementations with
/// one shared habit: <c>if (owner is not null)</c> and no else. Every case here is
/// one of the ways an item reached a library owned by nobody — measured on the live
/// library 2026-08-06, where 16 of 879 movies and 73 orphaned records were the
/// visible result.
/// </summary>
public sealed class MediaOwnershipTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"ownership-{Guid.NewGuid():N}.db");
    private readonly MediaMetadataService _metadata;

    public MediaOwnershipTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _databasePath })
            .Build();
        _metadata = new MediaMetadataService(new AppDatabase(config));
    }

    // ─── Resolution: one rule, previously written twice ──────────────────

    [Theory]
    [InlineData("Paul", "Paul")]
    [InlineData("paul", "Paul")]
    [InlineData("Paul (Admin)", "Paul")]          // the audiobook requester label
    [InlineData("  Mom  ", "Mom")]
    [InlineData("Dad's iPad", "Dad")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Guest", null)]
    public void ResolveName_MapsAnythingACallerHoldsOntoOneMember(string? raw, string? expected) =>
        HouseholdOwners.ResolveName(raw).Should().Be(expected);

    // ─── Assignment: never silent ────────────────────────────────────────

    [Fact]
    public void Assign_TagsTheItem()
    {
        MediaOwnership.Assign(_metadata, "movie", "42", "Mom", "test").Should().BeTrue();

        _metadata.Get("movie", "42")!.Owners.Should().Equal("Mom");
    }

    [Fact]
    public void Assign_ReportsFailureRatherThanSilentlySkipping()
    {
        MediaOwnership.Assign(_metadata, "movie", "42", "Guest", "test").Should().BeFalse();

        _metadata.Get("movie", "42").Should().BeNull();
    }

    [Fact]
    public void Assign_IsAdditive_SoASecondRequesterDoesNotEvictTheFirst()
    {
        MediaOwnership.Assign(_metadata, "audiobook", "abc", "Paul (Admin)", "test");
        MediaOwnership.Assign(_metadata, "audiobook", "abc", "Mom", "test");

        _metadata.Get("audiobook", "abc")!.Owners.Should().BeEquivalentTo("Paul", "Mom");
    }

    // ─── Backfill: adopt and prune ───────────────────────────────────────

    private OwnershipBackfillService Backfill(string? defaultMember = "Paul") =>
        new(new EmptyServiceProvider(), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ownership:DefaultMember"] = defaultMember })
            .Build());

    [Fact]
    public void Backfill_AdoptsLiveItemsNobodyOwns()
    {
        _metadata.AddOwner("movie", "1", "Mom");

        var (adopted, pruned) = Backfill().Reconcile(_metadata, "movie", new HashSet<string> { "1", "2", "3" });

        adopted.Should().Be(2);
        pruned.Should().Be(0);
        _metadata.Get("movie", "1")!.Owners.Should().Equal("Mom");   // untouched
        _metadata.Get("movie", "2")!.Owners.Should().Equal("Paul");
    }

    /// <summary>The 73 orphans: TV and movie deletes never dropped their record.</summary>
    [Fact]
    public void Backfill_PrunesRecordsWhoseItemIsGone()
    {
        _metadata.AddOwner("movie", "1", "Paul");
        _metadata.AddOwner("movie", "999", "Paul");

        var (_, pruned) = Backfill().Reconcile(_metadata, "movie", new HashSet<string> { "1" });

        pruned.Should().Be(1);
        _metadata.Get("movie", "999").Should().BeNull();
    }

    /// <summary>Pruning is keyed by media type, so an audiobook id that happens to
    /// look like a movie id is never collateral damage.</summary>
    [Fact]
    public void Backfill_OnlyTouchesItsOwnMediaType()
    {
        _metadata.AddOwner("movie", "7", "Paul");
        _metadata.AddOwner("tv", "7", "Paul");

        Backfill().Reconcile(_metadata, "movie", new HashSet<string>());

        _metadata.Get("movie", "7").Should().BeNull();
        _metadata.Get("tv", "7")!.Owners.Should().Equal("Paul");
    }

    [Fact]
    public void Backfill_StillPrunesWhenNoDefaultMemberIsConfigured()
    {
        _metadata.AddOwner("movie", "999", "Paul");

        var (adopted, pruned) = Backfill(defaultMember: "nobody")
            .Reconcile(_metadata, "movie", new HashSet<string> { "1" });

        adopted.Should().Be(0);
        pruned.Should().Be(1);
        _metadata.Get("movie", "1").Should().BeNull();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    public void Dispose()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}
