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

    // ─── the wire shape the browser actually parses ─────────────────────

    /// <summary>
    /// Tiers, thread statuses, and group kinds cross the wire by <b>name</b>.
    ///
    /// <para>The cast list filters on <c>"Major"</c>. The application registers no
    /// global string-enum converter, so the default was an integer, and the filter
    /// matched nobody from the day it shipped — the table reported "27 not shown"
    /// beside "Nothing matches those filters", which is precisely what it should
    /// say when a filter really does exclude everyone. Every frontend spec passed,
    /// because each hand-writes an actor with <c>tier: 'Major'</c> and so asserts
    /// the component works against a shape nothing checked the server produces.
    /// This is the assertion that had to live on this side of the wire.</para>
    /// </summary>
    [Fact]
    public async Task The_story_model_names_its_tiers_rather_than_numbering_them()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        (await SummariseAsync(book)).EnsureSuccessStatusCode();

        var body = await (await Client.GetAsync(
            $"/api/reader2/books/{book}/story-model")).Content.ReadAsStringAsync();

        body.Should().Contain("\"tier\":\"Major\"")
            .And.NotMatchRegex(@"""tier""\s*:\s*\d", "a number here is a filter that matches nobody");
    }

    // ─── the reader's own corrections ────────────────────────────────────

    private Task<HttpResponseMessage> CorrectAsync(string book, string actorId, object body) =>
        Client.PutAsJsonAsync($"/api/reader2/books/{book}/story-model/actors/{actorId}", body);

    private Task<HttpResponseMessage> HideAsync(string book, string actorId, bool hidden) =>
        Client.PutAsJsonAsync(
            $"/api/reader2/books/{book}/story-model/actors/{actorId}/hidden", new { hidden });

    private async Task<StoryModelResponse> ModelAsync(string book) =>
        (await Client.GetFromJsonAsync<StoryModelResponse>(
            $"/api/reader2/books/{book}/story-model"))!;

    [Fact]
    public async Task A_reader_can_rename_somebody_and_add_a_note()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var actor = (await ModelAsync(book)).Actors.Single();
        (await CorrectAsync(book, actor.Id, new { preferredName = "Petya", note = "lied in ch 3" }))
            .EnsureSuccessStatusCode();

        var corrected = (await ModelAsync(book)).Actors.Single();

        corrected.CanonicalName.Should().Be("Petya");
        corrected.Aliases.Should().Contain("Pierre", "the model's own name stays reachable");
        corrected.ReaderNote.Should().Be("lied in ch 3");
    }

    /// <summary>
    /// <b>The property the whole design exists for.</b> A rebuild empties the
    /// model and admits everybody again with fresh ids; the correction is stored
    /// against a name in a different artifact, so it lands on the same person.
    /// </summary>
    [Fact]
    public async Task A_correction_survives_a_rebuild_that_discards_the_record()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var actor = (await ModelAsync(book)).Actors.Single();
        await CorrectAsync(book, actor.Id, new { preferredName = "Petya", note = "lied in ch 3" });

        (await Client.PostAsync(
            $"/api/reader2/books/{book}/story-model/back-fill?rebuild=true", null))
            .EnsureSuccessStatusCode();

        var after = (await ModelAsync(book)).Actors.Single();

        after.CanonicalName.Should().Be("Petya");
        after.ReaderNote.Should().Be("lied in ch 3");
    }

    /// <summary>
    /// Hidden, not deleted. A cast this size has walk-ons that make the map
    /// unreadable, but the extraction did find them in the book — so they stay in
    /// the record, marked, and the map is what leaves them out.
    /// </summary>
    [Fact]
    public async Task Hiding_somebody_marks_them_rather_than_removing_them()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var actor = (await ModelAsync(book)).Actors.Single();
        await HideAsync(book, actor.Id, true);

        var after = (await ModelAsync(book)).Actors.Single();

        after.Hidden.Should().BeTrue();
        after.CanonicalName.Should().Be(actor.CanonicalName);
    }

    /// <summary>A rebuild renumbers everybody, so a hiding keyed on an id would move.</summary>
    [Fact]
    public async Task Hiding_survives_a_rebuild_that_discards_the_record()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var actor = (await ModelAsync(book)).Actors.Single();
        await HideAsync(book, actor.Id, true);

        (await Client.PostAsync(
            $"/api/reader2/books/{book}/story-model/back-fill?rebuild=true", null))
            .EnsureSuccessStatusCode();

        (await ModelAsync(book)).Actors.Single().Hidden.Should().BeTrue();
    }

    [Fact]
    public async Task Unhiding_somebody_puts_them_back_on_the_map()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var actor = (await ModelAsync(book)).Actors.Single();
        await HideAsync(book, actor.Id, true);
        await HideAsync(book, actor.Id, false);

        (await ModelAsync(book)).Actors.Single().Hidden.Should().BeFalse();
    }

    /// <summary>
    /// The reason hiding has a route of its own. The edit form does not offer
    /// hiding, so submitting it must not undo one — and the client could not
    /// resend the flag even if it wanted to, because a preferred name is
    /// projected onto the canonical one and nothing served back tells them apart.
    /// </summary>
    [Fact]
    public async Task Editing_a_note_does_not_un_hide_somebody()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var actor = (await ModelAsync(book)).Actors.Single();
        await HideAsync(book, actor.Id, true);
        await CorrectAsync(book, actor.Id, new { note = "the one who lied" });

        var after = (await ModelAsync(book)).Actors.Single();

        after.Hidden.Should().BeTrue();
        after.ReaderNote.Should().Be("the one who lied");
    }

    /// <summary>And the other way: hiding must not wipe a note they already had.</summary>
    [Fact]
    public async Task Hiding_somebody_keeps_the_note_they_already_had()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var actor = (await ModelAsync(book)).Actors.Single();
        await CorrectAsync(book, actor.Id, new { preferredName = "Petya", note = "lied in ch 3" });
        await HideAsync(book, actor.Id, true);

        var after = (await ModelAsync(book)).Actors.Single();

        after.Hidden.Should().BeTrue();
        after.ReaderNote.Should().Be("lied in ch 3");
        after.CanonicalName.Should().Be("Petya");
    }

    [Fact]
    public async Task Clearing_a_correction_puts_the_models_own_name_back()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var actor = (await ModelAsync(book)).Actors.Single();
        await CorrectAsync(book, actor.Id, new { preferredName = "Petya" });
        await CorrectAsync(book, actor.Id, new { preferredName = (string?)null });

        (await ModelAsync(book)).Actors.Single().CanonicalName.Should().Be("Pierre");
    }

    /// <summary>Fixing a name must not be refusable at the spending limit.</summary>
    [Fact]
    public async Task Correcting_somebody_reaches_no_model()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();
        await SummariseAsync(book);

        var actor = (await ModelAsync(book)).Actors.Single();
        _app.Ai.Calls.Clear();

        await CorrectAsync(book, actor.Id, new { preferredName = "Petya" });

        _app.Ai.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Correcting_somebody_who_is_not_in_the_record_is_a_404()
    {
        _app.Ai.Answer = Answer;
        var book = await NovelAsync();

        (await CorrectAsync(book, "a999", new { preferredName = "Nobody" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>One actor, so a test can assert on a name rather than on a count.</summary>
    private static string Answer(AnnasArchive.API.Services.Ai.AiChatCall call) =>
        call.Endpoint == ModelCalls.EndpointName(CallKind.StoryExtraction)
            ? """{"newActors": [{"canonicalName": "Pierre", "tier": "major"}]}"""
            : $"[{call.Endpoint}]";
}
