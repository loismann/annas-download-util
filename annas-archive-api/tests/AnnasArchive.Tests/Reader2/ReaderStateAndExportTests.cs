using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Export;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// Reading position and appearance are per-person. Reader I kept all of this in
/// <c>localStorage</c>, so it was per-browser and could not tell two household
/// members apart on one machine.
/// </summary>
public class ReaderStateTests : IDisposable
{
    private readonly Reader2Fixture _f = new();
    private readonly ReaderStateStore _state;

    public ReaderStateTests() => _state = new ReaderStateStore(_f.Db);

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task A_reader_with_no_position_has_none_rather_than_a_wrong_one()
    {
        var book = await _f.EnrolAsync("fresh.epub", "contents");

        (await _state.GetPositionAsync(book, "paul")).Should().BeNull();
    }

    [Fact]
    public async Task A_position_round_trips_and_the_latest_write_wins()
    {
        var book = await _f.EnrolAsync("position.epub", "contents");

        await _state.SetPositionAsync(book, "paul", 3, 250);
        await _state.SetPositionAsync(book, "paul", 4, 10);

        var position = await _state.GetPositionAsync(book, "paul");
        position!.Chapter.Should().Be(4);
        position.WordOffset.Should().Be(10);
    }

    [Fact]
    public async Task Two_readers_in_one_book_do_not_collide()
    {
        var book = await _f.EnrolAsync("shared.epub", "contents");

        await _state.SetPositionAsync(book, "paul", 3, 250);
        await _state.SetPositionAsync(book, "someone-else", 11, 900);

        (await _state.GetPositionAsync(book, "paul"))!.Chapter.Should().Be(3);
        (await _state.GetPositionAsync(book, "someone-else"))!.Chapter.Should().Be(11);
    }

    [Fact]
    public async Task Un_enrolling_a_book_takes_its_positions_with_it()
    {
        var book = await _f.EnrolAsync("gone.epub", "contents");
        await _state.SetPositionAsync(book, "paul", 3, 250);

        await _f.Books.RemoveAsync(book);

        (await _state.GetPositionAsync(book, "paul")).Should().BeNull();
    }

    [Fact]
    public async Task A_reader_with_no_preferences_gets_the_defaults()
    {
        var preferences = await _state.GetPreferencesAsync("newcomer");

        preferences.Should().Be(new ReadingPreferences());
        preferences.FontFamily.Should().Be("serif");
    }

    [Fact]
    public async Task Preferences_round_trip_and_are_per_reader()
    {
        await _state.SetPreferencesAsync("paul", new ReadingPreferences("mono", 22, "dark", 0.75));
        await _state.SetPreferencesAsync("someone-else", new ReadingPreferences("sans", 14, "light", 0.5));

        (await _state.GetPreferencesAsync("paul")).Should()
            .Be(new ReadingPreferences("mono", 22, "dark", 0.75));
        (await _state.GetPreferencesAsync("someone-else")).FontSize.Should().Be(14);
    }

    /// <summary>Appearance belongs to a person, not to a book they were reading.</summary>
    [Fact]
    public async Task Preferences_survive_un_enrolling_every_book()
    {
        var book = await _f.EnrolAsync("temporary.epub", "contents");
        await _state.SetPreferencesAsync("paul", new ReadingPreferences("mono", 22, "dark", 0.75));

        await _f.Books.RemoveAsync(book);

        (await _state.GetPreferencesAsync("paul")).FontSize.Should().Be(22);
    }
}

public class ExportTests : IDisposable
{
    private readonly PipelineFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static readonly ChapterIndex Index = new("A Book", [
        new Chapter(0, "The Beginning", 0, 100, "c0.xhtml"),
        new Chapter(1, "The Middle", 0, 100, "c1.xhtml")
    ]);

    [Fact]
    public async Task An_export_carries_the_book_its_authors_and_its_book_type()
    {
        var ctx = await _f.WithChapterAsync("some text");

        var markdown = await ExportMarkdown.BuildAsync(ctx, Index, _f.Store.Artifacts, default);

        markdown.Should().Contain("# A Book").And.Contain("An Author").And.Contain("Ideas");
    }

    [Fact]
    public async Task An_export_includes_summaries_under_their_chapter_headings()
    {
        var ctx = await _f.WithChapterAsync("some text");
        await Put(ctx, ArtifactKind.ChapterSummary, 0, "the summary of chapter one");
        await Put(ctx, ArtifactKind.ExplainSimply, 0, "the plain version");

        var markdown = await ExportMarkdown.BuildAsync(ctx, Index, _f.Store.Artifacts, default);

        markdown.Should().Contain("## The Beginning")
            .And.Contain("the summary of chapter one")
            .And.Contain("### In plain language")
            .And.Contain("the plain version");
    }

    /// <summary>
    /// A book read twice has two sets of work. Interleaving them would produce a
    /// document that contradicts itself paragraph to paragraph.
    /// </summary>
    [Fact]
    public async Task An_export_carries_only_the_current_book_type_s_work()
    {
        var ctx = await _f.WithChapterAsync("some text");
        await Put(ctx, ArtifactKind.ChapterSummary, 0, "the literary reading");

        var other = PipelineFixture.Context(ctx.Book, new TestLens());
        await Put(other, ArtifactKind.ChapterSummary, 0, "the test-lens reading");

        var markdown = await ExportMarkdown.BuildAsync(ctx, Index, _f.Store.Artifacts, default);

        markdown.Should().Contain("the literary reading").And.NotContain("the test-lens reading");
    }

    [Fact]
    public async Task A_chapter_with_nothing_generated_is_left_out_rather_than_left_empty()
    {
        var ctx = await _f.WithChapterAsync("some text");
        await Put(ctx, ArtifactKind.ChapterSummary, 0, "only chapter one was summarised");

        var markdown = await ExportMarkdown.BuildAsync(ctx, Index, _f.Store.Artifacts, default);

        markdown.Should().Contain("## The Beginning").And.NotContain("## The Middle");
    }

    [Theory]
    [InlineData("War and Peace", "War-and-Peace")]
    [InlineData("A/B: Test?", "A-B--Test")]
    [InlineData("Война и миръ", "book")]
    [InlineData("   ", "book")]
    public void An_export_file_name_is_something_a_filesystem_will_accept(string title, string expected)
    {
        FileNames.Sanitize(title).Should().Be(expected);
    }

    private Task Put(
        AnnasArchive.API.Reader2.Domain.ReaderContext ctx, ArtifactKind kind, int chapter, string body) =>
        _f.Store.Artifacts.PutAsync(
            kind == ArtifactKind.ChapterSummary
                ? ArtifactKey.ChapterSummary(ctx.Ref, ctx.Lens.Key, chapter)
                : ArtifactKey.ExplainSimply(ctx.Ref, ctx.Lens.Key, chapter),
            new Prose(body),
            new ArtifactProvenance(Prose.SchemaVersion, ctx.Lens.Versions[CallKind.ChapterSummary], "test-model"));
}
