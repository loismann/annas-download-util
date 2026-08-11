using System.Reflection;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Story;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// That the prompt asks for what the parser reads.
///
/// <para><b>This is the test whose absence hollowed out the feature.</b> The
/// extraction prompt named the eight arrays and nothing inside them, so the model
/// answered with the only two fields the surrounding prose happened to mention — a
/// name and a tier. Thirty-two characters reached the record with no dossier, no
/// role, no arc, and not one relationship between them. Nothing failed: every
/// parser test hand-writes its JSON in the shape the parser wants, which proves
/// the parser reads that shape and says nothing at all about whether anybody was
/// ever asked for it.</para>
///
/// <para>The field list is <i>derived</i> from the delta records rather than typed
/// out here, because a hand-written copy is a third statement of the contract and
/// would drift the same way the first two did. Adding a field to
/// <see cref="NewActor"/> fails this until the prompt asks for it.</para>
/// </summary>
public class StoryWireContractTests
{
    private static readonly IReadOnlyList<IReaderLens> StoryLenses =
        [new MilitaryLens(), new FictionLens()];

    /// <summary>
    /// Every JSON key the extraction contract is made of: the eight array names
    /// from <see cref="StoryDelta"/>, and every field of the records they hold.
    /// </summary>
    /// <remarks>
    /// <c>Chapter</c> is excluded because the model is never asked for it — the
    /// ingest supplies which chapter it is reading, and a chapter number the model
    /// chose would be one more thing that could be wrong.
    /// </remarks>
    public static TheoryData<string> ContractKeys()
    {
        var data = new TheoryData<string>();

        foreach (var key in Keys(typeof(StoryDelta)).Where(k => k != "chapter").Distinct())
            data.Add(key);

        return data;
    }

    private static IEnumerable<string> Keys(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return Camel(property.Name);

            // One level down: the arrays on the delta hold the records whose own
            // fields are the rest of the contract.
            if (Element(property.PropertyType) is { } element)
                foreach (var nested in element.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    yield return Camel(nested.Name);
        }
    }

    /// <summary>The record an <c>IReadOnlyList&lt;T&gt;</c> holds, or null.</summary>
    private static Type? Element(Type type) =>
        type.IsGenericType && type.GetGenericArguments() is [{ IsClass: true } element]
        && element.Namespace == typeof(StoryDelta).Namespace
            ? element
            : null;

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    [Theory]
    [MemberData(nameof(ContractKeys))]
    public void Every_story_lens_asks_for_every_field_the_parser_reads(string key)
    {
        foreach (var lens in StoryLenses)
            lens.Prompts[CallKind.StoryExtraction].Should().NotBeNull()
                .And.Subject.As<string>().Should().Contain($"\"{key}\"",
                    $"{lens.Key} parses \"{key}\" but never asks for it, so the model has no way "
                    + "to know it is wanted — which is how a cast list arrives with no dossiers");
    }

    /// <summary>
    /// The values, not just the field names. A tier the parser cannot read falls
    /// to <see cref="ActorTier.Mentioned"/> and a confidence it cannot read falls
    /// to <see cref="AliasConfidence.Low"/>, so a prompt offering words the parser
    /// does not accept degrades silently into a cast of nobodies.
    /// </summary>
    [Theory]
    [MemberData(nameof(EnumValues))]
    public void Every_story_lens_offers_only_values_the_parser_accepts(string word, string parsesAs)
    {
        foreach (var lens in StoryLenses)
            lens.Prompts[CallKind.StoryExtraction]!.Should().Contain($"\"{word}\"",
                $"the prompt must offer \"{word}\", which the parser reads as {parsesAs}");
    }

    public static TheoryData<string, string> EnumValues()
    {
        var data = new TheoryData<string, string>();

        foreach (var tier in Enum.GetValues<ActorTier>())
            data.Add(tier.ToString().ToLowerInvariant(), $"ActorTier.{tier}");

        foreach (var confidence in Enum.GetValues<AliasConfidence>())
            data.Add(confidence.ToString().ToLowerInvariant(), $"AliasConfidence.{confidence}");

        return data;
    }

    /// <summary>
    /// Group kinds are offered hyphenated — <c>military-unit</c> — and the parser
    /// strips the hyphen before parsing. This pins that agreement, which is
    /// otherwise a coincidence between two files.
    /// </summary>
    [Fact]
    public void Every_group_kind_the_prompt_offers_is_one_the_parser_knows()
    {
        foreach (var lens in StoryLenses)
        {
            var prompt = lens.Prompts[CallKind.StoryExtraction]!;

            foreach (var kind in Enum.GetValues<GroupKind>())
            {
                var offered = Hyphenate(kind.ToString());

                prompt.Should().Contain($"\"{offered}\"", $"{lens.Key} must offer the {kind} kind");
                StoryExtraction.Parse($$"""{"newGroups": [{"name": "X", "kind": "{{offered}}"}]}""", 0)
                    .NewGroups.Single().Kind.Should().Be(kind);
            }
        }
    }

    private static string Hyphenate(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? $"-{c}" : $"{c}")).ToLowerInvariant();
}
