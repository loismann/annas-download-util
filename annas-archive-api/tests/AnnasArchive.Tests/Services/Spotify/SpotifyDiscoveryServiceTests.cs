using System.Net;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Spotify;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AnnasArchive.Tests.Services.Spotify;

public sealed class SpotifyDiscoveryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"spotify-discovery-{Guid.NewGuid():N}");
    private readonly SpotifyDiscoveryStore _store;

    public SpotifyDiscoveryServiceTests()
    {
        Directory.CreateDirectory(_directory);
        var database = new AppDatabase(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_directory, "app.db")
            })
            .Build());
        _store = new SpotifyDiscoveryStore(database);
    }

    [Fact]
    public async Task Generate_UsesOnlyUserTextForAiThenResolvesWithSpotify()
    {
        var handler = new CapturingHandler([
            AiResponse("""
                {
                  "suggestedName": "Southern Crossroads",
                  "summary": "A historical sequence",
                  "clarifyingQuestion": null,
                  "candidates": [
                    { "artist": "Sister Rosetta Tharpe", "title": "Up Above My Head", "rationale": "Gospel and early rock" }
                  ]
                }
                """)
        ]);
        var spotify = StrictSpotify([
            Track("catalog-secret", "Up Above My Head", "Sister Rosetta Tharpe")
        ]);
        var known = KnownMusic("private-library-sentinel|private-song");
        var service = CreateService(handler, spotify.Object, known.Object);

        var draft = await service.CreateAsync("1950s Deep South music", 25);

        draft.State.Should().Be(SpotifyDiscoveryDraftState.Ready);
        draft.Candidates.Should().ContainSingle();
        draft.Candidates[0].Track!.Uri.Should().Be("spotify:track:catalog-secret");
        draft.Candidates[0].ProbablyUnfamiliar.Should().BeTrue();
        handler.Bodies.Should().ContainSingle();
        handler.Bodies[0].Should().Contain("1950s Deep South music");
        handler.Bodies[0].Should().NotContain("private-library-sentinel");
        handler.Bodies[0].Should().NotContain("spotify:track:catalog-secret");
        spotify.Verify(service => service.CreatePlaylistAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refine_DoesNotSendResolvedSpotifyContentBackToAi()
    {
        var handler = new CapturingHandler([
            AiResponse(CandidateResponse("First Draft", "Song One", "Artist One")),
            AiResponse(CandidateResponse("Refined Draft", "Song Two", "Artist Two"))
        ]);
        var spotify = new Mock<ISpotifyService>(MockBehavior.Strict);
        spotify.Setup(service => service.SearchTracksAsync(
                It.IsAny<string>(), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string query, int _, CancellationToken _) =>
                query.Contains("Song One", StringComparison.Ordinal)
                    ? new SpotifySearchResultDto([Track("spotify-private-one", "Song One", "Artist One")], 1)
                    : new SpotifySearchResultDto([Track("spotify-private-two", "Song Two", "Artist Two")], 1));
        var service = CreateService(handler, spotify.Object, KnownMusic().Object);

        var first = await service.CreateAsync("Start with gospel");
        var second = await service.RefineAsync(first.Id, "More blues, less country", 30);

        second.Id.Should().Be(first.Id);
        second.DesiredTrackCount.Should().Be(30);
        second.UserPrompts.Should().Equal("Start with gospel", "More blues, less country");
        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[1].Should().Contain("Start with gospel");
        handler.Bodies[1].Should().Contain("More blues, less country");
        handler.Bodies[1].Should().NotContain("spotify-private-one");
        handler.Bodies[1].Should().NotContain("spotify:track:spotify-private-one");
    }

    [Fact]
    public async Task MultipleExactSpotifyMatchesRemainAmbiguousForReview()
    {
        var handler = new CapturingHandler([
            AiResponse(CandidateResponse("Draft", "Mystery Train", "Little Junior's Blue Flames"))
        ]);
        var spotify = StrictSpotify([
            Track("one", "Mystery Train", "Little Junior's Blue Flames"),
            Track("two", "Mystery Train", "Little Junior's Blue Flames")
        ]);
        var service = CreateService(handler, spotify.Object, KnownMusic().Object);

        var draft = await service.CreateAsync("Sun Records roots");

        draft.State.Should().Be(SpotifyDiscoveryDraftState.Partial);
        draft.Candidates[0].Resolution.Should().Be(SpotifyCandidateResolution.Ambiguous);
        draft.Candidates[0].Track.Should().BeNull();
        draft.Candidates[0].Alternatives.Should().HaveCount(2);

        var reviewed = service.Update(draft.Id, new SpotifyDiscoveryDraftUpdateRequest(
            CandidateSelections: new Dictionary<string, string>
            {
                [draft.Candidates[0].Id] = "two"
            }));
        reviewed.State.Should().Be(SpotifyDiscoveryDraftState.Ready);
        reviewed.Candidates[0].Track!.Id.Should().Be("two");
        reviewed.Candidates[0].Alternatives.Should().BeEmpty();
    }

    [Fact]
    public void DraftsPersistAcrossStoreInstancesAndNeverCrossOwners()
    {
        var now = DateTimeOffset.UtcNow;
        var draft = new SpotifyDiscoveryDraft(
            "draft", SpotifyDiscoveryDraftState.Ready, "Name", "Summary", ["prompt"], 25,
            null, [], "coverage", now, now);

        _store.Save("owner-a", draft);

        _store.Get("owner-a", "draft").Should().BeEquivalentTo(draft);
        _store.Get("owner-b", "draft").Should().BeNull();
    }

    private SpotifyDiscoveryService CreateService(
        HttpMessageHandler handler,
        ISpotifyService spotify,
        ISpotifyKnownMusicService knownMusic)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "test-key" })
            .Build();
        return new SpotifyDiscoveryService(
            new StubFactory(new HttpClient(handler)), configuration, spotify, knownMusic,
            _store, new CurrentUser(), TimeProvider.System);
    }

    private static Mock<ISpotifyService> StrictSpotify(List<SpotifyTrackDto> tracks)
    {
        var spotify = new Mock<ISpotifyService>(MockBehavior.Strict);
        spotify.Setup(service => service.SearchTracksAsync(
                It.IsAny<string>(), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpotifySearchResultDto(tracks, tracks.Count));
        spotify.Setup(service => service.CreatePlaylistAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("A discovery draft must never write to Spotify."));
        return spotify;
    }

    private static Mock<ISpotifyKnownMusicService> KnownMusic(string trackKey = "")
    {
        var known = new Mock<ISpotifyKnownMusicService>(MockBehavior.Strict);
        var trackKeys = string.IsNullOrEmpty(trackKey)
            ? new HashSet<string>()
            : new HashSet<string> { trackKey };
        var index = new SpotifyKnownMusicIndex(
            new HashSet<string>(), trackKeys, 1, 0, true, true);
        known.Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpotifyKnownMusicReport(index, "Accessible evidence", DateTimeOffset.UtcNow));
        return known;
    }

    private static SpotifyTrackDto Track(string id, string name, string artist) =>
        new(id, name, $"spotify:track:{id}", 180_000, artist, "Catalog Album", null,
            $"https://open.spotify.com/track/{id}");

    private static string CandidateResponse(string name, string title, string artist) => $$"""
        {
          "suggestedName": "{{name}}",
          "summary": "A sequence",
          "clarifyingQuestion": null,
          "candidates": [
            { "artist": "{{artist}}", "title": "{{title}}", "rationale": "Fits the request" }
          ]
        }
        """;

    private static HttpResponseMessage AiResponse(string content)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CurrentUser : ISpotifyCurrentUser
    {
        public string GetRequiredOwnerKey() => "owner-a";
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public CapturingHandler(IEnumerable<HttpResponseMessage> responses) : this(new Queue<HttpResponseMessage>(responses)) { }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
