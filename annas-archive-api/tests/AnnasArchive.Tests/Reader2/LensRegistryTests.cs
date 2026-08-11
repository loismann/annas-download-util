using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// The registry's job is to refuse a bad lens at boot. Every test here is one
/// deploy that should fail loudly rather than one reader who finds out later.
/// </summary>
public class LensRegistryTests
{
    private static LensRegistry Registry(params IReaderLens[] lenses) => new(lenses);

    private static Action Building(params IReaderLens[] lenses) => () => Registry(lenses);

    [Fact]
    public void The_registry_orders_by_sort_order_and_defaults_to_the_first()
    {
        var registry = Registry(new TestLens(), new LiteraryLens());

        registry.All.Select(l => l.Key).Should().Equal("literary", TestLens.LensKey);
        registry.Default.Key.Should().Be("literary");
    }

    [Fact]
    public void An_unknown_key_resolves_to_null_rather_than_the_default()
    {
        var registry = Registry(new LiteraryLens());

        registry.TryGet("no-such-lens", out var lens).Should().BeFalse();
        lens.Should().BeNull("a caller that ignores the result must not silently get a default");
        registry.ForRequest("no-such-lens").Should().BeNull();
    }

    /// <summary>Omitting a type is a choice; naming a wrong one is a mistake.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_key_resolves_to_the_default(string? key)
    {
        Registry(new LiteraryLens()).ForRequest(key)!.Key.Should().Be("literary");
    }

    [Fact]
    public void Lookup_ignores_case_because_the_database_column_does_not()
    {
        Registry(new LiteraryLens()).ForRequest("LITERARY")!.Key.Should().Be("literary");
    }

    [Fact]
    public void No_lenses_at_all_fails_to_start()
    {
        Building().Should().Throw<LensConfigurationException>().WithMessage("*No reader lenses*");
    }

    [Fact]
    public void Two_lenses_with_one_key_fail_to_start()
    {
        Building(new LiteraryLens(), new BrokenLens { Key = "literary" })
            .Should().Throw<LensConfigurationException>().WithMessage("*claim the key 'literary'*");
    }

    [Theory]
    [InlineData("Literary")]
    [InlineData("literary lens")]
    [InlineData("literary_lens")]
    [InlineData("2legit")]
    [InlineData("")]
    public void A_key_that_is_not_lowercase_kebab_case_fails_to_start(string key)
    {
        Building(new BrokenLens { Key = key })
            .Should().Throw<LensConfigurationException>().WithMessage("*kebab-case*");
    }

    [Fact]
    public void Claiming_a_story_model_without_an_extraction_prompt_fails_to_start()
    {
        Building(new BrokenLens
        {
            BuildsStoryModel = true,
            StoryVocabulary = new StoryVocabulary("a", "b", "c")
        })
            .Should().Throw<LensConfigurationException>().WithMessage("*no StoryExtraction prompt*");
    }

    /// <summary>The other direction: dead prompt text that reads as a working feature.</summary>
    [Fact]
    public void An_extraction_prompt_without_a_story_model_fails_to_start()
    {
        Building(new BrokenLens
        {
            BuildsStoryModel = false,
            Prompts = new BrokenLens().Prompts with { StoryExtraction = "extract" }
        })
            .Should().Throw<LensConfigurationException>().WithMessage("*does not build a story model*");
    }

    [Fact]
    public void Claiming_a_story_model_without_vocabulary_fails_to_start()
    {
        Building(new BrokenLens
        {
            BuildsStoryModel = true,
            Prompts = new BrokenLens().Prompts with { StoryExtraction = "extract" }
        })
            .Should().Throw<LensConfigurationException>().WithMessage("*no StoryVocabulary*");
    }

    [Fact]
    public void A_missing_required_prompt_fails_to_start()
    {
        Building(new BrokenLens { Prompts = new BrokenLens().Prompts with { ChapterSummary = "  " } })
            .Should().Throw<LensConfigurationException>().WithMessage("*ChapterSummary prompt*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_prompt_version_below_one_fails_to_start(int version)
    {
        Building(new BrokenLens { Versions = PromptVersions.All(version) })
            .Should().Throw<LensConfigurationException>().WithMessage("*prompt version*");
    }

    /// <summary>
    /// Every prompt, not only the ones every lens must supply. A zero on the
    /// optional story prompt would make its artifacts impossible to mark stale
    /// ever again, which fails quietly rather than loudly.
    /// </summary>
    [Fact]
    public void One_prompt_version_below_one_fails_to_start_even_when_the_rest_are_fine()
    {
        Building(new BrokenLens
        {
            BuildsStoryModel = true,
            StoryVocabulary = new StoryVocabulary("A", "B", "C"),
            Prompts = new BrokenLens().Prompts with { StoryExtraction = "x" },
            Versions = PromptVersions.All(1) with { StoryExtraction = 0 }
        })
            .Should().Throw<LensConfigurationException>().WithMessage("*StoryExtraction prompt version*");
    }

    [Theory]
    [InlineData("DisplayName")]
    [InlineData("Description")]
    [InlineData("Icon")]
    public void A_lens_with_nothing_to_show_the_reader_fails_to_start(string blankProperty)
    {
        var lens = blankProperty switch
        {
            "DisplayName" => new BrokenLens { DisplayName = " " },
            "Description" => new BrokenLens { Description = " " },
            _ => new BrokenLens { Icon = " " }
        };

        Building(lens).Should().Throw<LensConfigurationException>();
    }

    /// <summary>
    /// Every tier must be reachable through the indexer, or a tier could be added
    /// to the enum and silently never sent to a model.
    /// </summary>
    [Fact]
    public void Every_prompt_tier_is_reachable_on_a_fully_populated_lens()
    {
        var prompts = new TestLens().Prompts;

        CallKinds.Lens.Should().OnlyContain(k => !string.IsNullOrWhiteSpace(prompts[k]));
        CallKinds.RequiredOfEveryLens.Should().NotContain(CallKind.StoryExtraction);
        CallKinds.Lens.Should().Contain(CallKind.StoryExtraction);

        // The kinds no lens owns must come back empty, not throw and not
        // accidentally alias another tier's wording.
        CallKinds.All.Except(CallKinds.Lens).Should()
            .OnlyContain(k => prompts[k] == null)
            .And.BeEquivalentTo([CallKind.ChapterLabels, CallKind.LearnMore, CallKind.SectionVocab]);
    }
}
