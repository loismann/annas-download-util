using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.Tests.Reader2;

public sealed class ArtifactStoreTests : IDisposable
{
    private readonly Reader2Fixture _f = new();
    private static readonly ArtifactVersions V1 = new(Schema: 1, Prompt: 1);
    private const string Lens = "fiction";

    public void Dispose() => _f.Dispose();

    private Task<BookRef> BookAsync() =>
        _f.EnrolAsync("war-and-peace.epub", "tolstoy bytes", Lens);

    [Fact]
    public async Task An_artifact_round_trips_with_its_provenance()
    {
        var book = await BookAsync();
        var key = ArtifactKey.ChapterSummary(book, Lens, 3);

        await _f.Artifacts.PutAsync(key, new TestPayload("a summary", 42),
            new ArtifactProvenance(1, 1, "gpt-5.2", PromptTokens: 100, CompletionTokens: 250));

        var stored = await _f.Artifacts.GetAsync<TestPayload>(key, V1);

        stored.Should().NotBeNull();
        stored!.Content.Should().Be(new TestPayload("a summary", 42));
        stored.Provenance.Model.Should().Be("gpt-5.2");
        stored.Provenance.PromptTokens.Should().Be(100);
        stored.Provenance.CompletionTokens.Should().Be(250);
    }

    [Fact]
    public async Task Every_kind_of_key_round_trips()
    {
        var book = await BookAsync();
        ArtifactKey[] keys =
        [
            ArtifactKey.ChapterIndex(book),
            ArtifactKey.ChapterLabels(book),
            ArtifactKey.Flashcards(book),
            ArtifactKey.ChunkBoundaries(book, 2),
            ArtifactKey.ChapterSummary(book, Lens, 2),
            ArtifactKey.ExplainSimply(book, Lens, 2),
            ArtifactKey.SectionSummary(book, Lens, 2, 1),
            ArtifactKey.SectionVocab(book, Lens, 2, 1),
            ArtifactKey.PassageAnalysis(book, Lens, 2, 900),
            ArtifactKey.StoryModel(book, Lens),
            ArtifactKey.LearnMore(book, Lens, "reification")
        ];

        foreach (var key in keys)
            await _f.Artifacts.PutAsync(key, new TestPayload(key.Kind.Wire()), new ArtifactProvenance(1, 1, "m"));

        foreach (var key in keys)
        {
            var stored = await _f.Artifacts.GetAsync<TestPayload>(key, V1);
            stored.Should().NotBeNull($"{key.Kind.Wire()} should round-trip");
            stored!.Content.Text.Should().Be(key.Kind.Wire());
        }
    }

    [Fact]
    public async Task A_missing_artifact_is_null_not_an_error()
    {
        var book = await BookAsync();
        (await _f.Artifacts.GetAsync<TestPayload>(ArtifactKey.ChapterSummary(book, Lens, 99), V1))
            .Should().BeNull();
    }

    [Fact]
    public async Task Writing_the_same_key_twice_replaces_rather_than_duplicates()
    {
        var book = await BookAsync();
        var key = ArtifactKey.StoryModel(book, Lens);

        await _f.Artifacts.PutAsync(key, new TestPayload("first"), new ArtifactProvenance(1, 1, "m"));
        await _f.Artifacts.PutAsync(key, new TestPayload("second"), new ArtifactProvenance(1, 1, "m"));

        (await _f.Artifacts.GetAsync<TestPayload>(key, V1))!.Content.Text.Should().Be("second");
        (await _f.Artifacts.ListAsync<TestPayload>(
            new ArtifactQuery(book, Lens, ArtifactKind.StoryModel), V1)).Should().HaveCount(1);
    }

    // ─── version gates ───────────────────────────────────────────────────

    [Fact]
    public async Task A_stale_prompt_version_is_a_miss_but_the_row_survives_for_overwrite()
    {
        var book = await BookAsync();
        var key = ArtifactKey.ChapterSummary(book, Lens, 1);
        await _f.Artifacts.PutAsync(key, new TestPayload("old prompt"), new ArtifactProvenance(1, 1, "m"));

        (await _f.Artifacts.GetAsync<TestPayload>(key, new ArtifactVersions(1, 2))).Should().BeNull();

        // Still readable by the build that wrote it — a prompt bump is not a delete.
        (await _f.Artifacts.GetAsync<TestPayload>(key, V1)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_stale_schema_version_is_a_miss_and_the_row_is_deleted()
    {
        var book = await BookAsync();
        var key = ArtifactKey.SectionSummary(book, Lens, 1, 0);
        await _f.Artifacts.PutAsync(key, new TestPayload("old shape"), new ArtifactProvenance(1, 1, "m"));

        (await _f.Artifacts.GetAsync<TestPayload>(key, new ArtifactVersions(2, 1))).Should().BeNull();

        // Gone, not merely hidden — an unreadable row would fail every future read.
        (await _f.Artifacts.GetAsync<TestPayload>(key, V1)).Should().BeNull();
    }

    [Fact]
    public async Task An_artifact_from_a_newer_build_is_served_and_never_deleted()
    {
        var book = await BookAsync();
        var key = ArtifactKey.ChapterSummary(book, Lens, 5);
        await _f.Artifacts.PutAsync(key, new TestPayload("from the future"), new ArtifactProvenance(9, 9, "m"));

        var stored = await _f.Artifacts.GetAsync<TestPayload>(key, V1);

        stored.Should().NotBeNull("a rollback must not destroy work a newer build produced");
        stored!.Content.Text.Should().Be("from the future");
    }

    [Fact]
    public async Task A_write_from_an_older_schema_will_not_clobber_a_newer_row()
    {
        var book = await BookAsync();
        var key = ArtifactKey.ChapterSummary(book, Lens, 5);
        await _f.Artifacts.PutAsync(key, new TestPayload("newer"), new ArtifactProvenance(5, 1, "m"));

        await _f.Artifacts.PutAsync(key, new TestPayload("older"), new ArtifactProvenance(1, 1, "m"));

        var stored = await _f.Artifacts.GetAsync<TestPayload>(key, new ArtifactVersions(5, 1));
        stored!.Content.Text.Should().Be("newer");
    }

    // ─── listing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_returns_only_the_requested_lens_and_kind_in_order()
    {
        var book = await BookAsync();
        for (var section = 2; section >= 0; section--)
            await _f.Artifacts.PutAsync(ArtifactKey.SectionSummary(book, Lens, 1, section),
                new TestPayload($"s{section}"), new ArtifactProvenance(1, 1, "m"));

        await _f.Artifacts.PutAsync(ArtifactKey.SectionSummary(book, "military", 1, 0),
            new TestPayload("other lens"), new ArtifactProvenance(1, 1, "m"));
        await _f.Artifacts.PutAsync(ArtifactKey.SectionVocab(book, Lens, 1, 0),
            new TestPayload("other kind"), new ArtifactProvenance(1, 1, "m"));

        var listed = await _f.Artifacts.ListAsync<TestPayload>(
            new ArtifactQuery(book, Lens, ArtifactKind.SectionSummary, Chapter: 1), V1);

        listed.Select(s => s.Content.Text).Should().Equal("s0", "s1", "s2");
    }

    [Fact]
    public async Task List_skips_stale_rows_without_deleting_them()
    {
        var book = await BookAsync();
        await _f.Artifacts.PutAsync(ArtifactKey.SectionSummary(book, Lens, 1, 0),
            new TestPayload("current"), new ArtifactProvenance(1, 2, "m"));
        await _f.Artifacts.PutAsync(ArtifactKey.SectionSummary(book, Lens, 1, 1),
            new TestPayload("stale"), new ArtifactProvenance(1, 1, "m"));

        var listed = await _f.Artifacts.ListAsync<TestPayload>(
            new ArtifactQuery(book, Lens, ArtifactKind.SectionSummary), new ArtifactVersions(1, 2));

        listed.Select(s => s.Content.Text).Should().Equal("current");
        (await _f.Artifacts.GetAsync<TestPayload>(ArtifactKey.SectionSummary(book, Lens, 1, 1), V1))
            .Should().NotBeNull("a read-many should not silently delete");
    }

    // ─── deletion ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteStale_removes_only_rows_below_the_given_prompt_version()
    {
        var book = await BookAsync();
        await _f.Artifacts.PutAsync(ArtifactKey.ChapterSummary(book, Lens, 1),
            new TestPayload("v1"), new ArtifactProvenance(1, 1, "m"));
        await _f.Artifacts.PutAsync(ArtifactKey.ChapterSummary(book, Lens, 2),
            new TestPayload("v3"), new ArtifactProvenance(1, 3, "m"));

        var removed = await _f.Artifacts.DeleteStaleAsync(book, Lens, belowPromptVersion: 3);

        removed.Should().Be(1);
        (await _f.Artifacts.GetAsync<TestPayload>(
            ArtifactKey.ChapterSummary(book, Lens, 2), new ArtifactVersions(1, 3))).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteStale_leaves_the_other_lens_alone()
    {
        var book = await BookAsync();
        await _f.Artifacts.PutAsync(ArtifactKey.ChapterSummary(book, "military", 1),
            new TestPayload("military v1"), new ArtifactProvenance(1, 1, "m"));

        await _f.Artifacts.DeleteStaleAsync(book, Lens, belowPromptVersion: 99);

        (await _f.Artifacts.GetAsync<TestPayload>(
            ArtifactKey.ChapterSummary(book, "military", 1), V1)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteForBook_removes_every_artifact_of_that_book_only()
    {
        var mine = await BookAsync();
        var other = await _f.EnrolAsync("other.epub", "different bytes", Lens);

        await _f.Artifacts.PutAsync(ArtifactKey.ChapterSummary(mine, Lens, 1),
            new TestPayload("mine"), new ArtifactProvenance(1, 1, "m"));
        await _f.Artifacts.PutAsync(ArtifactKey.ChapterSummary(other, Lens, 1),
            new TestPayload("theirs"), new ArtifactProvenance(1, 1, "m"));

        (await _f.Artifacts.DeleteForBookAsync(mine)).Should().Be(1);

        (await _f.Artifacts.GetAsync<TestPayload>(ArtifactKey.ChapterSummary(other, Lens, 1), V1))
            .Should().NotBeNull();
    }

    /// <summary>
    /// Artifacts carry no user id on purpose: they describe the book, not the
    /// reader. One person paying to summarise a chapter should benefit everyone.
    /// </summary>
    [Fact]
    public async Task Artifacts_are_shared_across_the_household()
    {
        var book = await BookAsync();
        var key = ArtifactKey.ChapterSummary(book, Lens, 1);

        await _f.Artifacts.PutAsync(key, new TestPayload("paul generated this"),
            new ArtifactProvenance(1, 1, "m"));

        // A different reader, same store, no user dimension anywhere in the key.
        var stored = await _f.Artifacts.GetAsync<TestPayload>(key, V1);
        stored!.Content.Text.Should().Be("paul generated this");
    }

    /// <summary>The concurrency the keyed lock exists to make unnecessary — but
    /// the UNIQUE constraint has to hold even without it.</summary>
    [Fact]
    public async Task Concurrent_writes_to_one_key_leave_exactly_one_row()
    {
        var book = await BookAsync();
        var key = ArtifactKey.StoryModel(book, Lens);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            _f.Artifacts.PutAsync(key, new TestPayload($"writer {i}"), new ArtifactProvenance(1, 1, "m"))));

        (await _f.Artifacts.ListAsync<TestPayload>(
            new ArtifactQuery(book, Lens, ArtifactKind.StoryModel), V1)).Should().HaveCount(1);
    }
}
