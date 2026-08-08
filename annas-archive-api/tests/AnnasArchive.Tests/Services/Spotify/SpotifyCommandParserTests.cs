using AnnasArchive.Core.Services;
using System.Net;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.API.Services.Spotify;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// The parser is the one place an outside system's output becomes an instruction,
/// so most of these are about what it refuses to accept from the model.
/// </summary>
public class SpotifyCommandParserTests
{
    [Fact]
    public async Task ParsesAWellFormedEnvelope()
    {
        var parser = ParserReturning("""
            { "schemaVersion": 1, "action": "list_playlist_items",
              "arguments": { "playlistReference": "Lucy + Laura" }, "confidence": 0.95 }
            """);

        var command = await parser.ParseAsync("what is in Lucy + Laura");

        command.Action.Should().Be(SpotifyReadAction.ListPlaylistItems);
        command.Arguments.PlaylistReference.Should().Be("Lucy + Laura");
        command.Confidence.Should().Be(0.95);
    }

    [Fact]
    public async Task RefusesAWriteActionEvenWhenTheModelIsCertain()
    {
        // The prototype prompt trained on these verbs; a model still emitting one
        // must not reach anything that mutates the account.
        var parser = ParserReturning("""
            { "schemaVersion": 1, "action": "create_playlist",
              "arguments": { "query": "Delta Blues" }, "confidence": 1.0 }
            """);

        (await parser.ParseAsync("make me a playlist")).Action.Should().Be(SpotifyReadAction.Unknown);
    }

    [Fact]
    public async Task RejectsAnEnvelopeCarryingPropertiesTheContractDoesNotDeclare()
    {
        // UnmappedMemberHandling.Disallow. A model that starts inventing fields
        // should fail loudly rather than have them silently ignored.
        var parser = ParserReturning("""
            { "schemaVersion": 1, "action": "list_playlists", "confidence": 1.0,
              "playlistId": "37i9dQ", "trackUris": ["spotify:track:x"] }
            """);

        (await parser.ParseAsync("show playlists")).Action.Should().Be(SpotifyReadAction.Unknown);
    }

    [Fact]
    public async Task RejectsAnEnvelopeFromAnotherSchemaVersion()
    {
        var parser = ParserReturning("""
            { "schemaVersion": 99, "action": "list_playlists", "confidence": 1.0 }
            """);

        (await parser.ParseAsync("show playlists")).Action.Should().Be(SpotifyReadAction.Unknown);
    }

    [Fact]
    public async Task SurvivesMarkdownFencedJson()
    {
        var parser = ParserReturning("```json\n{ \"schemaVersion\": 1, \"action\": \"list_playlists\", \"confidence\": 1.0 }\n```");

        (await parser.ParseAsync("show playlists")).Action.Should().Be(SpotifyReadAction.ListPlaylists);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ broken")]
    [InlineData("")]
    public async Task DegradesToAQuestionRatherThanThrowingOnUnusableOutput(string content)
    {
        var parser = ParserReturning(content);

        var command = await parser.ParseAsync("show playlists");

        command.Action.Should().Be(SpotifyReadAction.Unknown);
        command.Clarification.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DegradesToAQuestionWhenOpenAiFails()
    {
        var parser = ParserReturning("{}", HttpStatusCode.InternalServerError);

        (await parser.ParseAsync("show playlists")).Action.Should().Be(SpotifyReadAction.Unknown);
    }

    [Fact]
    public async Task DegradesToAQuestionWhenTheNetworkIsDown()
    {
        var parser = ParserThrowing(new HttpRequestException("no route to host"));

        var command = await parser.ParseAsync("show playlists");

        command.Action.Should().Be(SpotifyReadAction.Unknown);
        command.Clarification.Should().NotBeNullOrWhiteSpace();
    }

    // ─── no-AI paths ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("what can you do?")]
    [InlineData("What can I ask you?")]
    [InlineData("can you answer questions about my playlists")]
    public async Task AnswersCapabilityQuestionsWithoutSpendingAnAiCall(string message)
    {
        var calls = 0;
        var parser = ParserReturning("{}", HttpStatusCode.OK, () => calls++);

        var command = await parser.ParseAsync(message);

        command.Action.Should().Be(SpotifyReadAction.ExplainCapability);
        calls.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DoesNotCallOpenAiForAnEmptyMessage(string message)
    {
        var calls = 0;
        var parser = ParserReturning("{}", HttpStatusCode.OK, () => calls++);

        (await parser.ParseAsync(message)).Action.Should().Be(SpotifyReadAction.Unknown);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task SaysSoRatherThanCrashingWhenNoApiKeyIsConfigured()
    {
        var parser = ParserReturning("{}", apiKey: null);

        var command = await parser.ParseAsync("show my playlists");

        command.Action.Should().Be(SpotifyReadAction.Unknown);
        command.Clarification.Should().Contain("not configured");
    }

    // ─── the policy boundary ─────────────────────────────────────────────────

    [Fact]
    public async Task SendsTheUsersOwnWordsAndTheActionCatalogToTheModel()
    {
        string? body = null;
        var parser = ParserReturning(
            """{ "schemaVersion": 1, "action": "list_playlists", "confidence": 1.0 }""",
            captureBody: content => body = content);

        await parser.ParseAsync("what is in Lucy and Laura");

        body.Should().NotBeNull();
        body.Should().Contain("Lucy and Laura");         // the user's own words
        body.Should().Contain("list_playlist_items");    // the catalog
    }

    [Fact]
    public async Task ForwardsWhateverContextItIsGiven_SoCallersMustNotPassSpotifyContent()
    {
        // Documents where the policy boundary actually is. This method has no way to
        // tell Spotify content from the user's own words, so it cannot enforce the
        // rule — it forwards what it is handed. The guarantee that no playlist name
        // or track ID reaches OpenAI lives in SpotifyConversationService, which
        // passes null, and is pinned by a test there. If a future caller starts
        // passing transcript here, this test is the reminder that it leaks.
        string? body = null;
        var parser = ParserReturning(
            """{ "schemaVersion": 1, "action": "list_playlists", "confidence": 1.0 }""",
            captureBody: content => body = content);

        await parser.ParseAsync("show more", conversationContext: "assistant: You have 3 playlists");

        body.Should().Contain("You have 3 playlists");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static SpotifyCommandParser ParserReturning(
        string content,
        HttpStatusCode status = HttpStatusCode.OK,
        Action? onCall = null,
        string? apiKey = "test-key",
        Action<string>? captureBody = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });

        var handler = new StubHandler(async request =>
        {
            onCall?.Invoke();
            if (captureBody != null && request.Content != null)
                captureBody(await request.Content.ReadAsStringAsync());

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });

        return Build(handler, apiKey);
    }

    private static SpotifyCommandParser ParserThrowing(Exception exception) =>
        Build(new StubHandler(_ => throw exception), "test-key");

    private static SpotifyCommandParser Build(HttpMessageHandler handler, string? apiKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = apiKey })
            .Build();

        // The real chat client over the stubbed handler, so the tests that read
        // the outgoing request body still have one to read.
        var chat = new AiChatCompletion(
            new StubFactory(new HttpClient(handler)),
            new OpenAiModelHelper(),
            new AiResponseParser(),
            Mock.Of<ITokenUsageService>());

        return new SpotifyCommandParser(configuration, chat);
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
