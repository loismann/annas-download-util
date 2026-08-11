using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// A fourth book type that exists only in the test project.
///
/// <para><b>This file and one DI line are the entire cost of a new book type.</b>
/// That is the extensibility claim, and <c>LensContractTests</c> is what stops it
/// becoming aspirational: if making this lens work ever requires editing
/// production code, a schema, an endpoint, or the frontend, those tests fail.</para>
///
/// <para>It builds a story model deliberately — the literary lens does not, so
/// without this nothing would exercise the story-model branch of validation or
/// the vocabulary that goes with it.</para>
/// </summary>
public sealed class TestLens : IReaderLens
{
    public const string LensKey = "test-lens";

    public string Key => LensKey;
    public string DisplayName => "Test";
    public string Description => "A book type that exists only in the test project.";
    public string Icon => "science";

    /// <summary>Last in the picker, so it never becomes the default by accident.</summary>
    public int SortOrder => 9999;

    /// <summary>
    /// Settable, unlike a production lens's, so a test can do what a deploy does:
    /// move the version under an artifact that is already stored. It is the only
    /// way to exercise staleness over HTTP without shipping a second build.
    /// </summary>
    public static int Version { get; set; } = 1;

    public PromptVersions Versions => PromptVersions.All(Version);
    public bool BuildsStoryModel => true;
    public StoryVocabulary? StoryVocabulary => new("Subjects", "Sets", "Strands");

    public LensPrompts Prompts { get; } = new(
        PassageAnalysis: "test passage analysis prompt",
        ChunkSummary: "test chunk summary prompt",
        SectionSynthesis: "test section synthesis prompt",
        ChapterSummary: "test chapter summary prompt",
        SectionSummary: "test section summary prompt",
        ExplainSimply: "test explain simply prompt",
        StoryExtraction: "test story extraction prompt");
}

/// <summary>A lens whose properties a test can bend to trip one validation rule.</summary>
public sealed record BrokenLens : IReaderLens
{
    public string Key { get; init; } = "broken";
    public string DisplayName { get; init; } = "Broken";
    public string Description { get; init; } = "A deliberately invalid lens.";
    public string Icon { get; init; } = "bug_report";
    public int SortOrder { get; init; } = 500;
    public PromptVersions Versions { get; init; } = PromptVersions.All(1);
    public bool BuildsStoryModel { get; init; }
    public StoryVocabulary? StoryVocabulary { get; init; }

    public LensPrompts Prompts { get; init; } = new(
        PassageAnalysis: "p", ChunkSummary: "c", SectionSynthesis: "s",
        ChapterSummary: "ch", SectionSummary: "se", ExplainSimply: "e");
}
