using AnnasArchive.API.Configuration;

namespace AnnasArchive.Tests.Configuration;

/// <summary>
/// The backend half of the ebook-cover cookie contract. The frontend half is
/// pinned in <c>auth.service.spec.ts</c> under "Ebook cover cookie".
///
/// Neither side throws on a mismatch — the cookie simply is not sent, the request
/// 401s, and the <c>(error)</c> handler on every cover <c>&lt;img&gt;</c> swaps in
/// the placeholder. That is a good failure mode and a terrible symptom to debug,
/// which is why the two literals are asserted rather than trusted.
/// </summary>
public class LibraryCoverCookieTests
{
    [Fact]
    public void CookieName_MatchesTheNameTheBrowserWrites()
    {
        ServiceConfiguration.LibraryCoverCookieName.Should().Be("annas_cover_token");
    }

    [Fact]
    public void CookiePath_MatchesThePathTheBrowserScopesItTo()
    {
        ServiceConfiguration.LibraryCoverCookiePath.Should().Be("/api/library/cover");
    }

    /// <summary>
    /// The path scope is the whole reason this is not a second ambient credential:
    /// the browser sends it to the cover route and nowhere else. A path that did
    /// not prefix the real route would either never match, or — if broadened to
    /// "/" or "/api" — quietly attach the token to every request in the app.
    /// </summary>
    [Fact]
    public void CookiePath_IsAPrefixOfTheCoverRouteAndNothingBroader()
    {
        const string coverRoute = "/api/library/cover/_covers/some-book.jpg";

        coverRoute.StartsWith(ServiceConfiguration.LibraryCoverCookiePath, StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue();

        new[] { "/", "/api", "/api/library" }
            .Should().NotContain(ServiceConfiguration.LibraryCoverCookiePath);
    }

    [Theory]
    [InlineData("/api/library/books")]
    [InlineData("/api/library/reader/epub/x")]
    [InlineData("/api/audiobooks/1/cover")]
    [InlineData("/api/auth/login")]
    public void CookiePath_DoesNotCoverOtherRoutes(string path)
    {
        path.StartsWith(ServiceConfiguration.LibraryCoverCookiePath, StringComparison.OrdinalIgnoreCase)
            .Should().BeFalse();
    }
}
