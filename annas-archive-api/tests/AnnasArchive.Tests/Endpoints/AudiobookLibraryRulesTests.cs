using AnnasArchive.API.Endpoints;

namespace AnnasArchive.Tests.Endpoints;

/// <summary>
/// The four pure decisions behind the audiobook endpoints.
///
/// <para>These were private statics inside a 513-line endpoint file with no test
/// file of its own, so none of them had ever been run against anything but a live
/// Audiobookshelf. Two are the kind that fails quietly rather than loudly: the id
/// guard is the only thing between a route parameter and an outbound request, and
/// the metadata validator is what decides whether an edit is one this app will
/// store at all.</para>
/// </summary>
public class AudiobookLibraryRulesTests
{
    // ------------------------------------------------------------- id guard

    /// <summary>
    /// The id arrives from the route and goes into both an Audiobookshelf URL and
    /// a metadata store key. Anything that could change which resource is
    /// addressed has to be refused rather than escaped, because the two consumers
    /// would need different escaping.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData("li_abc/../../admin")]
    [InlineData("li_abc/sub")]
    [InlineData("li_abc\\sub")]
    [InlineData("http://elsewhere.test/x")]
    [InlineData("li:abc")]
    public void AnIdThatCouldAddressSomethingElseIsRejected(string id)
    {
        AudiobookLibraryRules.SanitizeId(id).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void AnEmptyIdIsRejected(string id)
    {
        AudiobookLibraryRules.SanitizeId(id).Should().BeNull();
    }

    /// <summary>
    /// The real shape: Audiobookshelf's own library-item ids. The guard must not
    /// be so eager that it rejects them — a false positive here is an audiobook
    /// nobody can open.
    /// </summary>
    [Theory]
    [InlineData("li_8x9v2mkq1p0zbn4t")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("li_abc.123")]
    public void ARealAudiobookshelfIdIsAccepted(string id)
    {
        AudiobookLibraryRules.SanitizeId(id).Should().Be(id);
    }

    /// <summary>
    /// A single dot is a legitimate character in these ids and only a pair means
    /// traversal. Rejecting every dot would be the easy over-correction.
    /// </summary>
    [Fact]
    public void ASingleDotIsNotTraversal()
    {
        AudiobookLibraryRules.SanitizeId("li_v1.2").Should().Be("li_v1.2");
    }

    // ---------------------------------------------------------- metadata

    private static SetMediaMetadataRequest Edit(
        List<string>? owners = null, List<string>? genres = null, string? title = null) =>
        new(owners, genres, title);

    /// <summary>
    /// The whole edit is refused rather than partially applied. Storing the valid
    /// half of a rejected edit is how an owner list ends up with a member the
    /// household does not have.
    /// </summary>
    [Fact]
    public void AnEditNamingSomeoneOutsideTheHouseholdIsRejectedEntirely()
    {
        var result = AudiobookLibraryRules.ValidateMetadata(
            Edit(owners: ["Paul", "Stranger"], genres: ["Sci-Fi"], title: "A Title"));

        result.Should().BeNull();
    }

    [Fact]
    public void TheHouseholdIsAccepted()
    {
        var result = AudiobookLibraryRules.ValidateMetadata(Edit(owners: ["Paul", "Mom", "Dad"]));

        result!.Owners.Should().BeEquivalentTo("Paul", "Mom", "Dad");
    }

    /// <summary>Owners arrive from a picker whose casing nobody controls.</summary>
    [Fact]
    public void OwnerMatchingIgnoresCase()
    {
        AudiobookLibraryRules.ValidateMetadata(Edit(owners: ["pAuL"]))
            .Should().NotBeNull();
    }

    /// <summary>Both lists come from the same free-text UI and get the same treatment.</summary>
    [Fact]
    public void OwnersAndGenresAreTrimmedDeduplicatedAndStrippedOfBlanks()
    {
        var result = AudiobookLibraryRules.ValidateMetadata(
            Edit(owners: ["  Paul  ", "PAUL", "", "   "], genres: [" Sci-Fi ", "sci-fi", ""]));

        result!.Owners.Should().ContainSingle().Which.Should().Be("Paul");
        result.Genres.Should().ContainSingle().Which.Should().Be("Sci-Fi");
    }

    /// <summary>
    /// Genres are free text by design — the household invents them. Only owners
    /// are checked against a list.
    /// </summary>
    [Fact]
    public void AnyGenreIsAllowed()
    {
        var result = AudiobookLibraryRules.ValidateMetadata(
            Edit(owners: ["Paul"], genres: ["Whatever Dad Calls This"]));

        result!.Genres.Should().ContainSingle().Which.Should().Be("Whatever Dad Calls This");
    }

    /// <summary>Absent lists are an edit that clears them, not a malformed request.</summary>
    [Fact]
    public void AbsentListsAreEmptyRatherThanAFailure()
    {
        var result = AudiobookLibraryRules.ValidateMetadata(Edit());

        result!.Owners.Should().BeEmpty();
        result.Genres.Should().BeEmpty();
    }

    /// <summary>
    /// The audiobook-only field, and the reason this rule is not simply
    /// <see cref="MediaLibraryRules.ValidateMetadata"/>.
    /// </summary>
    [Fact]
    public void ATitleOverrideIsCarriedThroughTrimmed()
    {
        var result = AudiobookLibraryRules.ValidateMetadata(
            Edit(owners: ["Paul"], title: "  The Hobbit  "));

        result!.Title.Should().Be("The Hobbit");
    }

    /// <summary>
    /// A blank title means "not part of this save", so the store merges the
    /// existing override forward. Passing an empty string through instead of null
    /// would wipe a title the user never touched.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankTitleIsNullSoTheStoreLeavesTheExistingOneAlone(string? title)
    {
        var result = AudiobookLibraryRules.ValidateMetadata(Edit(owners: ["Paul"], title: title));

        result!.Title.Should().BeNull();
    }

    /// <summary>
    /// The title must not survive a rejected edit — the whole request is refused,
    /// and returning a partly-built record would invite a caller to use it.
    /// </summary>
    [Fact]
    public void ARejectedEditCarriesNoTitleEither()
    {
        AudiobookLibraryRules.ValidateMetadata(Edit(owners: ["Stranger"], title: "A Title"))
            .Should().BeNull();
    }

    // ------------------------------------------------------ upstream failure

    /// <summary>
    /// These three are what an unreachable or slow Audiobookshelf actually throws
    /// through the resilience pipeline, and together they are the difference
    /// between a friendly 502 and an unexplained 500.
    /// </summary>
    [Fact]
    public void TheThreeUpstreamFailureShapesAreRecognised()
    {
        AudiobookLibraryRules.IsUpstreamFailure(new HttpRequestException("down")).Should().BeTrue();
        AudiobookLibraryRules.IsUpstreamFailure(new Polly.Timeout.TimeoutRejectedException()).Should().BeTrue();
        AudiobookLibraryRules.IsUpstreamFailure(new TaskCanceledException()).Should().BeTrue();
    }

    /// <summary>
    /// A bug in this app is not an upstream failure. Reporting one as 502 would
    /// blame Audiobookshelf for something it never saw, and hide the real error.
    /// </summary>
    [Fact]
    public void ALocalBugIsNotBlamedOnAudiobookshelf()
    {
        AudiobookLibraryRules.IsUpstreamFailure(new InvalidOperationException()).Should().BeFalse();
        AudiobookLibraryRules.IsUpstreamFailure(new NullReferenceException()).Should().BeFalse();
        AudiobookLibraryRules.IsUpstreamFailure(new ArgumentException()).Should().BeFalse();
    }

    // ------------------------------------------------------------- covers

    /// <summary>
    /// Stored covers are served straight back to an <c>&lt;img&gt;</c>, so the
    /// content type has to match the bytes or the browser refuses to render them.
    /// </summary>
    [Theory]
    [InlineData("cover.jpg", "image/jpeg")]
    [InlineData("cover.jpeg", "image/jpeg")]
    [InlineData("cover.JPG", "image/jpeg")]
    [InlineData("cover.png", "image/png")]
    [InlineData("cover.gif", "image/gif")]
    [InlineData("cover.webp", "image/webp")]
    public void AStoredCoverIsServedAsWhatItIs(string file, string expected)
    {
        AudiobookLibraryRules.ContentTypeForCoverFile(file).Should().Be(expected);
    }

    /// <summary>
    /// Anything unrecognised falls back to a type no browser will execute or
    /// render — the safe answer for a file whose bytes we are not vouching for.
    /// </summary>
    [Theory]
    [InlineData("cover.svg")]
    [InlineData("cover.html")]
    [InlineData("cover")]
    [InlineData("")]
    public void AnUnrecognisedCoverIsNotGivenARenderableType(string file)
    {
        AudiobookLibraryRules.ContentTypeForCoverFile(file).Should().Be("application/octet-stream");
    }
}
