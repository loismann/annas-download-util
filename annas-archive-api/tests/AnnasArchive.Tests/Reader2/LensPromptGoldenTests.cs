using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// Pins every prompt to a file named for the version that produced it.
///
/// <para><b>Why the version is in the filename.</b> An artifact row records the
/// <c>PromptVersion</c> it was generated under, and the store treats a row from
/// an older version as a miss. That only works if the version actually moves
/// when the wording does. A golden file alone does not achieve it — you would
/// edit the prompt, re-record the golden, and ship silently reusing months of
/// output from wording nobody has read.</para>
///
/// <para>So the goldens live at <c>{key}.v{version}.{tier}.txt</c> and three
/// rules run together: the current prompts must match the current version's
/// files; every version from 1 up must still have its files; and no two
/// consecutive versions may be identical. Editing without bumping fails the
/// first. Bumping without editing fails the third. Passing requires doing both,
/// which is the point.</para>
/// </summary>
public class LensPromptGoldenTests
{
    /// <summary>
    /// Production lenses only. <c>TestLens</c> is deliberately absent — it exists
    /// to prove a lens can be added without touching production, and pinning its
    /// wording here would make it the exception that disproves that.
    /// </summary>
    public static TheoryData<string> ProductionLenses() => new() { "literary", "military", "fiction" };

    private static readonly IReadOnlyList<IReaderLens> Lenses =
        [new LiteraryLens(), new MilitaryLens(), new FictionLens()];

    private static readonly string GoldenDirectory =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Reader2Prompts");

    private static IReaderLens Lens(string key) => Lenses.Single(l => l.Key == key);

    private static string PathFor(string key, int version, CallKind tier) =>
        Path.Combine(GoldenDirectory, $"{key}.v{version}.{Tier(tier)}.txt");

    private static string Tier(CallKind tier) => tier.ToString().ToLowerInvariant();

    /// <summary>Newlines only, so a checkout on another platform does not "change" a prompt.</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    private static string ReadGolden(string key, int version, CallKind tier)
    {
        var path = PathFor(key, version, tier);

        File.Exists(path).Should().BeTrue(
            $"golden {Path.GetFileName(path)} must exist — create it from the current prompt "
            + "and make sure PromptVersion was bumped");

        return Normalize(File.ReadAllText(path));
    }

    /// <summary>
    /// The list above is written by hand, so it can be forgotten. This is what
    /// stops a fourth book type shipping with no prompt pinned at all — every
    /// lens the API assembly defines has to appear here.
    /// </summary>
    [Fact]
    public void Every_lens_the_api_ships_is_pinned_here()
    {
        var shipped = typeof(LiteraryLens).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IReaderLens).IsAssignableFrom(t))
            .Select(t => ((IReaderLens)Activator.CreateInstance(t)!).Key);

        Lenses.Select(l => l.Key).Should().BeEquivalentTo(shipped,
            "a lens with no golden file is a prompt that can be edited without its "
            + "version moving, which is the one thing this file exists to prevent");
    }

    [Theory]
    [MemberData(nameof(ProductionLenses))]
    public void Every_prompt_matches_the_golden_for_its_current_version(string key)
    {
        var lens = Lens(key);

        foreach (var tier in CallKinds.Lens)
        {
            var prompt = lens.Prompts[tier];
            if (prompt is null) continue;

            Normalize(prompt).Should().Be(
                ReadGolden(key, lens.PromptVersion, tier),
                $"{key}'s {tier} prompt changed without its golden being updated, or without "
                + "PromptVersion being bumped alongside it");
        }
    }

    /// <summary>
    /// Every historical version keeps its files. Without this, bumping the version
    /// and deleting the old goldens would satisfy every other rule here.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProductionLenses))]
    public void Every_version_from_one_upwards_still_has_its_goldens(string key)
    {
        var lens = Lens(key);

        for (var version = 1; version <= lens.PromptVersion; version++)
            foreach (var tier in CallKinds.RequiredOfEveryLens)
                File.Exists(PathFor(key, version, tier)).Should().BeTrue(
                    $"{key} v{version} {tier} must be kept — the version history is what makes "
                    + "a stale artifact detectable");
    }

    /// <summary>A version bump has to mean something changed.</summary>
    [Theory]
    [MemberData(nameof(ProductionLenses))]
    public void No_two_consecutive_versions_are_identical(string key)
    {
        var lens = Lens(key);

        for (var version = 2; version <= lens.PromptVersion; version++)
        {
            var previous = CallKinds.RequiredOfEveryLens.Select(t => ReadGolden(key, version - 1, t));
            var current = CallKinds.RequiredOfEveryLens.Select(t => ReadGolden(key, version, t));

            current.Should().NotEqual(previous,
                $"{key} v{version} is word for word v{version - 1}; a bump with no edit makes "
                + "every artifact from the earlier version stale for nothing");
        }
    }

    /// <summary>
    /// The lens-independent prompts get the same treatment. They are outside the
    /// lens contract but not outside the versioning rule — a chapter-label edit
    /// that did not move <see cref="SharedPrompts.Version"/> would leave labels
    /// generated by wording nobody has used in months looking current.
    /// </summary>
    [Fact]
    public void Shared_prompts_match_the_golden_for_their_current_version()
    {
        SharedPrompts.All.Should().NotBeEmpty();

        foreach (var (name, prompt) in SharedPrompts.All)
        {
            var path = Path.Combine(GoldenDirectory, $"shared.v{SharedPrompts.Version}.{name}.txt");

            File.Exists(path).Should().BeTrue($"golden {Path.GetFileName(path)} must exist");
            Normalize(prompt).Should().Be(Normalize(File.ReadAllText(path)), $"shared {name} changed");
        }
    }

    /// <summary>
    /// The image rules came from Reader I the hard way — a hallucinated Wikimedia
    /// URL renders as a broken image — so they are pinned by content, not just by
    /// hash, and a well-meaning edit that softens them fails here.
    /// </summary>
    [Fact]
    public void The_deep_dive_keeps_reader_ones_strict_image_rules()
    {
        SharedPrompts.LearnMore.Should()
            .Contain("upload.wikimedia.org")
            .And.Contain("fully-qualified")
            .And.Contain("skip images entirely")
            .And.Contain("No base64");
    }

    /// <summary>
    /// Prompts are the product. This asserts none of them reaches the browser
    /// through the payload that drives the picker.
    /// </summary>
    [Fact]
    public void No_prompt_text_is_served_to_the_client()
    {
        foreach (var lens in Lenses.Concat<IReaderLens>([new TestLens()]))
        {
            var served = System.Text.Json.JsonSerializer.Serialize(
                AnnasArchive.API.Reader2.Endpoints.LensResponse.From(lens, isDefault: false));

            foreach (var tier in CallKinds.Lens)
            {
                var prompt = lens.Prompts[tier];
                if (prompt is null) continue;

                served.Should().NotContain(prompt[..Math.Min(40, prompt.Length)],
                    $"{lens.Key}'s {tier} prompt must not leave the server");
            }
        }
    }

    /// <summary>
    /// Book text is passed as user content, never folded into an instruction. A
    /// prompt that interpolates the passage would let the book rewrite the
    /// instructions, and would make the golden unpinnable.
    /// </summary>
    [Fact]
    public void No_prompt_contains_an_interpolation_hole_for_book_text()
    {
        foreach (var lens in Lenses)
            foreach (var tier in CallKinds.Lens)
                if (lens.Prompts[tier] is { } prompt)
                    prompt.Should().NotMatchRegex(
                        @"\{[A-Za-z_]", $"{lens.Key}'s {tier} prompt looks like a template");
    }
}
