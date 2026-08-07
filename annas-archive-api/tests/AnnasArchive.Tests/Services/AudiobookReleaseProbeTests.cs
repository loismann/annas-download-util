using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The gate that stops a book nothing carries from being added by accident. Its
/// value is entirely in when it says "no", so the cases that matter are the ones
/// where it must not: an indexer outage, and a rung that happens to be broad.
/// </summary>
public sealed class AudiobookReleaseProbeTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"audiobook-probe-{Guid.NewGuid():N}.db");
    private readonly List<string> _queries = [];
    private readonly Mock<IListenarrService> _listenarr = new();

    private AudiobookRequestService Service()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Path"] = _databasePath }).Build();
        var database = new AppDatabase(configuration);
        var store = new AudiobookRequestStore(database);
        var reconciler = new AudiobookRequestReconciler(
            Mock.Of<IAudiobookshelfService>(), store, Mock.Of<IMediaMetadataService>(), TimeProvider.System);

        return new AudiobookRequestService(
            _listenarr.Object, store, new AudiobookRequestTokenStore(TimeProvider.System),
            reconciler, configuration, TimeProvider.System);
    }

    private static ListenarrIndexerSearchResult Release(string title) => new(
        Id: "id", Title: title, Artist: null, Source: "Indexer", PublishedDate: null, Format: null,
        Score: 0, Size: 1, Seeders: 1, Leechers: 0, Grabs: 0, Files: 1,
        DownloadType: "Usenet", Quality: null, Language: null, DownloadReference: "ref");

    /// <summary>Records every query and answers with a hit only for the ones named.</summary>
    private void IndexersCarry(params string[] queriesWithResults) =>
        _listenarr
            .Setup(l => l.SearchIndexersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string query, CancellationToken _) =>
            {
                _queries.Add(query);
                IReadOnlyList<ListenarrIndexerSearchResult> hit =
                    queriesWithResults.Contains(query, StringComparer.OrdinalIgnoreCase)
                        ? [Release(query)]
                        : [];
                return Task.FromResult(hit);
            });

    [Fact]
    public async Task ReportsAvailable_AndStopsAsSoonAsSomethingIsFound()
    {
        IndexersCarry("Judas Unchained Peter F Hamilton");

        (await Service().HasAnyReleaseAsync("Judas Unchained", "Peter F. Hamilton", default))
            .Should().BeTrue();

        _queries.Should().ContainSingle("the first rung answered, so nothing wider should be asked");
    }

    /// <summary>The reported case: the whole decorated title returns nothing and a
    /// later rung finds the book. Still available — just not on the first try.</summary>
    [Fact]
    public async Task ReportsAvailable_WhenOnlyANarrowerRungFindsIt()
    {
        IndexersCarry("Dark Apprentice");

        (await Service().HasAnyReleaseAsync(
            "Star Wars: The Jedi Academy: Dark Apprentice", "Kevin Anderson", default))
            .Should().BeTrue();

        _queries.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task ReportsUnavailable_WhenNothingCarriesIt()
    {
        IndexersCarry();

        (await Service().HasAnyReleaseAsync("Carl's Doomsday Scenario", "Matt Dinniman", default))
            .Should().BeFalse();
    }

    /// <summary>
    /// The full release search ends with an author-only query so the picker is never
    /// empty. The probe must not: "Matt Dinniman has a catalog" is true for nearly
    /// every author and would wave through exactly the books this is meant to stop.
    /// </summary>
    [Fact]
    public async Task NeverAsksForTheAuthorAlone()
    {
        IndexersCarry("Matt Dinniman");

        (await Service().HasAnyReleaseAsync("Carl's Doomsday Scenario", "Matt Dinniman", default))
            .Should().BeFalse();

        _queries.Should().NotContain("Matt Dinniman");
    }

    /// <summary>An indexer outage is not evidence that a book does not exist, and
    /// blocking a legitimate request on one would be the worse failure.</summary>
    [Fact]
    public async Task FailsOpen_WhenTheIndexersAreUnreachable()
    {
        _listenarr
            .Setup(l => l.SearchIndexersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("prowlarr is down"));

        (await Service().HasAnyReleaseAsync("Judas Unchained", "Peter F. Hamilton", default))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ReportsUnavailable_ForAnEditionWithNoUsableTitle()
    {
        IndexersCarry();

        (await Service().HasAnyReleaseAsync("   ", "Peter F. Hamilton", default)).Should().BeFalse();

        _queries.Should().BeEmpty("there is nothing to ask the indexers");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}
