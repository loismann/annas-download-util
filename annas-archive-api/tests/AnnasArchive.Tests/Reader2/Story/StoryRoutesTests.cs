using System.Net;
using System.Net.Http.Json;
using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Endpoints;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Story;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// The story-model routes, over real HTTP.
///
/// <para>What only an assembled app can answer: that the routes are wired to the
/// handlers anyone thinks they are, that a book type without a cast refuses
/// rather than inventing one, and — the one that matters most — that turning
/// auto-ingest off actually stops a model call rather than merely hiding a
/// step.</para>
/// </summary>
[Collection("Sequential")]
public sealed class StoryRoutesTests : IDisposable
{
    private readonly Reader2AppFactory _app = new();
    private HttpClient? _client;

    public void Dispose()
    {
        _client?.Dispose();
        _app.Dispose();
    }

    /// <summary>
    /// Built on first use, not in the constructor: every test here sets
    /// <see cref="Reader2AppFactory.Settings"/> first, and the host is composed the
    /// moment a client is created.
    /// </summary>
    private HttpClient Client => _client ??= _app.SignedInAs("paul");

    private async Task<string> NovelAsync(string lensKey = "fiction")
    {
        _app.AddBook(EpubBuilder.Epub3WithNav(), "novel.epub");

        var enrolled = await Client.PostAsJsonAsync(
            "/api/reader2/books", new EnrolBookRequest("novel.epub", lensKey));

        var book = (await enrolled.Content.ReadFromJsonAsync<BookResponse>())!.BookId;
        (await Client.PostAsync($"/api/reader2/books/{book}/ingest", null)).EnsureSuccessStatusCode();

        return book;
    }

    private Task<HttpResponseMessage> SummariseAsync(string book, int chapter = 0) =>
        Client.PostAsync($"/api/reader2/books/{book}/chapters/{chapter}/summary", null);

    private int Extractions => _app.Ai.CallsOf(CallKind.StoryExtraction);

    // ─── the configuration gate ─────────────────────────────────────────

    /// <summary>
    /// Extraction rides a summary the reader asked for, and it is announced in the
    /// stream rather than hidden. It costs one fast-model call over prose already
    /// paid for.
    /// </summary>
    [Fact]
    public async Task A_chapter_summary_folds_that_chapter_into_the_story_model()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();

        var body = await (await SummariseAsync(book)).Content.ReadAsStringAsync();

        Extractions.Should().Be(1);
        body.Should().Contain("story model", "a call the reader did not click for is shown as a step");
    }

    /// <summary>
    /// The gate is around the call, not around the reporting of it. A flag that
    /// only stopped the progress message would be worse than no flag at all.
    /// </summary>
    [Fact]
    public async Task With_auto_ingest_off_a_chapter_summary_makes_no_extraction_call()
    {
        _app.Settings["Reader2:StoryModel:AutoIngestOnSummary"] = "false";
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();

        (await SummariseAsync(book)).EnsureSuccessStatusCode();

        Extractions.Should().Be(0);
        _app.Ai.Calls.Should().NotBeEmpty("the summary itself still happens");
    }

    /// <summary>A book type with no cast never pays for one, however the flag is set.</summary>
    [Fact]
    public async Task A_literary_book_never_ingests_a_story_model()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync("literary");

        (await SummariseAsync(book)).EnsureSuccessStatusCode();

        Extractions.Should().Be(0);
    }

    // ─── reading ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_model_is_served_with_the_lenses_own_words_for_its_parts()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var model = await Client.GetFromJsonAsync<StoryModelResponse>(
            $"/api/reader2/books/{book}/story-model?throughChapter=0");

        model!.Actors.Should().ContainSingle(a => a.CanonicalName == "Pierre");
        model.Vocabulary.Should().Be(new StoryVocabulary("Characters", "Factions", "Plot threads"));
        model.ThroughChapter.Should().Be(0);
    }

    /// <summary>
    /// The military lens gets the same machinery under its own nouns — which is
    /// the whole reason the vocabulary is on the lens rather than in the client.
    /// </summary>
    [Fact]
    public async Task A_campaign_history_gets_the_same_model_under_different_nouns()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync("military");
        await SummariseAsync(book);

        var model = await Client.GetFromJsonAsync<StoryModelResponse>(
            $"/api/reader2/books/{book}/story-model?throughChapter=0");

        model!.Vocabulary.Actors.Should().Be("Commanders & Units");
    }

    [Fact]
    public async Task A_book_type_that_builds_no_story_model_says_so_rather_than_404ing()
    {
        var book = await NovelAsync("literary");

        var response = await Client.GetAsync($"/api/reader2/books/{book}/story-model");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the book and the route both exist; only the cast does not");
    }

    // ─── ingesting on request ───────────────────────────────────────────

    [Fact]
    public async Task A_chapter_with_no_summary_is_refused_rather_than_summarised()
    {
        _app.Settings["Reader2:StoryModel:AutoIngestOnSummary"] = "false";
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/reader2/books/{book}/story-model/ingest", new IngestChapterRequest(0));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("summarise it first");
        Extractions.Should().Be(0);
    }

    [Fact]
    public async Task Ingesting_the_same_chapter_twice_over_http_buys_one_extraction()
    {
        _app.Settings["Reader2:StoryModel:AutoIngestOnSummary"] = "false";
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        for (var i = 0; i < 2; i++)
            (await Client.PostAsJsonAsync(
                $"/api/reader2/books/{book}/story-model/ingest", new IngestChapterRequest(0)))
                .EnsureSuccessStatusCode();

        Extractions.Should().Be(1);
    }

    /// <summary>
    /// Every route here is a POST except the read. A GET that ingested would be one
    /// a browser could prefetch, a crawler could follow, and a refresh could
    /// re-bill — so what is asserted is that nothing was spent, not which of the
    /// two refusing status codes routing happens to pick.
    /// </summary>
    [Fact]
    public async Task Ingesting_is_not_reachable_by_a_get()
    {
        _app.Settings["Reader2:StoryModel:AutoIngestOnSummary"] = "false";
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var response = await Client.GetAsync($"/api/reader2/books/{book}/story-model/ingest");

        response.IsSuccessStatusCode.Should().BeFalse();
        Extractions.Should().Be(0);
    }

    [Fact]
    public async Task The_story_model_routes_need_a_signed_in_reader()
    {
        var book = await NovelAsync();
        using var stranger = _app.CreateClient();

        (await stranger.GetAsync($"/api/reader2/books/{book}/story-model"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>One actor, so a test can assert on a name rather than on a count.</summary>
    private static string Answer(AnnasArchive.API.Services.Ai.AiChatCall call) =>
        call.Endpoint == ModelCalls.EndpointName(CallKind.StoryExtraction)
            ? """{"newActors": [{"canonicalName": "Pierre", "tier": "major"}]}"""
            : $"[{call.Endpoint}]";
}
