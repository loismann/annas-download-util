using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Lenses;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// Configuration must be wrong loudly or right silently, never wrong quietly.
///
/// <para>The whole type exists because of one Reader I defect:
/// <c>AI:MaxCompletionTokens:LearnMore</c> sat configured at 2,000 while the code
/// read a different key and capped the feature at 1,200 for months. A wrong
/// budget produces shorter output, not an error, so nothing noticed.</para>
/// </summary>
public class Reader2OptionsTests
{
    private static Reader2Options Load(params (string Key, string Value)[] settings) =>
        Reader2Options.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    [Fact]
    public void An_empty_configuration_produces_the_tuned_defaults_and_validates()
    {
        var options = Load();

        options.Validate();
        options.ChunkSize.Should().Be(2000);
        options.ChunksPerSection.Should().Be(4);
        options.ChapterLabelsEnabled.Should().BeTrue();
    }

    /// <summary>
    /// The one that matters: every kind the code can ask for must have a budget,
    /// so no call can fall through to zero.
    /// </summary>
    [Fact]
    public void Every_call_kind_has_a_budget_a_model_and_a_positive_token_cap()
    {
        var options = Load();

        foreach (var kind in CallKinds.All)
        {
            options.Budgets.Should().ContainKey(kind);
            options[kind].MaxCompletionTokens.Should().BePositive($"{kind} must be able to answer");
        }
    }

    [Fact]
    public void A_zero_budget_fails_at_boot_rather_than_producing_empty_output()
    {
        var act = () => Load(("Reader2:MaxCompletionTokens:ChapterSummary", "0")).Validate();

        act.Should().Throw<Reader2ConfigurationException>().WithMessage("*ChapterSummary*");
    }

    [Theory]
    [InlineData("Reader2:ChunkSize", "0")]
    [InlineData("Reader2:ChunksPerSection", "0")]
    [InlineData("Reader2:Search:MinQueryLength", "0")]
    [InlineData("Reader2:ThreadDormantAfterChapters", "0")]
    [InlineData("Reader2:TierDemotionAfterChapters", "0")]
    [InlineData("Reader2:StoryDigestMaxActors", "0")]
    [InlineData("Reader2:StoryDigestRecentChapters", "0")]
    public void A_nonsensical_value_fails_at_boot(string key, string value)
    {
        var act = () => Load((key, value)).Validate();
        act.Should().Throw<Reader2ConfigurationException>();
    }

    [Fact]
    public void A_search_range_that_excludes_everything_fails_at_boot()
    {
        var act = () => Load(
            ("Reader2:Search:MinQueryLength", "50"),
            ("Reader2:Search:MaxQueryLength", "10")).Validate();

        act.Should().Throw<Reader2ConfigurationException>().WithMessage("*MaxQueryLength*");
    }

    /// <summary>Both together means the provider quietly ignores the reasoning setting.</summary>
    [Fact]
    public void A_temperature_alongside_a_reasoning_effort_fails_at_boot()
    {
        var act = () => Load(("Reader2:Temperature:ExplainSimply", "0.7")).Validate();

        act.Should().Throw<Reader2ConfigurationException>()
            .WithMessage("*mutually exclusive*");
    }

    [Fact]
    public void An_override_is_actually_read_and_not_silently_ignored()
    {
        var options = Load(
            ("Reader2:MaxCompletionTokens:LearnMore", "2000"),
            ("Reader2:Model:PassageAnalysis", "Deep"),
            ("Reader2:DirectSummaryWordThreshold", "42"));

        options[CallKind.LearnMore].MaxCompletionTokens.Should().Be(2000);
        options[CallKind.PassageAnalysis].Model.Should().Be(ModelTier.Deep);
        options.DirectSummaryWordThreshold.Should().Be(42);
    }

    /// <summary>
    /// A distinct endpoint name per kind, so one kind's spend cannot be booked
    /// against another's line in the usage breakdown.
    /// </summary>
    [Fact]
    public void Every_call_kind_has_its_own_endpoint_name()
    {
        var names = CallKinds.All.Select(ModelCalls.EndpointName).ToArray();

        names.Should().OnlyHaveUniqueItems();
        names.Should().OnlyContain(n => n.StartsWith("reader2-"));
        ModelCalls.EndpointName(CallKind.ChapterSummary).Should().Be("reader2-chapter-summary");
    }

    /// <summary>
    /// Story extraction gets the most room of any call, and the reason is not that
    /// it writes the most prose — it writes none. It is the one kind whose output
    /// length scales with how <i>good</i> its input was: a chapter summary naming
    /// thirty-five commanders and twenty-five places must come back as an entry for
    /// each, a container for every place, and an edge for every pair in contact.
    ///
    /// <para>At 4,000 a campaign chapter was cut off mid-JSON. The answer did not
    /// parse, nothing was recorded, and the panel said "none recorded yet" — so a
    /// budget trimmed back here does not read as a smaller answer, it reads as a
    /// broken feature.</para>
    /// </summary>
    [Fact]
    public void Story_extraction_has_the_most_room_of_any_call()
    {
        var options = Load();
        var extraction = options[CallKind.StoryExtraction].MaxCompletionTokens;

        extraction.Should().BeGreaterThanOrEqualTo(
            12000, "a dense chapter's delta runs to thousands of tokens of JSON");

        foreach (var kind in CallKinds.All.Where(k => k != CallKind.StoryExtraction))
            options[kind].MaxCompletionTokens.Should().BeLessThan(
                extraction, $"{kind} writes prose for a reader; extraction writes a record of a cast");
    }
}
