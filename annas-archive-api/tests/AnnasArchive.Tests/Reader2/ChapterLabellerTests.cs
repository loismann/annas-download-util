using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// One of only two calls a reader does not click for, so its guards matter more
/// than most: it must be switchable off, must cost nothing on a second open, and
/// must never fail an ingest over cosmetics.
/// </summary>
public class ChapterLabellerTests : IDisposable
{
    private readonly PipelineFixture _f;
    private readonly ChapterLabeller _labeller;

    public ChapterLabellerTests() : this(null) { }

    private ChapterLabellerTests(Dictionary<string, string?>? settings)
    {
        _f = new PipelineFixture(settings);
        _labeller = new ChapterLabeller(_f.Gateway, _f.Store.Text, _f.Model, _f.Options);
    }

    public void Dispose() => _f.Dispose();

    private static ChapterIndex Index(int chapters) => new("A Book",
        Enumerable.Range(0, chapters)
            .Select(i => new Chapter(i, $"Section{i:D4}.xhtml", 0, 100, $"c{i}.xhtml"))
            .ToArray());

    [Fact]
    public async Task Titles_come_back_tidied_and_in_order()
    {
        var ctx = await _f.WithChapterAsync("It was a bright cold day in April.");
        _f.Ai.Answer = _ => "1. The Beginning\n2. The Middle\n3. The End";

        var labelled = await _labeller.ApplyAsync(ctx, Index(3));

        labelled.Chapters.Select(c => c.Title)
            .Should().Equal("The Beginning", "The Middle", "The End");
    }

    [Fact]
    public async Task The_model_is_shown_each_chapter_s_existing_title_and_opening()
    {
        var ctx = await _f.WithChapterAsync("It was a bright cold day in April.");
        _f.Ai.Answer = _ => "1. Only One";

        await _labeller.ApplyAsync(ctx, Index(1));

        _f.Ai.Calls[0].UserPrompt.Should()
            .Contain("[Section0000.xhtml]").And.Contain("bright cold day");
    }

    /// <summary>
    /// A dropped line would shift every title after it by one — worse than the raw
    /// headings and much harder to notice than no change at all.
    /// </summary>
    [Fact]
    public async Task A_wrong_number_of_titles_is_rejected_whole()
    {
        var ctx = await _f.WithChapterAsync("some text");
        _f.Ai.Answer = _ => "1. The Beginning\n2. The Middle";

        var labelled = await _labeller.ApplyAsync(ctx, Index(3));

        labelled.Chapters.Select(c => c.Title).Should().Equal(Index(3).Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task A_failed_call_keeps_the_book_s_own_titles_rather_than_failing_the_ingest()
    {
        var ctx = await _f.WithChapterAsync("some text");
        _f.Ai.FailOnCall = 1;

        var labelled = await _labeller.ApplyAsync(ctx, Index(2));

        labelled.Should().BeEquivalentTo(Index(2));
    }

    [Fact]
    public async Task A_second_ingest_reuses_the_stored_labels_and_bills_nothing()
    {
        var ctx = await _f.WithChapterAsync("some text");
        _f.Ai.Answer = _ => "1. First\n2. Second";

        await _labeller.ApplyAsync(ctx, Index(2));
        var labelled = await _labeller.ApplyAsync(ctx, Index(2));

        _f.Ai.Calls.Should().HaveCount(1);
        labelled.Chapters[0].Title.Should().Be("First");
    }

    /// <summary>Opening a book must never cost money.</summary>
    [Fact]
    public async Task Reading_stored_labels_never_calls_a_model()
    {
        var ctx = await _f.WithChapterAsync("some text");

        var labelled = await _labeller.StoredLabelsAsync(ctx, Index(2));

        _f.Ai.Calls.Should().BeEmpty();
        labelled.Should().BeEquivalentTo(Index(2), "there is nothing stored to apply");
    }

    [Fact]
    public async Task Stored_labels_are_applied_on_a_later_open()
    {
        var ctx = await _f.WithChapterAsync("some text");
        _f.Ai.Answer = _ => "1. First\n2. Second";
        await _labeller.ApplyAsync(ctx, Index(2));

        var opened = await _labeller.StoredLabelsAsync(ctx, Index(2));

        opened.Chapters.Select(c => c.Title).Should().Equal("First", "Second");
        _f.Ai.Calls.Should().HaveCount(1, "the open itself added nothing");
    }

    [Fact]
    public async Task Turning_labelling_off_makes_it_free()
    {
        using var off = new ChapterLabellerTests(
            new Dictionary<string, string?> { ["Reader2:ChapterLabels:Enabled"] = "false" });

        var ctx = await off._f.WithChapterAsync("some text");

        var labelled = await off._labeller.ApplyAsync(ctx, Index(2));

        off._f.Ai.Calls.Should().BeEmpty();
        labelled.Should().BeEquivalentTo(Index(2));
    }

    /// <summary>Titles describe the book, so switching book type must not redo them.</summary>
    [Fact]
    public async Task Labels_survive_a_lens_switch()
    {
        var ctx = await _f.WithChapterAsync("some text");
        _f.Ai.Answer = _ => "1. First\n2. Second";
        await _labeller.ApplyAsync(ctx, Index(2));

        var switched = PipelineFixture.Context(ctx.Book, new TestLens());

        (await _labeller.StoredLabelsAsync(switched, Index(2)))
            .Chapters[0].Title.Should().Be("First");
        _f.Ai.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Labelling_uses_the_fast_model_because_it_is_tidying_strings()
    {
        var ctx = await _f.WithChapterAsync("some text");
        _f.Ai.Answer = _ => "1. First";

        await _labeller.ApplyAsync(ctx, Index(1));

        _f.Ai.Calls[0].Model.Should().Be("fast-model");
    }
}
