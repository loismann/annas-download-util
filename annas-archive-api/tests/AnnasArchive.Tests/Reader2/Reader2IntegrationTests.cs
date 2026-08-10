using System.Net;
using System.Net.Http.Json;
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
}
