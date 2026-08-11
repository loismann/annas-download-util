using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Endpoints;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// The assembled application, over HTTP.
///
/// <para>Sequential because <see cref="Reader2AppFactory"/> points the library
/// root at a temporary directory through a process-wide environment variable.
/// </para>
/// </summary>
[Collection("Sequential")]
public sealed class Reader2IntegrationTests : IDisposable
{
    private readonly Reader2AppFactory _app = new();
    private readonly HttpClient _client;

    public Reader2IntegrationTests() => _client = _app.SignedInAs("paul");

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    private async Task<BookResponse> EnrolAsync(string? lensKey = null, string fileName = "book.epub")
    {
        _app.AddBook(EpubBuilder.Epub3WithNav(), fileName);

        var response = await _client.PostAsJsonAsync(
            "/api/reader2/books", new EnrolBookRequest(fileName, lensKey));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BookResponse>())!;
    }

    // ─── the shelf ───────────────────────────────────────────────────────

    /// <summary>
    /// The shelf draws the library's cover, so the field has to be on the wire —
    /// under this exact name, because <c>Book</c> in <c>reader2.models.ts</c>
    /// declares it and a rename here is a silent shelf of blank tiles.
    /// </summary>
    [Fact]
    public async Task Every_shelf_entry_carries_a_cover_field()
    {
        await EnrolAsync();

        using var shelf = JsonDocument.Parse(await _client.GetStringAsync("/api/reader2/books"));

        shelf.RootElement.EnumerateArray().Should().NotBeEmpty();
        foreach (var book in shelf.RootElement.EnumerateArray())
            book.TryGetProperty("coverUrl", out _).Should().BeTrue();
    }

    /// <summary>
    /// Null and not an empty string or a placeholder path: the shelf decides what
    /// to draw for a book with no picture, and it can only do that if "none" is
    /// distinguishable from "here it is".
    /// </summary>
    [Fact]
    public async Task A_book_the_library_has_no_cover_for_reports_none()
    {
        (await EnrolAsync()).CoverUrl.Should().BeNull();
    }

    // ─── the extensibility contract, over the wire ───────────────────────

    /// <summary>
    /// <see cref="TestLens"/> exists only in this project and is registered with
    /// one DI line. It has to reach the picker with no production change at all.
    /// </summary>
    [Fact]
    public async Task A_test_only_lens_appears_in_the_lenses_endpoint()
    {
        var lenses = await _client.GetFromJsonAsync<LensResponse[]>("/api/reader2/lenses");

        lenses.Should().Contain(l => l.Key == TestLens.LensKey);
        lenses.Should().Contain(l => l.Key == "literary" && l.IsDefault);
        lenses!.Single(l => l.Key == TestLens.LensKey).IsDefault
            .Should().BeFalse("adding a lens must not change the default");
    }

    [Fact]
    public async Task A_test_only_lens_is_selectable_by_patching_a_book()
    {
        var book = await EnrolAsync();

        var response = await _client.PatchAsJsonAsync(
            $"/api/reader2/books/{book.BookId}", new SetLensRequest(TestLens.LensKey));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<BookResponse>())!.LensKey
            .Should().Be(TestLens.LensKey);
    }

    [Fact]
    public async Task No_prompt_text_is_served_by_the_lenses_endpoint()
    {
        var body = await _client.GetStringAsync("/api/reader2/lenses");

        body.Should().NotContain("You are an").And.NotContain("test chapter summary prompt");
    }

    // ─── enrol → ingest → chapters → summarise ───────────────────────────

    [Fact]
    public async Task A_book_can_be_enrolled_ingested_read_and_summarised()
    {
        var book = await EnrolAsync();
        book.Title.Should().Be("An EPUB 3 Book");

        (await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var chapters = await _client.GetFromJsonAsync<ChapterListResponse>(
            $"/api/reader2/books/{book.BookId}/chapters");
        chapters!.Chapters.Should().HaveCount(3);

        _app.Ai.Answer = _ => "the summary";
        var summary = await _client.PostAsync(
            $"/api/reader2/books/{book.BookId}/chapters/0/summary", null);

        (await summary.Content.ReadAsStringAsync()).Should().Contain("the summary");
    }

    /// <summary>
    /// The chapter list says which chapters are already paid for.
    ///
    /// <para>The client cannot work this out: an artifact is keyed by lens and
    /// prompt version, so whether a summary counts as current is a question only
    /// the store can answer. Without the flag the reader has no way to see what
    /// they already bought, and buys it again.</para>
    /// </summary>
    [Fact]
    public async Task The_chapter_list_says_which_chapters_are_already_summarised()
    {
        var book = await EnrolAsync();
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);

        var before = await _client.GetFromJsonAsync<ChapterListResponse>(
            $"/api/reader2/books/{book.BookId}/chapters");
        before!.Chapters.Should().OnlyContain(c => !c.HasSummary);

        _app.Ai.Answer = _ => "the summary";
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/chapters/1/summary", null);

        var after = await _client.GetFromJsonAsync<ChapterListResponse>(
            $"/api/reader2/books/{book.BookId}/chapters");

        after!.Chapters.Single(c => c.Id == 1).HasSummary.Should().BeTrue();
        after.Chapters.Where(c => c.Id != 1).Should().OnlyContain(c => !c.HasSummary);
    }

    /// <summary>
    /// <b>A deploy that moves a prompt version must not cost the reader their
    /// summaries.</b> This is the whole of it, over real HTTP: summarise, move the
    /// version the way a deploy does, and read the book back.
    ///
    /// <para>It used to come back as though the book had never been read — the
    /// store treated an older prompt as a miss, so the tick disappeared, and the
    /// next press paid for a summary of prose that had not changed and overwrote
    /// the one already bought.</para>
    /// </summary>
    [Fact]
    public async Task A_summary_survives_the_prompt_version_moving_under_it()
    {
        var book = await EnrolAsync(TestLens.LensKey);
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);

        _app.Ai.Answer = _ => "the summary";
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/chapters/1/summary", null);
        var paidFor = _app.Ai.Calls.Count;

        try
        {
            TestLens.Version = 2;

            var after = await _client.GetFromJsonAsync<ChapterListResponse>(
                $"/api/reader2/books/{book.BookId}/chapters");
            var chapter = after!.Chapters.Single(c => c.Id == 1);

            chapter.HasSummary.Should().BeTrue("the prose it summarises has not changed");
            chapter.SummaryIsStale.Should().BeTrue("but a newer wording exists, and the reader may want it");

            // The summary itself still reads back, and reading it spends nothing.
            var summary = await _client.GetFromJsonAsync<Prose>(
                $"/api/reader2/books/{book.BookId}/chapters/1/summary");

            summary!.Markdown.Should().Be("the summary");
            _app.Ai.Calls.Count.Should().Be(paidFor, "reading what is already owned is free");
        }
        finally
        {
            TestLens.Version = 1;
        }
    }

    /// <summary>The reader can still choose to buy the newer wording.</summary>
    [Fact]
    public async Task A_stale_summary_is_replaced_only_when_the_reader_asks()
    {
        var book = await EnrolAsync(TestLens.LensKey);
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);

        _app.Ai.Answer = _ => "the old summary";
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/chapters/1/summary", null);

        try
        {
            TestLens.Version = 2;
            _app.Ai.Answer = _ => "the new summary";

            // Asking again without forcing keeps what is already owned.
            await _client.PostAsync($"/api/reader2/books/{book.BookId}/chapters/1/summary", null);
            (await _client.GetFromJsonAsync<Prose>(
                $"/api/reader2/books/{book.BookId}/chapters/1/summary"))!
                .Markdown.Should().Be("the old summary");

            await _client.PostAsync(
                $"/api/reader2/books/{book.BookId}/chapters/1/summary?force=true", null);

            var after = await _client.GetFromJsonAsync<ChapterListResponse>(
                $"/api/reader2/books/{book.BookId}/chapters");

            (await _client.GetFromJsonAsync<Prose>(
                $"/api/reader2/books/{book.BookId}/chapters/1/summary"))!
                .Markdown.Should().Be("the new summary");
            after!.Chapters.Single(c => c.Id == 1).SummaryIsStale.Should().BeFalse();
        }
        finally
        {
            TestLens.Version = 1;
        }
    }

    /// <summary>
    /// The other half of the tick: what the chapter list promises exists, the
    /// summary route can read back — free, and without the reader clicking
    /// "Summarise chapter" a second time to see it.
    /// </summary>
    [Fact]
    public async Task A_stored_chapter_summary_can_be_read_back_for_nothing()
    {
        var book = await EnrolAsync();
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);

        _app.Ai.Answer = _ => "what happened in chapter one";
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/chapters/1/summary", null);
        _app.Ai.Calls.Clear();

        var peeked = await _client.GetFromJsonAsync<Prose>(
            $"/api/reader2/books/{book.BookId}/chapters/1/summary");

        peeked!.Markdown.Should().Contain("what happened in chapter one");
        _app.Ai.Calls.Should().BeEmpty("reading what is already stored must never cost money");
    }

    /// <summary>
    /// A chapter the tick has not marked reads as nothing, not as an error — the
    /// panel's "nothing generated yet" state depends on this.
    /// </summary>
    [Fact]
    public async Task Peeking_a_chapter_with_no_summary_returns_nothing()
    {
        var book = await EnrolAsync();
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);

        var response = await _client.GetAsync($"/api/reader2/books/{book.BookId}/chapters/1/summary");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// The bug this closes: <c>HandleSectionVocab</c> answered both verbs and
    /// reached the generating path either way, so a <c>GET</c> to a section
    /// nobody had asked about yet quietly billed the household. A <c>GET</c> for
    /// an ungenerated section must now come back empty rather than paid for.
    /// </summary>
    [Fact]
    public async Task Reading_uncached_section_vocabulary_spends_nothing()
    {
        var book = await EnrolAsync();
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);
        _app.Ai.Calls.Clear(); // ingest's own chapter-labelling call is not what this test is about

        var response = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/reader2/books/{book.BookId}/chapters/0/sections/0/vocabulary");

        response.GetProperty("terms").GetArrayLength().Should().Be(0);
        _app.Ai.Calls.Should().BeEmpty("a GET must never be the request that pays for something");
    }

    /// <summary>
    /// A summary bought under one book type does not mark the chapter under
    /// another. The flag has to follow the same lens scoping the artifact does,
    /// or switching type would show work that is not there.
    /// </summary>
    [Fact]
    public async Task A_summary_under_one_book_type_does_not_mark_the_chapter_under_the_other()
    {
        var book = await EnrolAsync();
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);

        _app.Ai.Answer = _ => "the summary";
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/chapters/1/summary", null);

        await _client.PatchAsJsonAsync(
            $"/api/reader2/books/{book.BookId}", new SetLensRequest("military"));

        var chapters = await _client.GetFromJsonAsync<ChapterListResponse>(
            $"/api/reader2/books/{book.BookId}/chapters");

        chapters!.Chapters.Should().OnlyContain(c => !c.HasSummary);
    }

    [Fact]
    public async Task Chapter_text_and_sections_come_back_without_calling_a_model()
    {
        var book = await EnrolAsync();
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);
        _app.Ai.Calls.Clear();

        var chapter = await _client.GetFromJsonAsync<ChapterResponse>(
            $"/api/reader2/books/{book.BookId}/chapters/0");
        var sections = await _client.GetFromJsonAsync<SectionInfo[]>(
            $"/api/reader2/books/{book.BookId}/chapters/0/sections");

        chapter!.Text.Should().Contain("bright cold day");
        sections.Should().NotBeEmpty();
        _app.Ai.Calls.Should().BeEmpty("opening a book must never cost money");
    }

    [Fact]
    public async Task Search_finds_a_short_term_that_reader_one_would_have_refused()
    {
        var book = await EnrolAsync();
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);

        var hits = await _client.GetFromJsonAsync<JsonElementList>(
            $"/api/reader2/books/{book.BookId}/search?q=clocks");

        hits!.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reading_position_round_trips_and_is_per_reader()
    {
        var book = await EnrolAsync();

        (await _client.PutAsJsonAsync(
            $"/api/reader2/books/{book.BookId}/position", new SetPositionRequest(2, 400)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var other = _app.SignedInAs("someone-else");
        var theirs = await other.GetFromJsonAsync<PositionDto>(
            $"/api/reader2/books/{book.BookId}/position");

        theirs!.Chapter.Should().Be(0, "another reader's position is not mine");
    }

    /// <summary>
    /// The route half of the bookmark rules. <see cref="BookmarkTests"/> pins the
    /// store; this pins that the reader on the request is the one whose marks come
    /// back, and that a mark somebody else owns is a 404 rather than a deletion.
    /// </summary>
    [Fact]
    public async Task Bookmarks_round_trip_over_http_and_are_per_reader()
    {
        var book = await EnrolAsync();

        var created = await _client.PostAsJsonAsync(
            $"/api/reader2/books/{book.BookId}/bookmarks", new SaveBookmarkRequest(2, 400, "here"));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var mark = (await created.Content.ReadFromJsonAsync<BookmarkDto>())!;

        (await _client.GetFromJsonAsync<BookmarkDto[]>(
            $"/api/reader2/books/{book.BookId}/bookmarks"))!
            .Should().ContainSingle().Which.Label.Should().Be("here");

        using var other = _app.SignedInAs("someone-else");

        (await other.GetFromJsonAsync<BookmarkDto[]>(
            $"/api/reader2/books/{book.BookId}/bookmarks"))
            .Should().BeEmpty("another reader's marks are not mine");

        (await other.DeleteAsync($"/api/reader2/books/{book.BookId}/bookmarks/{mark.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await _client.DeleteAsync($"/api/reader2/books/{book.BookId}/bookmarks/{mark.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_bookmark_at_a_negative_place_is_refused()
    {
        var book = await EnrolAsync();

        (await _client.PostAsJsonAsync(
            $"/api/reader2/books/{book.BookId}/bookmarks", new SaveBookmarkRequest(-1, 0, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── refusals ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/reader2/lenses")]
    [InlineData("/api/reader2/books")]
    [InlineData("/api/reader2/preferences")]
    public async Task An_unauthenticated_request_is_rejected(string path)
    {
        using var anonymous = _app.CreateClient();

        (await anonymous.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_book_is_a_404()
    {
        (await _client.GetAsync("/api/reader2/books/0123456789abcdef/chapters"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_lens_key_is_a_400_naming_the_ones_that_exist()
    {
        _app.AddBook(EpubBuilder.Epub3WithNav(), "unknown-lens.epub");

        var response = await _client.PostAsJsonAsync(
            "/api/reader2/books", new EnrolBookRequest("unknown-lens.epub", "no-such-type"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("literary");
    }

    /// <summary>
    /// The gate has to answer as a status code. Once SSE headers are out there is
    /// no way to send one, which is why the check runs before the stream opens.
    /// </summary>
    [Fact]
    public async Task An_exhausted_allowance_answers_before_the_stream_opens()
    {
        var book = await EnrolAsync();
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);

        _app.Usage.CostUsd = 9999;
        var response = await _client.PostAsync(
            $"/api/reader2/books/{book.BookId}/chapters/0/summary", null);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
    }

    /// <summary>A failure mid-stream renders once, not twice.</summary>
    [Fact]
    public async Task A_mid_stream_failure_emits_exactly_one_error_event()
    {
        var book = await EnrolAsync();
        await _client.PostAsync($"/api/reader2/books/{book.BookId}/ingest", null);

        _app.Ai.Calls.Clear();
        _app.Ai.FailOnCall = 1;

        var body = await (await _client.PostAsync(
            $"/api/reader2/books/{book.BookId}/chapters/0/summary", null)).Content.ReadAsStringAsync();

        System.Text.RegularExpressions.Regex.Matches(body, @"""stage"":""error""")
            .Should().HaveCount(1);
    }

    [Fact]
    public async Task Un_enrolling_removes_the_book_from_the_shelf()
    {
        var book = await EnrolAsync();

        (await _client.DeleteAsync($"/api/reader2/books/{book.BookId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _client.GetFromJsonAsync<BookResponse[]>("/api/reader2/books"))
            .Should().BeEmpty();
    }

    private sealed record PositionDto(int Chapter, int WordOffset);

    private sealed record BookmarkDto(string Id, int Chapter, int WordOffset, string? Label);

    private sealed class JsonElementList : List<System.Text.Json.JsonElement>;

    /// <summary>
    /// The word lists come back naming their state, not numbering it.
    ///
    /// <para>Saving always worked — the request carries a string and the route
    /// parses it by hand — so the failure was one-directional and quiet: terms
    /// were filed correctly and then never appeared, because the panel filters on
    /// <c>state === 'Known'</c> and was handed a <c>0</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_filed_term_comes_back_naming_its_state()
    {
        await _client.PostAsJsonAsync(
            "/api/reader2/vocabulary",
            new SaveTermRequest("reification", "Known", "treating an abstraction as a thing", null));

        var body = await (await _client.GetAsync("/api/reader2/vocabulary")).Content.ReadAsStringAsync();

        body.Should().Contain("\"state\":\"Known\"")
            .And.NotMatchRegex(@"""state""\s*:\s*\d", "a number here matches no branch of the client's union");
    }
}
