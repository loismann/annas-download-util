using AnnasArchive.API.Endpoints;

namespace AnnasArchive.Tests.Endpoints;

/// <summary>
/// The two rules that decide whether <c>/api/library/cover/{*path}</c> hands a
/// file back. Both are pure so they can be asserted without a library on disk.
/// </summary>
public class LibraryCoverEndpointsTests
{
    private static string Root => Path.Combine(Path.DirectorySeparatorChar.ToString(), "volume1", "books", "Library");

    private static string Under(params string[] parts) =>
        Path.GetFullPath(Path.Combine(new[] { Root }.Concat(parts).ToArray()));

    // ── containment ────────────────────────────────────────────────────────

    [Fact]
    public void IsInsideRoot_AcceptsAFileDirectlyInTheRoot()
    {
        LibraryCoverEndpoints.IsInsideRoot(Root, Under("cover.jpg")).Should().BeTrue();
    }

    [Fact]
    public void IsInsideRoot_AcceptsAFileInANestedFolder()
    {
        LibraryCoverEndpoints.IsInsideRoot(Root, Under("_covers", "a", "cover.jpg")).Should().BeTrue();
    }

    /// <summary>
    /// The reason this is a function and not a <c>StartsWith</c>. A sibling whose
    /// name merely begins with the root's is entirely outside the library, and a
    /// prefix test lets it through.
    /// </summary>
    [Fact]
    public void IsInsideRoot_RejectsASiblingWhoseNameStartsWithTheRoots()
    {
        var sibling = Path.GetFullPath(Root + "-backup" + Path.DirectorySeparatorChar + "secret.jpg");

        sibling.StartsWith(Root, StringComparison.OrdinalIgnoreCase).Should().BeTrue(
            "otherwise this test is not exercising the case the separator exists to catch");
        LibraryCoverEndpoints.IsInsideRoot(Root, sibling).Should().BeFalse();
    }

    [Fact]
    public void IsInsideRoot_RejectsAPathThatClimbsOutWithDotDot()
    {
        var escaped = Path.GetFullPath(Path.Combine(Root, "..", "..", "etc", "passwd"));

        LibraryCoverEndpoints.IsInsideRoot(Root, escaped).Should().BeFalse();
    }

    [Fact]
    public void IsInsideRoot_RejectsTheRootItself()
    {
        LibraryCoverEndpoints.IsInsideRoot(Root, Path.GetFullPath(Root)).Should().BeFalse();
    }

    [Fact]
    public void IsInsideRoot_ToleratesATrailingSeparatorOnTheConfiguredRoot()
    {
        var rootWithSlash = Root + Path.DirectorySeparatorChar;

        LibraryCoverEndpoints.IsInsideRoot(rootWithSlash, Under("cover.jpg")).Should().BeTrue();
    }

    // ── extension allowlist ────────────────────────────────────────────────

    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    [InlineData(".gif", "image/gif")]
    [InlineData(".webp", "image/webp")]
    public void CoverContentTypes_ServesTheImageFormatsCoversAreStoredIn(string extension, string expected)
    {
        LibraryCoverEndpoints.CoverContentTypes[extension].Should().Be(expected);
    }

    [Fact]
    public void CoverContentTypes_IsCaseInsensitive()
    {
        LibraryCoverEndpoints.CoverContentTypes.ContainsKey(".JPG").Should().BeTrue();
    }

    /// <summary>
    /// The point of the allowlist: the route used to fall through to
    /// <c>application/octet-stream</c>, so every book under the library root was
    /// retrievable by exact path from a thumbnail endpoint.
    /// </summary>
    [Theory]
    [InlineData(".epub")]
    [InlineData(".pdf")]
    [InlineData(".mobi")]
    [InlineData(".azw3")]
    [InlineData(".json")]
    [InlineData(".db")]
    [InlineData("")]
    public void CoverContentTypes_RefusesEverythingThatIsNotAnImage(string extension)
    {
        LibraryCoverEndpoints.CoverContentTypes.ContainsKey(extension).Should().BeFalse();
    }
}
