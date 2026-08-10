using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;
using AnnasArchive.API.Reader2.Vocabulary;

namespace AnnasArchive.Tests.Reader2;

public class TermNormTests
{
    /// <summary>
    /// The rule the whole feature keys on. A reader who has learned <i>naïveté</i>
    /// has learned <i>naivete</i>.
    /// </summary>
    [Theory]
    [InlineData("naïveté", "naivete")]
    [InlineData("Naïveté", "naivete")]
    [InlineData("NAIVETE", "naivete")]
    [InlineData("  naivete  ", "naivete")]
    [InlineData("Dasein", "dasein")]
    [InlineData("l'être", "l'etre")]
    [InlineData("self-consciousness", "self-consciousness")]
    public void Casefolding_and_diacritics_collapse_to_one_key(string input, string expected)
    {
        TermNorm.Of(input).Should().Be(expected);
    }

    /// <summary>A text selection routinely drags in punctuation at its edges.</summary>
    [Theory]
    [InlineData("“reification”", "reification")]
    [InlineData("reification,", "reification")]
    [InlineData("(reification)", "reification")]
    [InlineData("reification.", "reification")]
    [InlineData("— reification …", "reification")]
    public void Edge_punctuation_is_trimmed_but_inner_punctuation_is_not(string input, string expected)
    {
        TermNorm.Of(input).Should().Be(expected);
    }

    [Fact]
    public void Inner_whitespace_collapses_so_a_line_break_does_not_make_a_new_term()
    {
        TermNorm.Of("categorical\n  imperative").Should().Be("categorical imperative");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void Nothing_normalises_to_nothing_rather_than_throwing(string? input)
    {
        TermNorm.Of(input).Should().BeEmpty();
    }

    [Fact]
    public void Two_spellings_of_one_word_are_the_same_term()
    {
        TermNorm.Same("Naïveté", "naivete").Should().BeTrue();
        TermNorm.Same("reification", "reify").Should().BeFalse();
        TermNorm.Same("", "").Should().BeFalse("nothing is not a term");
    }
}

public class VocabularyStoreTests : IDisposable
{
    private readonly Reader2Fixture _f = new();
    private readonly VocabularyStore _vocabulary;

    public VocabularyStoreTests() => _vocabulary = new VocabularyStore(_f.Db);

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task A_term_round_trips_with_the_spelling_the_reader_saw()
    {
        await _vocabulary.SaveAsync("paul", "Naïveté", TermState.Studying, "a lack of experience");

        var terms = await _vocabulary.ListAsync("paul");

        terms.Should().ContainSingle();
        terms[0].Term.Should().Be("Naïveté", "the reader should see the word as it was written");
        terms[0].TermNorm.Should().Be("naivete");
        terms[0].Definition.Should().Be("a lack of experience");
    }

    [Fact]
    public async Task Two_spellings_of_one_word_are_one_row()
    {
        await _vocabulary.SaveAsync("paul", "naïveté", TermState.Studying);
        await _vocabulary.SaveAsync("paul", "Naivete", TermState.Studying);

        (await _vocabulary.ListAsync("paul")).Should().ContainSingle();
    }

    [Fact]
    public async Task Moving_a_term_between_states_is_the_same_operation_as_adding_it()
    {
        await _vocabulary.SaveAsync("paul", "reification", TermState.Studying, "a definition");
        await _vocabulary.SaveAsync("paul", "reification", TermState.Known);

        var terms = await _vocabulary.ListAsync("paul");

        terms.Should().ContainSingle();
        terms[0].State.Should().Be(TermState.Known);
        terms[0].Definition.Should().Be("a definition", "moving a term must not erase what it means");
    }

    [Fact]
    public async Task Listing_can_be_narrowed_to_one_state()
    {
        await _vocabulary.SaveAsync("paul", "known-word", TermState.Known);
        await _vocabulary.SaveAsync("paul", "studying-word", TermState.Studying);

        (await _vocabulary.ListAsync("paul", TermState.Known)).Should().ContainSingle();
        (await _vocabulary.ListAsync("paul")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Vocabulary_is_per_reader()
    {
        await _vocabulary.SaveAsync("paul", "reification", TermState.Known);

        (await _vocabulary.ListAsync("someone-else")).Should().BeEmpty();
    }

    /// <summary>
    /// The exclusion list is known terms only. A reader still studying a word
    /// wants to keep seeing it defined.
    /// </summary>
    [Fact]
    public async Task The_exclusion_list_is_known_terms_and_not_studying_ones()
    {
        await _vocabulary.SaveAsync("paul", "known-word", TermState.Known);
        await _vocabulary.SaveAsync("paul", "studying-word", TermState.Studying);

        (await _vocabulary.KnownAsync("paul")).Should().BeEquivalentTo(["known-word"]);
        (await _vocabulary.FiledAsync("paul")).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(TermState.Known, 1)]
    [InlineData(TermState.Studying, 1)]
    public async Task Clearing_can_be_scoped_to_one_state(TermState? scope, int remaining)
    {
        await _vocabulary.SaveAsync("paul", "known-word", TermState.Known);
        await _vocabulary.SaveAsync("paul", "studying-word", TermState.Studying);

        await _vocabulary.ClearAsync("paul", scope);

        (await _vocabulary.ListAsync("paul")).Should().HaveCount(remaining);
    }

    /// <summary>
    /// The rule the schema exists to enforce: a word does not become unknown
    /// again because you finished the book you met it in.
    /// </summary>
    [Fact]
    public async Task A_known_term_survives_un_enrolling_the_book_it_was_met_in()
    {
        var book = await _f.EnrolAsync("met-here.epub", "contents");
        await _vocabulary.SaveAsync("paul", "reification", TermState.Known, firstSeenIn: book);

        await _f.Books.RemoveAsync(book);

        (await _vocabulary.ListAsync("paul")).Should().ContainSingle();
    }

    [Fact]
    public async Task Forgetting_a_book_drops_its_provenance_and_keeps_the_words()
    {
        var book = await _f.EnrolAsync("forget.epub", "contents");
        await _vocabulary.SaveAsync("paul", "reification", TermState.Known, firstSeenIn: book);

        (await _vocabulary.ForgetBookAsync("paul", book)).Should().Be(1);

        var terms = await _vocabulary.ListAsync("paul");
        terms.Should().ContainSingle();
        terms[0].FirstSeenBookId.Should().BeNull();
    }

    [Fact]
    public async Task A_term_that_is_only_punctuation_is_refused()
    {
        var act = () => _vocabulary.SaveAsync("paul", " ... ", TermState.Known);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

public class VocabularyPipelineTests : IDisposable
{
    private readonly PipelineFixture _f = new();
    private readonly VocabularyStore _terms;
    private readonly VocabularyPipeline _vocabulary;

    public VocabularyPipelineTests()
    {
        _terms = new VocabularyStore(_f.Store.Db);
        _vocabulary = new VocabularyPipeline(
            _f.Gateway, _f.Pipeline, _f.Store.Text, _terms, _f.Model);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Definitions_come_back_parsed_from_one_line_per_term()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(4, 100));
        _f.Ai.Answer = _ => "reification — treating an abstraction as a thing\nDasein — being-there";

        var vocab = await _vocabulary.ForSectionAsync(ctx, 0, 0);

        vocab.Terms.Should().HaveCount(2);
        vocab.Terms[0].Term.Should().Be("reification");
        vocab.Terms[0].Meaning.Should().Be("treating an abstraction as a thing");
    }

    /// <summary>A blank entry looks like a defect; a shorter list looks like nothing.</summary>
    [Fact]
    public async Task A_line_that_does_not_parse_is_dropped_rather_than_kept_empty()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(4, 100));
        _f.Ai.Answer = _ => "Here are the terms:\nreification — an abstraction made thing\n\nthat's all";

        (await _vocabulary.ForSectionAsync(ctx, 0, 0)).Terms
            .Should().ContainSingle().Which.Term.Should().Be("reification");
    }

    [Theory]
    [InlineData(" — ")]
    [InlineData(" – ")]
    [InlineData(" - ")]
    public async Task Any_of_the_three_dashes_a_model_reaches_for_is_accepted(string dash)
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(4, 100));
        _f.Ai.Answer = _ => $"reification{dash}an abstraction made thing";

        (await _vocabulary.ForSectionAsync(ctx, 0, 0)).Terms.Should().ContainSingle();
    }

    /// <summary>The product rule: definitions are personal, not exhaustive.</summary>
    [Fact]
    public async Task Known_terms_are_named_in_the_input_so_the_model_skips_them()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(4, 100));
        await _terms.SaveAsync(ctx.UserId, "reification", TermState.Known);

        await _vocabulary.ForSectionAsync(ctx, 0, 0);

        _f.Ai.Calls[0].UserPrompt.Should().Contain("Already known").And.Contain("reification");
    }

    /// <summary>
    /// The artifact is the household's; the exclusion list is one reader's. So
    /// filtering happens on read, never before storage.
    /// </summary>
    [Fact]
    public async Task A_term_filed_after_generation_is_filtered_out_on_the_next_read()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(4, 100));
        _f.Ai.Answer = _ => "reification — an abstraction made thing\nDasein — being-there";

        (await _vocabulary.ForSectionAsync(ctx, 0, 0)).Terms.Should().HaveCount(2);

        await _terms.SaveAsync(ctx.UserId, "Reification", TermState.Known);

        var second = await _vocabulary.ForSectionAsync(ctx, 0, 0);
        second.Terms.Should().ContainSingle().Which.Term.Should().Be("Dasein");
        _f.Ai.Calls.Should().HaveCount(1, "filtering is not regenerating");
    }

    [Fact]
    public async Task The_book_type_is_named_in_the_input_so_the_right_words_are_picked()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(4, 100));

        await _vocabulary.ForSectionAsync(ctx, 0, 0);

        _f.Ai.Calls[0].UserPrompt.Should().Contain("Reading this book as").And.Contain("Ideas");
    }

    [Fact]
    public async Task A_chapter_s_vocabulary_is_its_sections_deduplicated()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(40, 500));
        _f.Ai.Answer = _ => "reification — the same term in every section";

        var layout = await _f.Pipeline.LayoutAsync(ctx, 0);
        var vocab = await _vocabulary.ForChapterAsync(ctx, 0);

        layout.Sections.Should().HaveCountGreaterThan(1);
        _f.Ai.Calls.Should().HaveCount(layout.Sections.Count);
        vocab.Terms.Should().ContainSingle("the same word twice is one entry");
    }

    /// <summary>Reader I re-bills for every ask. This is the whole reason it is an artifact.</summary>
    [Fact]
    public async Task The_second_deep_dive_on_a_term_is_free()
    {
        var ctx = await _f.WithChapterAsync("some text");
        _f.Ai.Answer = _ => "<p>a scholarly deep dive</p>";

        var first = await _vocabulary.DeepDiveAsync(ctx, "reification");
        var second = await _vocabulary.DeepDiveAsync(ctx, "Reification");

        second.Should().Be(first, "casing must not miss the cache");
        _f.Ai.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_deep_dive_is_stored_per_book_type()
    {
        var ctx = await _f.WithChapterAsync("some text");
        _f.Ai.Answer = _ => "the literary reading of the term";
        await _vocabulary.DeepDiveAsync(ctx, "reification");

        var switched = PipelineFixture.Context(ctx.Book, new TestLens());
        _f.Ai.Answer = _ => "the test-lens reading of the term";

        (await _vocabulary.DeepDiveAsync(switched, "reification")).Html
            .Should().Be("the test-lens reading of the term");
        (await _vocabulary.DeepDiveAsync(ctx, "reification")).Html
            .Should().Be("the literary reading of the term");
    }

    [Fact]
    public async Task A_deep_dive_on_nothing_is_refused_before_a_model_is_called()
    {
        var ctx = await _f.WithChapterAsync("some text");

        var act = () => _vocabulary.DeepDiveAsync(ctx, "  ...  ");

        await act.Should().ThrowAsync<ReaderAiException>();
        _f.Ai.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deep_dive_uses_the_deep_model_because_that_is_the_whole_point()
    {
        var ctx = await _f.WithChapterAsync("some text");

        await _vocabulary.DeepDiveAsync(ctx, "reification");

        _f.Ai.Calls[0].Model.Should().Be("deep-model");
    }
}

public class FlashcardTests : IDisposable
{
    private readonly Reader2Fixture _f = new();
    private readonly FlashcardStore _cards;

    public FlashcardTests() => _cards = new FlashcardStore(_f.Artifacts);

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Cards_round_trip_and_start_empty()
    {
        var book = await _f.EnrolAsync("cards.epub", "contents");

        (await _cards.ListAsync(book)).Cards.Should().BeEmpty();

        await _cards.AddAsync(book, "reification", "an abstraction made thing");

        (await _cards.ListAsync(book)).Cards.Should().ContainSingle()
            .Which.Definition.Should().Be("an abstraction made thing");
    }

    [Fact]
    public async Task Saving_a_term_twice_replaces_it_rather_than_duplicating_it()
    {
        var book = await _f.EnrolAsync("cards.epub", "contents");

        await _cards.AddAsync(book, "reification", "first definition");
        await _cards.AddAsync(book, "Reification", "second definition");

        var cards = (await _cards.ListAsync(book)).Cards;
        cards.Should().ContainSingle();
        cards[0].Definition.Should().Be("second definition");
    }

    [Fact]
    public async Task A_card_can_be_removed_by_any_spelling_of_its_term()
    {
        var book = await _f.EnrolAsync("cards.epub", "contents");
        await _cards.AddAsync(book, "naïveté", "a lack of experience");

        await _cards.RemoveAsync(book, "NAIVETE");

        (await _cards.ListAsync(book)).Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Clearing_removes_every_card_for_that_book_only()
    {
        var first = await _f.EnrolAsync("one.epub", "contents one");
        var second = await _f.EnrolAsync("two.epub", "contents two");
        await _cards.AddAsync(first, "a", "one");
        await _cards.AddAsync(second, "b", "two");

        await _cards.ClearAsync(first);

        (await _cards.ListAsync(first)).Cards.Should().BeEmpty();
        (await _cards.ListAsync(second)).Cards.Should().ContainSingle();
    }

    /// <summary>A term worth remembering is worth remembering either way round.</summary>
    [Fact]
    public async Task Cards_survive_a_lens_switch()
    {
        var book = await _f.EnrolAsync("cards.epub", "contents");
        await _cards.AddAsync(book, "reification", "an abstraction made thing");

        await _f.Books.SetLensAsync(book, TestLens.LensKey);

        (await _cards.ListAsync(book)).Cards.Should().ContainSingle();
    }

    [Fact]
    public async Task A_card_with_no_term_is_refused()
    {
        var book = await _f.EnrolAsync("cards.epub", "contents");

        var act = () => _cards.AddAsync(book, "   ", "a definition");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
