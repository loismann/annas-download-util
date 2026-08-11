using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.API.Reader2.Ai;

/// <summary>Which of the two configured models a call should use.</summary>
public enum ModelTier { Fast, Deep }

/// <summary>
/// What one call kind costs and how it is tuned.
/// </summary>
/// <param name="Temperature">
/// Null when <paramref name="ReasoningEffort"/> is set. Sending both silently
/// switches reasoning off, which is a mistake Reader I's prompt code documents
/// having made.
/// </param>
public sealed record CallBudget(
    int MaxCompletionTokens,
    ModelTier Model,
    double? Temperature = null,
    string? ReasoningEffort = null);

/// <summary>Everything under <c>Reader2:</c>, read once and checked at boot.</summary>
public sealed class Reader2Options
{
    public required int ChunkSize { get; init; }
    public required int ChunksPerSection { get; init; }

    /// <summary>Below this, a chapter skips tiers 1–2 and goes straight to one call.</summary>
    public required int DirectSummaryWordThreshold { get; init; }

    public required int SearchMinQueryLength { get; init; }
    public required int SearchMaxQueryLength { get; init; }

    /// <summary>The one AI call in the ingestion path.</summary>
    public required bool ChapterLabelsEnabled { get; init; }

    /// <summary>Whether story extraction rides a chapter summary the reader asked for.</summary>
    public required bool AutoIngestOnSummary { get; init; }

    /// <summary>Chapters without a beat before a thread is called dormant.</summary>
    public required int ThreadDormantAfterChapters { get; init; }

    /// <summary>Chapters of absence before a proposed tier demotion is honoured.</summary>
    public required int TierDemotionAfterChapters { get; init; }

    /// <summary>
    /// How many actors the extraction digest may carry. The one number standing
    /// between a 580-character novel and a 17k-token prompt on every chapter.
    /// </summary>
    public required int StoryDigestMaxActors { get; init; }

    /// <summary>
    /// How far back "recently" reaches when the cap above forces a choice about
    /// who to leave out. Configurable alongside the cap because the two are one
    /// decision: raise the cap and this matters less, lower it and this is what
    /// decides whether the model can still resolve an alias to a walk-on.
    /// </summary>
    public required int StoryDigestRecentChapters { get; init; }

    /// <summary>
    /// The two thresholds the merge is tuned by, in the shape the merger takes.
    ///
    /// <para>Exposed as one object so that <see cref="Story.StoryModelMerger"/>
    /// stays a pure function of its arguments and never reads configuration —
    /// which is what lets every merge rule be tested without a host.</para>
    /// </summary>
    public Story.StoryMergeRules MergeRules =>
        new(ThreadDormantAfterChapters, TierDemotionAfterChapters);

    public required IReadOnlyDictionary<CallKind, CallBudget> Budgets { get; init; }

    public CallBudget this[CallKind kind] => Budgets[kind];

    /// <summary>
    /// Reads configuration, applying a default for every key.
    ///
    /// <para>Defaults are the tuned values, not placeholders, so an empty
    /// <c>appsettings</c> section produces the intended behaviour rather than a
    /// degraded one. <see cref="Validate"/> then rejects anything a deployment
    /// has overridden into nonsense.</para>
    /// </summary>
    public static Reader2Options Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("Reader2");

        return new Reader2Options
        {
            ChunkSize = section.GetValue("ChunkSize", SectionChunker.DefaultTargetWords),
            ChunksPerSection = section.GetValue("ChunksPerSection", 4),
            DirectSummaryWordThreshold = section.GetValue("DirectSummaryWordThreshold", 1200),
            SearchMinQueryLength = section.GetValue("Search:MinQueryLength", BookSearch.DefaultMinQueryLength),
            SearchMaxQueryLength = section.GetValue("Search:MaxQueryLength", BookSearch.DefaultMaxQueryLength),
            ChapterLabelsEnabled = section.GetValue("ChapterLabels:Enabled", true),
            AutoIngestOnSummary = section.GetValue("StoryModel:AutoIngestOnSummary", true),
            ThreadDormantAfterChapters = section.GetValue("ThreadDormantAfterChapters", 10),
            TierDemotionAfterChapters = section.GetValue("TierDemotionAfterChapters", 10),
            StoryDigestMaxActors = section.GetValue("StoryDigestMaxActors", 120),
            StoryDigestRecentChapters = section.GetValue("StoryDigestRecentChapters", 20),
            Budgets = CallKinds.All.ToDictionary(kind => kind, kind => BudgetFor(section, kind))
        };
    }

    private static CallBudget BudgetFor(IConfigurationSection section, CallKind kind)
    {
        var fallback = Defaults[kind];

        return new CallBudget(
            section.GetValue($"MaxCompletionTokens:{kind}", fallback.MaxCompletionTokens),
            section.GetValue($"Model:{kind}", fallback.Model),
            section.GetValue<double?>($"Temperature:{kind}") ?? fallback.Temperature,
            section.GetValue<string?>($"ReasoningEffort:{kind}") ?? fallback.ReasoningEffort);
    }

    /// <summary>
    /// The tuned defaults. Deep for interpretive writing, fast for anything
    /// bounded and structural — story extraction is fast because it reads prose
    /// that has <i>already</i> been summarised, which is what keeps the story
    /// model's per-chapter cost marginal.
    /// </summary>
    private static readonly IReadOnlyDictionary<CallKind, CallBudget> Defaults =
        new Dictionary<CallKind, CallBudget>
        {
            [CallKind.ChunkSummary] = new(1200, ModelTier.Deep, Temperature: 0.5),
            [CallKind.SectionSynthesis] = new(1500, ModelTier.Deep, Temperature: 0.5),
            [CallKind.ChapterSummary] = new(2500, ModelTier.Deep, Temperature: 0.6),
            [CallKind.SectionSummary] = new(1800, ModelTier.Deep, Temperature: 0.6),
            [CallKind.PassageAnalysis] = new(1600, ModelTier.Fast, Temperature: 0.5),
            [CallKind.ExplainSimply] = new(1500, ModelTier.Deep, ReasoningEffort: "medium"),
            [CallKind.LearnMore] = new(2000, ModelTier.Deep, Temperature: 0.6),
            // The largest budget here, and it has to be. This is the one call whose
            // output length scales with how *good* the input was: a chapter summary
            // naming thirty-five commanders and twenty-five places must come back as
            // an entry for each, a container for every place, and an edge for every
            // pair in contact. At 4,000 a campaign chapter was cut off mid-JSON, the
            // answer would not parse, and the record came back empty — see the note
            // on truncation in StoryModelService.ExtractAsync.
            [CallKind.StoryExtraction] = new(12000, ModelTier.Fast, Temperature: 0.2),
            [CallKind.ChapterLabels] = new(1500, ModelTier.Fast, Temperature: 0.2),
            // Generous on purpose. Terms the reader already knows are excluded in
            // the input, and an exclusion list only earns its place if the model
            // can spend the words it saves on the terms that are left.
            [CallKind.SectionVocab] = new(1800, ModelTier.Fast, Temperature: 0.3)
        };

    /// <summary>
    /// Rejects a configuration that cannot work.
    ///
    /// <para>This exists because of a specific Reader I defect:
    /// <c>AI:MaxCompletionTokens:LearnMore</c> sat configured at 2,000 while the
    /// code read a different key and silently capped the feature at 1,200. Nobody
    /// noticed for months, because a wrong budget produces shorter output rather
    /// than an error. A zero or missing budget now stops the deploy.</para>
    /// </summary>
    /// <exception cref="Reader2ConfigurationException"/>
    public void Validate()
    {
        Require(ChunkSize > 0, "Reader2:ChunkSize must be positive.");
        Require(ChunksPerSection > 0, "Reader2:ChunksPerSection must be positive.");
        Require(DirectSummaryWordThreshold >= 0, "Reader2:DirectSummaryWordThreshold cannot be negative.");
        Require(SearchMinQueryLength > 0, "Reader2:Search:MinQueryLength must be positive.");
        Require(
            SearchMaxQueryLength > SearchMinQueryLength,
            "Reader2:Search:MaxQueryLength must exceed MinQueryLength.");

        // Zero would mark every thread dormant the moment it was opened, and
        // demote anyone the model had a quiet opinion about — both are states the
        // reader would have to un-learn rather than errors they would see.
        Require(
            ThreadDormantAfterChapters > 0,
            "Reader2:ThreadDormantAfterChapters must be positive.");
        Require(
            TierDemotionAfterChapters > 0,
            "Reader2:TierDemotionAfterChapters must be positive.");
        Require(
            StoryDigestMaxActors > 0,
            "Reader2:StoryDigestMaxActors must be positive; a digest of nobody makes every "
            + "chapter re-introduce the entire cast.");

        // Zero would fill the cap with majors alone, and a minor character absent
        // from the digest is one the model cannot resolve a new name to — so it
        // invents a duplicate, and duplicates are permanent.
        Require(
            StoryDigestRecentChapters > 0,
            "Reader2:StoryDigestRecentChapters must be positive.");

        foreach (var kind in CallKinds.All)
        {
            Require(Budgets.ContainsKey(kind), $"Reader2:MaxCompletionTokens:{kind} has no value or default.");

            var budget = Budgets[kind];
            Require(
                budget.MaxCompletionTokens > 0,
                $"Reader2:MaxCompletionTokens:{kind} must be positive; a zero budget produces "
                + "empty output rather than an error.");

            // Both together is the failure that looks like it worked: the provider
            // takes the temperature and quietly ignores the reasoning setting.
            Require(
                budget.Temperature is null || budget.ReasoningEffort is null,
                $"Reader2 {kind} sets both Temperature and ReasoningEffort; they are mutually exclusive.");
        }
    }

    private static void Require(bool condition, string problem)
    {
        if (!condition) throw new Reader2ConfigurationException(problem);
    }
}

/// <summary>Configuration that cannot work, discovered at boot.</summary>
public sealed class Reader2ConfigurationException(string message) : Exception(message);
