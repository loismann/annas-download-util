using AnnasArchive.API.Services;
using FluentAssertions;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// LibraryWatcher:AutoTagNewBooks is a fallback owner for books that arrive
/// with no owner at all. It used to be added whenever it was merely absent,
/// so every book Mom or Dad downloaded came out owned by them *and* Paul.
/// </summary>
public sealed class LibraryWatcherOwnerTagTests
{
    private const string Fallback = "Paul's Books";

    [Fact]
    public void FallbackOwner_IsNotAddedWhenTheDownloaderAlreadyOwnsTheBook()
    {
        var tags = LibraryWatcherService.ApplyFallbackOwnerTag(
            ["Mom's Books"], Fallback, enrichmentComplete: false);

        tags.Should().Equal("Mom's Books");
    }

    [Fact]
    public void FallbackOwner_IsAddedOnlyWhenNobodyOwnsTheBook()
    {
        var tags = LibraryWatcherService.ApplyFallbackOwnerTag(
            ["Science Fiction"], Fallback, enrichmentComplete: false);

        tags.Should().Equal("Science Fiction", Fallback);
    }

    [Fact]
    public void FallbackOwner_LeavesNonOwnerTagsAlone()
    {
        var tags = LibraryWatcherService.ApplyFallbackOwnerTag(
            ["Dad's Books", "History"], Fallback, enrichmentComplete: false);

        tags.Should().Equal("Dad's Books", "History");
    }

    [Fact]
    public void FallbackOwner_IsNotReAddedAfterEnrichmentCompleted()
    {
        var tags = LibraryWatcherService.ApplyFallbackOwnerTag(
            [], Fallback, enrichmentComplete: true);

        tags.Should().BeEmpty();
    }

    [Fact]
    public void FallbackOwner_IsSkippedWhenNotConfigured()
    {
        LibraryWatcherService.ApplyFallbackOwnerTag([], null, enrichmentComplete: false)
            .Should().BeEmpty();
    }

    [Fact]
    public void FallbackOwner_IsNotDuplicated()
    {
        var tags = LibraryWatcherService.ApplyFallbackOwnerTag(
            [Fallback], Fallback, enrichmentComplete: false);

        tags.Should().Equal(Fallback);
    }
}
