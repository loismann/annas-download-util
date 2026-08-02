using System.Text.Json.Nodes;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The bulk gate in DOCS/features/LISTENARR_INTEGRATION.md: a series preview
/// must state its exact effect, only checked missing books may be added, a
/// partial failure must neither roll back nor duplicate the successes, and
/// the ceiling plus the administrator rule are enforced on the server.
/// </summary>
public sealed class AudiobookSeriesServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"audiobook-series-{Guid.NewGuid():N}.db");
    private const string SeriesAsin = "B0SERIES01";

    [Fact]
    public async Task Preview_ClassifiesOwnedRequestedAndRequestableMembers_InReadingOrder()
    {
        var owned = JsonNode.Parse("""
            [{
              "id": "abs-one",
              "media": { "metadata": {
                "title": "All Systems Red", "authorName": "Martha Wells",
                "narratorName": "Kevin R. Free", "asin": "B0BOOK0001"
              }}
            }]
            """)!.AsArray();
        var harness = new Harness(
            [
                Member("B0BOOK0003", "Rogue Protocol", "3"),
                Member("B0BOOK0001", "All Systems Red", "1"),
                Member("B0BOOK0002", "Artificial Condition", "2"),
                Member(null, "Untitled Novella", "4")
            ],
            owned,
            [new ListenarrLibraryItem(9, "B0BOOK0002", "Artificial Condition", ["Martha Wells"], null, null, true, null)]);

        var preview = await harness.Service.PreviewAsync("owner-a", SeriesAsin, "us", default);

        preview.Members.Select(member => member.Position).Should().Equal("1", "2", "3", "4");
        preview.Members[0].Classification.Should().Be("owned");
        preview.Members[1].Classification.Should().Be("requested");
        preview.Members[2].Classification.Should().Be("requestable");
        preview.Members[3].Classification.Should().Be("ambiguous",
            "a member without one specific edition cannot be requested in bulk");
        preview.OwnedCount.Should().Be(1);
        preview.RequestedCount.Should().Be(1);
        preview.RequestableCount.Should().Be(1);
        preview.ExceedsCeiling.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_AddsOnlyTheCheckedBooks_AndReportsEachOutcome()
    {
        var harness = new Harness([
            Member("B0BOOK0001", "All Systems Red", "1"),
            Member("B0BOOK0002", "Artificial Condition", "2")
        ]);
        var preview = await harness.Service.PreviewAsync("owner-a", SeriesAsin, "us", default);

        var result = await harness.Service.ConfirmAsync(
            "owner-a", "Paul", isAdmin: false, preview.PreviewToken, ["B0BOOK0001"], false, default);

        result.RequestedCount.Should().Be(1);
        result.Outcomes.Should().ContainSingle().Which.Asin.Should().Be("B0BOOK0001");
        harness.AddedAsins.Should().Equal("B0BOOK0001");
    }

    [Fact]
    public async Task Confirm_SeriesMembers_AreAddedWithoutAutomaticSearch()
    {
        var harness = new Harness([Member("B0BOOK0001", "All Systems Red", "1")]);
        var preview = await harness.Service.PreviewAsync("owner-a", SeriesAsin, "us", default);

        await harness.Service.ConfirmAsync(
            "owner-a", "Paul", isAdmin: false, preview.PreviewToken, ["B0BOOK0001"], false, default);

        harness.AddRequests.Should().ContainSingle()
            .Which.AutoSearch.Should().BeFalse("a bulk confirmation proves no per-edition preference");
    }

    [Fact]
    public async Task Confirm_RejectsAnAsinThePreviewNeverOffered()
    {
        var harness = new Harness([Member("B0BOOK0001", "All Systems Red", "1")]);
        var preview = await harness.Service.PreviewAsync("owner-a", SeriesAsin, "us", default);

        var confirm = () => harness.Service.ConfirmAsync(
            "owner-a", "Paul", isAdmin: false, preview.PreviewToken, ["B0INJECTED"], false, default);

        await confirm.Should().ThrowAsync<AudiobookRequestValidationException>()
            .WithMessage("*preview did not offer*");
        harness.AddedAsins.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_AboveTheCeiling_RequiresAnAdministratorAndASecondConfirmation()
    {
        var members = Enumerable.Range(1, 30)
            .Select(index => Member($"B0BOOK{index:D4}", $"Book {index}", index.ToString()))
            .ToArray();
        var harness = new Harness(members);
        var preview = await harness.Service.PreviewAsync("owner-a", SeriesAsin, "us", default);
        var everything = members.Select(member => member.Asin!).ToArray();

        preview.ExceedsCeiling.Should().BeTrue();
        preview.RequestCeiling.Should().Be(AudiobookSeriesService.RequestCeiling);

        var asOrdinaryUser = () => harness.Service.ConfirmAsync(
            "owner-a", "Paul", isAdmin: false, preview.PreviewToken, everything, true, default);
        await asOrdinaryUser.Should().ThrowAsync<AudiobookRequestValidationException>()
            .WithMessage("*administrator*");

        // The rejected attempt consumed the token, so the admin path needs a
        // fresh preview — the same rule as every other capability here.
        var second = await harness.Service.PreviewAsync("owner-a", SeriesAsin, "us", default);
        var withoutSecondConfirmation = () => harness.Service.ConfirmAsync(
            "owner-a", "Paul", isAdmin: true, second.PreviewToken, everything, false, default);
        await withoutSecondConfirmation.Should().ThrowAsync<AudiobookRequestValidationException>()
            .WithMessage("*second confirmation*");

        var third = await harness.Service.PreviewAsync("owner-a", SeriesAsin, "us", default);
        var result = await harness.Service.ConfirmAsync(
            "owner-a", "Paul", isAdmin: true, third.PreviewToken, everything, true, default);
        result.RequestedCount.Should().Be(30);
    }

    [Fact]
    public async Task Confirm_PartialFailure_KeepsTheSuccessfulRequests()
    {
        var harness = new Harness([
            Member("B0BOOK0001", "All Systems Red", "1"),
            Member("B0BOOK0002", "Artificial Condition", "2")
        ]);
        harness.FailAsin = "B0BOOK0002";
        var preview = await harness.Service.PreviewAsync("owner-a", SeriesAsin, "us", default);

        var result = await harness.Service.ConfirmAsync(
            "owner-a", "Paul", isAdmin: false, preview.PreviewToken,
            ["B0BOOK0001", "B0BOOK0002"], false, default);

        result.RequestedCount.Should().Be(1);
        result.FailedCount.Should().Be(1);
        result.Outcomes.Single(outcome => outcome.Outcome == "failed").Error.Should().NotBeNullOrWhiteSpace();
        harness.AddedAsins.Should().Contain("B0BOOK0001");
    }

    [Fact]
    public async Task Confirm_RejectsAnExpiredOrForeignPreviewToken()
    {
        var harness = new Harness([Member("B0BOOK0001", "All Systems Red", "1")]);
        var preview = await harness.Service.PreviewAsync("owner-a", SeriesAsin, "us", default);

        var asAnotherUser = () => harness.Service.ConfirmAsync(
            "owner-b", "Mom", isAdmin: false, preview.PreviewToken, ["B0BOOK0001"], false, default);

        await asAnotherUser.Should().ThrowAsync<AudiobookRequestValidationException>()
            .WithMessage("*expired or belongs to another user*");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }

    private static ListenarrAudibleSearchResult Member(string? asin, string title, string position) => new(
        asin, title, null,
        [new ListenarrAudibleAuthor(null, "Martha Wells", "us")],
        [new ListenarrAudibleNarrator("Kevin R. Free")],
        [new ListenarrAudibleSeries(SeriesAsin, "The Murderbot Diaries", position)],
        [], null, 200, null, null, "english", "unabridged", null, null, null, "us");

    /// <summary>A fake Listenarr that records every add, so the tests assert
    /// on what would actually have been sent upstream.</summary>
    private sealed class Harness
    {
        private int _nextId = 100;
        public List<string> AddedAsins { get; } = [];
        public List<ListenarrAddToLibraryRequest> AddRequests { get; } = [];
        public string? FailAsin { get; set; }
        public AudiobookSeriesService Service { get; }

        public Harness(
            IReadOnlyList<ListenarrAudibleSearchResult> members,
            JsonArray? owned = null,
            IReadOnlyList<ListenarrLibraryItem>? library = null)
        {
            var listenarr = new Mock<IListenarrService>();
            listenarr.Setup(service => service.GetSeriesBooksAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(members);
            listenarr.Setup(service => service.GetLibraryAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(library ?? []);
            listenarr.Setup(service => service.GetLibraryByAsinAsync(
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ListenarrLibraryItem?)null);
            listenarr.Setup(service => service.GetDefaultQualityProfileAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListenarrQualityProfile(1, "AAC M4B", true));
            listenarr.Setup(service => service.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new ListenarrRootFolder(1, "Audiobooks", "/data/audiobooks", true)]);
            listenarr.Setup(service => service.GetAudibleMetadataAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string asin, string region, CancellationToken _) =>
                {
                    var member = members.FirstOrDefault(item =>
                        string.Equals(item.Asin, asin, StringComparison.OrdinalIgnoreCase));
                    return member is null ? null : new ListenarrAudibleBook(
                        asin, member.Title, null, member.Authors, member.Narrators,
                        null, null, null, null, member.RuntimeLengthMin, member.Language,
                        [], member.Series, false, null, null, region, member.BookFormat);
                });
            listenarr.Setup(service => service.AddToLibraryAsync(
                    It.IsAny<ListenarrAddToLibraryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ListenarrAddToLibraryRequest request, CancellationToken _) =>
                {
                    var asin = request.Metadata.Asin;
                    if (asin == FailAsin)
                        throw new HttpRequestException("upstream rejected this book");

                    AddedAsins.Add(asin);
                    AddRequests.Add(request);
                    return new ListenarrLibraryAddResponse(
                        "added",
                        new ListenarrLibraryItem(
                            Interlocked.Increment(ref _nextId), asin, request.Metadata.Title,
                            request.Metadata.Authors, request.Metadata.Narrators, null, true, null),
                        AlreadyExisted: false);
                });

            var audiobookshelf = new Mock<IAudiobookshelfService>();
            audiobookshelf.Setup(service => service.GetLibraryItemsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(owned ?? []);

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Listenarr:DefaultRegion"] = "us",
                ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"audiobook-series-{Guid.NewGuid():N}.db")
            }).Build();

            var store = new AudiobookRequestStore(new AppDatabase(configuration));
            var tokens = new AudiobookRequestTokenStore(TimeProvider.System);
            var availability = new AudiobookAvailabilityService(
                listenarr.Object, audiobookshelf.Object, store, configuration);
            var reconciler = new AudiobookRequestReconciler(
                audiobookshelf.Object, store, Mock.Of<IMediaMetadataService>(), TimeProvider.System);
            var requests = new AudiobookRequestService(
                listenarr.Object, store, tokens, reconciler, configuration, TimeProvider.System);

            Service = new AudiobookSeriesService(listenarr.Object, availability, requests, tokens);
        }
    }
}
