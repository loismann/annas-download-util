using AnnasArchive.API.Reader2.Endpoints;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// The extensibility contract, checked rather than asserted in prose.
///
/// <para><see cref="TestLens"/> exists only in this project and is registered
/// through DI exactly as a production lens would be. If any of these need a
/// change to production code, a migration, or an endpoint, then "a fourth book
/// type is one class and one DI line" was never true and this is where that
/// shows up.</para>
/// </summary>
public class LensContractTests : IDisposable
{
    private readonly Reader2Fixture _f = new();

    public void Dispose() => _f.Dispose();

    /// <summary>
    /// The registry as the application composes it, plus the one extra line a new
    /// book type costs. Nothing else about the composition changes.
    /// </summary>
    private static ILensRegistry RegistryWithTestLens()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IReaderLens, LiteraryLens>();
        services.AddSingleton<IReaderLens, TestLens>();     // ← the whole cost
        services.AddSingleton<ILensRegistry, LensRegistry>();

        return services.BuildServiceProvider().GetRequiredService<ILensRegistry>();
    }

    /// <summary>1. It appears in the payload that drives the picker.</summary>
    [Fact]
    public void A_test_only_lens_reaches_the_client_through_the_lenses_payload()
    {
        var registry = RegistryWithTestLens();

        var served = registry.All
            .Select(l => LensResponse.From(l, isDefault: l.Key == registry.Default.Key))
            .ToArray();

        served.Should().Contain(l => l.Key == TestLens.LensKey);
        served.Single(l => l.Key == TestLens.LensKey).DisplayName.Should().Be("Test");
    }

    /// <summary>It does not become the default merely by existing.</summary>
    [Fact]
    public void Adding_a_lens_does_not_change_which_one_is_default()
    {
        RegistryWithTestLens().Default.Key.Should().Be("literary");
    }

    /// <summary>2. It is selectable — the resolution a PATCH performs.</summary>
    [Fact]
    public void A_test_only_lens_is_selectable_by_key()
    {
        RegistryWithTestLens().ForRequest(TestLens.LensKey)!.Key.Should().Be(TestLens.LensKey);
    }

    [Fact]
    public async Task A_book_can_be_switched_to_a_test_only_lens_and_back()
    {
        var book = await _f.EnrolAsync("switch.epub", "contents");

        (await _f.Books.SetLensAsync(book, TestLens.LensKey)).Should().BeTrue();
        (await _f.Books.GetAsync(book))!.LensKey.Should().Be(TestLens.LensKey);

        await _f.Books.SetLensAsync(book, "literary");
        (await _f.Books.GetAsync(book))!.LensKey.Should().Be("literary");
    }

    /// <summary>3. Its artifacts store under its own key, with no schema change.</summary>
    [Fact]
    public async Task A_test_only_lens_stores_artifacts_under_its_own_lens_key()
    {
        var book = await _f.EnrolAsync("artifacts.epub", "contents", TestLens.LensKey);
        var key = ArtifactKey.ChapterSummary(book, TestLens.LensKey, chapter: 3);

        await _f.Artifacts.PutAsync(key, new TestPayload("through the test lens"), Provenance);

        var read = await _f.Artifacts.GetAsync<TestPayload>(key, Versions);
        read!.Content.Text.Should().Be("through the test lens");
    }

    /// <summary>
    /// 4. Two lenses' output for the same chapter coexist.
    ///
    /// <para>This is the one that would fail if <c>lens_key</c> were not part of
    /// the artifact's identity — and it is why switching a book's type does not
    /// throw away work somebody already paid for.</para>
    /// </summary>
    [Fact]
    public async Task Two_lenses_artifacts_for_one_chapter_coexist()
    {
        var book = await _f.EnrolAsync("coexist.epub", "contents");

        var literary = ArtifactKey.ChapterSummary(book, "literary", chapter: 1);
        var test = ArtifactKey.ChapterSummary(book, TestLens.LensKey, chapter: 1);

        await _f.Artifacts.PutAsync(literary, new TestPayload("ideas reading"), Provenance);
        await _f.Artifacts.PutAsync(test, new TestPayload("test reading"), Provenance);

        (await _f.Artifacts.GetAsync<TestPayload>(literary, Versions))!.Content.Text
            .Should().Be("ideas reading");
        (await _f.Artifacts.GetAsync<TestPayload>(test, Versions))!.Content.Text
            .Should().Be("test reading");
    }

    /// <summary>
    /// A lens that builds a story model needs no extra registration to do so —
    /// the vocabulary rides along on the same interface.
    /// </summary>
    [Fact]
    public void A_story_building_lens_carries_its_own_vocabulary()
    {
        var lens = RegistryWithTestLens().Get(TestLens.LensKey);

        lens.BuildsStoryModel.Should().BeTrue();
        lens.StoryVocabulary!.Actors.Should().Be("Subjects");
    }

    private static ArtifactProvenance Provenance =>
        new(SchemaVersion: 1, PromptVersion: 1, Model: "test-model");

    private static ArtifactVersions Versions => new(Schema: 1, Prompt: 1);
}
