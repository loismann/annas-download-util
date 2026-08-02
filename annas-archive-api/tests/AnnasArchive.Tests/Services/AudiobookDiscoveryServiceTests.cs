using System.Text.Json;
using System.Text.Json.Nodes;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The AI gate in DOCS/features/LISTENARR_INTEGRATION.md: a recommendation
/// may only become requestable once it has been matched to a real catalog
/// edition, and an invented or ambiguous suggestion must stay unrequestable.
/// </summary>
public sealed class AudiobookDiscoveryServiceTests
{
    [Fact]
    public async Task Resolve_ExactTitleAndAuthor_BecomesOneRequestableEdition()
    {
        var service = Create(_ => [Edition("B0EXACT001", "Project Hail Mary", "Andy Weir", "Ray Porter")]);

        var response = await service.ResolveAsync(
            "Modern science fiction", [Candidate("Project Hail Mary", "Andy Weir")], "us");

        response.Results.Should().ContainSingle();
        response.Results[0].Resolution.Should().Be("resolved");
        response.Results[0].Match!.Asin.Should().Be("B0EXACT001");
        response.Results[0].Match!.Availability.Should().Be("available");
        response.ResolvedCount.Should().Be(1);
        response.Summary.Should().Be("Modern science fiction");
    }

    [Fact]
    public async Task Resolve_SeveralNarratorEditions_StaysAmbiguousAndCarriesNoMatch()
    {
        var service = Create(_ =>
        [
            Edition("B0NARR0001", "The Fellowship of the Ring", "J.R.R. Tolkien", "Andy Serkis"),
            Edition("B0NARR0002", "The Fellowship of the Ring", "J.R.R. Tolkien", "Rob Inglis")
        ]);

        var response = await service.ResolveAsync(
            null, [Candidate("The Fellowship of the Ring", "J.R.R. Tolkien")], "us");

        var result = response.Results[0];
        result.Resolution.Should().Be("ambiguous");
        result.Match.Should().BeNull("an ambiguous suggestion must not be requestable");
        result.Choices.Should().HaveCount(2);
        response.AmbiguousCount.Should().Be(1);
    }

    [Fact]
    public async Task Resolve_NamedNarrator_PicksThatEditionOutOfSeveral()
    {
        var service = Create(_ =>
        [
            Edition("B0NARR0001", "The Fellowship of the Ring", "J.R.R. Tolkien", "Andy Serkis"),
            Edition("B0NARR0002", "The Fellowship of the Ring", "J.R.R. Tolkien", "Rob Inglis")
        ]);

        var response = await service.ResolveAsync(
            null,
            [Candidate("The Fellowship of the Ring", "J.R.R. Tolkien", narrator: "Andy Serkis")],
            "us");

        response.Results[0].Resolution.Should().Be("resolved");
        response.Results[0].Match!.Asin.Should().Be("B0NARR0001");
        response.Results[0].NarratorPreference.Should().Be("Andy Serkis",
            "the request must stay on manual review to prove the narrator");
    }

    [Fact]
    public async Task Resolve_HallucinatedWork_IsNotFoundAndNeverTakesTheTopResult()
    {
        var service = Create(_ => [Edition("B0OTHER001", "A Completely Different Book", "Someone Else", "A Narrator")]);

        var response = await service.ResolveAsync(
            null, [Candidate("The Nonexistent Chronicles", "Imaginary Author")], "us");

        var result = response.Results[0];
        result.Resolution.Should().Be("notFound");
        result.Match.Should().BeNull();
        result.Choices.Should().BeEmpty();
        response.NotFoundCount.Should().Be(1);
    }

    [Fact]
    public async Task Resolve_OwnedAndRequestedWorks_AreLabelledNotSilentlyDropped()
    {
        var owned = JsonNode.Parse("""
            [{
              "id": "abs-owned",
              "media": { "metadata": {
                "title": "Project Hail Mary",
                "authorName": "Andy Weir",
                "narratorName": "Ray Porter",
                "asin": "B0EXACT001"
              }}
            }]
            """)!.AsArray();
        var service = Create(
            query => query.Contains("Hail Mary")
                ? [Edition("B0EXACT001", "Project Hail Mary", "Andy Weir", "Ray Porter")]
                : [Edition("B0REQ00001", "Artemis", "Andy Weir", "Rosario Dawson")],
            owned,
            [new ListenarrLibraryItem(7, "B0REQ00001", "Artemis", ["Andy Weir"], null, null, true, null)]);

        var response = await service.ResolveAsync(
            null,
            [Candidate("Project Hail Mary", "Andy Weir"), Candidate("Artemis", "Andy Weir")],
            "us");

        response.Results.Should().HaveCount(2, "owned works are labelled, not hidden");
        response.Results[0].Match!.Availability.Should().Be("owned");
        response.Results[1].Match!.Availability.Should().Be("requested");
        response.Results[1].Match!.ListenarrId.Should().Be(7);
        response.OwnedCount.Should().Be(1);
    }

    [Fact]
    public async Task Resolve_SuggestionWithoutAnAuthor_AlwaysRequiresReview()
    {
        var service = Create(_ => [Edition("B0TITLE001", "Persuasion", "Jane Austen", "Juliet Stevenson")]);

        var response = await service.ResolveAsync(null, [Candidate("Persuasion", author: null)], "us");

        response.Results[0].Resolution.Should().Be("ambiguous");
        response.Results[0].Match.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_UpstreamFailureForOneSuggestion_DoesNotFailTheBatch()
    {
        var service = Create(query => query.Contains("Broken")
            ? throw new HttpRequestException("catalog down")
            : [Edition("B0EXACT001", "Project Hail Mary", "Andy Weir", "Ray Porter")]);

        var response = await service.ResolveAsync(
            null,
            [Candidate("Broken Suggestion", "Nobody"), Candidate("Project Hail Mary", "Andy Weir")],
            "us");

        response.Results[0].Resolution.Should().Be("notFound");
        response.Results[1].Resolution.Should().Be("resolved");
    }

    /* ── AI privacy: the prompt may contain the user's words and nothing else ── */

    [Fact]
    public void Prompt_ContainsOnlyTheUserQuery_AndNoLibraryOrCatalogData()
    {
        var prompt = AudiobookDiscoveryPrompt.BuildUserPrompt("literary sci-fi under 12 hours", 10);
        var everything = (AudiobookDiscoveryPrompt.SystemPrompt + prompt).ToLowerInvariant();

        prompt.Should().Contain("literary sci-fi under 12 hours");

        // Names of the systems the model must never learn about, and the
        // shapes their data would take. "ASIN"/"ISBN" appear in the prompt as
        // prohibitions, so identifier *values* are asserted separately below.
        foreach (var forbidden in new[]
        {
            "audiobookshelf", "listenarr", "prowlarr", "qbittorrent", "sabnzbd",
            "/data/", "abs-", "magnet:", "http://", "https://", "x-api-key"
        })
        {
            everything.Should().NotContain(forbidden,
                $"the model must never receive {forbidden} data");
        }

        // Nothing identifier-shaped: a real ASIN is 10 characters starting B0.
        System.Text.RegularExpressions.Regex.IsMatch(prompt, @"\bB0[A-Z0-9]{8}\b")
            .Should().BeFalse("no catalog identifier may reach the model");
    }

    [Theory]
    [InlineData(null, 12)]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    [InlineData(500, 30)]
    public void Prompt_ClampsTheRequestedCount(int? requested, int expected) =>
        AudiobookDiscoveryPrompt.ClampCount(requested).Should().Be(expected);

    [Fact]
    public void Prompt_CapsEachReasonAtFortyWords()
    {
        var trimmed = AudiobookDiscoveryPrompt.TrimReason(string.Join(' ', Enumerable.Repeat("word", 60)));

        trimmed!.Split(' ').Should().HaveCount(AudiobookDiscoveryPrompt.MaxReasonWords);
        trimmed.Should().EndWith("…", "a truncated reason must look truncated");
    }

    [Fact]
    public void Prompt_ParsesCandidates_AndTreatsLiteralNullStringsAsAbsent()
    {
        using var document = JsonDocument.Parse("""
            {
              "results": [
                { "title": "Good Book", "author": "Real Author", "year": 2019,
                  "series": "null", "narratorPreference": "  ", "reason": "Fits the mood." },
                { "title": "   " }
              ]
            }
            """);

        var candidates = AudiobookDiscoveryPrompt.ParseCandidates(document.RootElement);

        candidates.Should().ContainSingle("an entry without a title is unusable");
        candidates[0].Title.Should().Be("Good Book");
        candidates[0].Year.Should().Be(2019);
        candidates[0].Series.Should().BeNull();
        candidates[0].NarratorPreference.Should().BeNull();
    }

    /* ── helpers ───────────────────────────────────────────────────────── */

    private static AudiobookDiscoveryService Create(
        Func<string, IReadOnlyList<ListenarrAudibleSearchResult>> search,
        JsonArray? owned = null,
        IReadOnlyList<ListenarrLibraryItem>? library = null)
    {
        var listenarr = new Mock<IListenarrService>();
        listenarr.Setup(service => service.SearchAudibleAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string query, string _, string? __, CancellationToken ___) =>
            {
                var results = search(query);
                return new ListenarrAudibleSearchResponse(results, results.Count);
            });
        listenarr.Setup(service => service.GetLibraryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(library ?? []);

        var audiobookshelf = new Mock<IAudiobookshelfService>();
        audiobookshelf.Setup(service => service.GetLibraryItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(owned ?? []);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Listenarr:DefaultRegion"] = "us",
            ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"listenarr-discovery-{Guid.NewGuid():N}.db")
        }).Build();
        var availability = new AudiobookAvailabilityService(
            listenarr.Object,
            audiobookshelf.Object,
            new AudiobookRequestStore(new AppDatabase(configuration)),
            configuration);

        return new AudiobookDiscoveryService(listenarr.Object, availability);
    }

    private static AudiobookDiscoveryCandidate Candidate(
        string title, string? author, string? narrator = null) =>
        new(title, author, null, null, null, narrator, "Because it fits.");

    private static ListenarrAudibleSearchResult Edition(
        string asin, string title, string author, string narrator) => new(
            asin, title, null,
            [new ListenarrAudibleAuthor(null, author, "us")],
            [new ListenarrAudibleNarrator(narrator)],
            [], [], null, 600, null, null, "english", "unabridged", null, null, null, "us");
}
