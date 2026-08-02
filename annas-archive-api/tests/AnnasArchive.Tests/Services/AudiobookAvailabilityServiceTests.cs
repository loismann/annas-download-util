using System.Text.Json.Nodes;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AnnasArchive.Tests.Services;

public sealed class AudiobookAvailabilityServiceTests
{
    [Fact]
    public async Task Search_PrefersExactOwnedIdentifier_ThenExactListenarrRequest()
    {
        var ownedCandidate = Candidate("OWNED-ASIN", "Pride and Prejudice", "Jane Austen", "Rosamund Pike");
        var requestedCandidate = Candidate("REQUESTED-ASIN", "Persuasion", "Jane Austen", "Juliet Stevenson");
        var availableCandidate = Candidate("AVAILABLE-ASIN", "Emma", "Jane Austen", "Emma Thompson");
        var listenarr = ListenarrReturning(
            [ownedCandidate, requestedCandidate, availableCandidate],
            [new ListenarrLibraryItem(42, "REQUESTED-ASIN", "Persuasion", ["Jane Austen"], ["Juliet Stevenson"], null, true, null)]);
        var abs = AudiobookshelfReturning(JsonNode.Parse("""
            [{
              "id": "abs-owned",
              "media": { "metadata": {
                "title": "Pride and Prejudice",
                "authorName": "Jane Austen",
                "narratorName": "Rosamund Pike",
                "asin": "OWNED-ASIN"
              }}
            }]
            """)!.AsArray());
        var service = Create(listenarr.Object, abs.Object);

        var result = await service.SearchAsync("Jane Austen", "us", null);

        result.Results.Should().HaveCount(3);
        result.Results[0].Availability.Should().Be("owned");
        result.Results[0].OwnedAudiobookshelfId.Should().Be("abs-owned");
        result.Results[1].Availability.Should().Be("requested");
        result.Results[1].ListenarrId.Should().Be(42);
        result.Results[2].Availability.Should().Be("available");
    }

    [Fact]
    public async Task Search_DoesNotCallDifferentNarratorEditionOwned()
    {
        var candidate = Candidate("NEW-ASIN", "Pride and Prejudice", "Jane Austen", "Rosamund Pike");
        var listenarr = ListenarrReturning([candidate], []);
        var abs = AudiobookshelfReturning(JsonNode.Parse("""
            [{
              "id": "abs-other-edition",
              "media": { "metadata": {
                "title": "Pride and Prejudice",
                "authorName": "Jane Austen",
                "narratorName": "Lindsay Duncan"
              }}
            }]
            """)!.AsArray());
        var service = Create(listenarr.Object, abs.Object);

        var result = await service.SearchAsync("Pride and Prejudice", null, "english");

        result.Region.Should().Be("us");
        result.Language.Should().Be("english");
        result.Results.Should().ContainSingle();
        result.Results[0].Availability.Should().Be("available");
        result.Results[0].AvailabilityReason.Should().BeNull();
    }

    private static AudiobookAvailabilityService Create(
        IListenarrService listenarr,
        IAudiobookshelfService abs)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Listenarr:DefaultRegion"] = "us"
        }).Build();
        return new AudiobookAvailabilityService(listenarr, abs, configuration);
    }

    private static Mock<IListenarrService> ListenarrReturning(
        IReadOnlyList<ListenarrAudibleSearchResult> search,
        IReadOnlyList<ListenarrLibraryItem> library)
    {
        var mock = new Mock<IListenarrService>();
        mock.Setup(service => service.SearchAudibleAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListenarrAudibleSearchResponse(search, search.Count));
        mock.Setup(service => service.GetLibraryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(library);
        return mock;
    }

    private static Mock<IAudiobookshelfService> AudiobookshelfReturning(JsonArray items)
    {
        var mock = new Mock<IAudiobookshelfService>();
        mock.Setup(service => service.GetLibraryItemsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(items);
        return mock;
    }

    private static ListenarrAudibleSearchResult Candidate(
        string asin,
        string title,
        string author,
        string narrator) => new(
            asin,
            title,
            null,
            [new ListenarrAudibleAuthor(null, author, "us")],
            [new ListenarrAudibleNarrator(narrator)],
            [],
            [],
            null,
            600,
            null,
            null,
            "english",
            "unabridged",
            null,
            null,
            null,
            "us");
}
