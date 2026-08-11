using System.Text.Json;
using AnnasArchive.API.Reader2.Story;
using AnnasArchive.API.Reader2.Vocabulary;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// That what the server sends is what the browser is written to read.
///
/// <para><b>The gap these close cost two features silently.</b> The application
/// registers no global string-enum converter, so an enum reaches the client as an
/// integer by default — while the cast list filters on <c>"Major"</c> and the
/// vocabulary panel on <c>"Known"</c>. Neither matched anything, ever. The cast
/// table reported "27 not shown" beside "Nothing matches those filters", which is
/// exactly what it should say when a filter genuinely excludes everybody, and the
/// word lists simply came up empty.</para>
///
/// <para>Every test on both sides passed throughout. The frontend specs hand-write
/// an actor with <c>tier: 'Major'</c>, which proves the component works against a
/// shape nothing checked the server produces; the backend tests deserialised into
/// the same C# records they had serialised from, which round-trips through any
/// representation at all. A contract needs one assertion that spans the wire, and
/// this is where those live.</para>
///
/// <para>The list below is written by hand and has to be added to. That is the
/// honest version: "every enum a response can reach" is not something reflection
/// answers well — <see cref="AliasConfidence"/> and <see cref="AnnasArchive.API.Reader2.Storage.ArtifactKind"/>
/// are properties of public records that no client ever sees, and a rule broad
/// enough to catch the real ones demands annotations on those too.</para>
/// </summary>
public class WireFormatTests
{
    /// <summary>
    /// The enums the client declares as string unions. Adding a member to any of
    /// them is covered automatically; adding a <i>new</i> enum to a response is
    /// not, and belongs here.
    /// </summary>
    public static TheoryData<string, object> NamedEnums()
    {
        var data = new TheoryData<string, object>();

        foreach (var type in new[]
                 { typeof(ActorTier), typeof(ThreadStatus), typeof(GroupKind), typeof(TermState) })
            foreach (var value in Enum.GetValues(type))
                data.Add(type.Name, value);

        return data;
    }

    [Theory]
    [MemberData(nameof(NamedEnums))]
    public void Every_enum_the_client_reads_as_a_word_is_sent_as_one(string type, object value)
    {
        JsonSerializer.Serialize(value).Should().Be(
            $"\"{value}\"",
            $"{type} is a string union in reader2.models.ts, and a number there matches no branch of it");
    }

    /// <summary>
    /// Reading still accepts a number, so a model stored under the old shape loads
    /// rather than throwing. This is what makes the change safe without a schema
    /// bump of its own.
    /// </summary>
    [Fact]
    public void A_tier_stored_as_a_number_still_loads()
    {
        JsonSerializer.Deserialize<ActorTier>("3").Should().Be(ActorTier.Major);
    }

    [Fact]
    public void A_tier_written_now_is_read_back_the_same()
    {
        var written = JsonSerializer.Serialize(ActorTier.Secondary);

        JsonSerializer.Deserialize<ActorTier>(written).Should().Be(ActorTier.Secondary);
    }
}
