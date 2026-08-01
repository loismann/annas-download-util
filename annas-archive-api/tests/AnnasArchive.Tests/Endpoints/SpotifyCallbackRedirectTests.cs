using AnnasArchive.API.Endpoints;

namespace AnnasArchive.Tests.Endpoints;

/// <summary>
/// Guards the landing URL Spotify's OAuth callback redirects the browser to.
///
/// The app previously used hash routing, and this redirect put <c>?spotify=</c>
/// before the <c>#</c>, where Angular's router cannot read it. Both outcomes then
/// looked identical from the browser: the token exchange had already succeeded or
/// failed server-side, but the page showed no confirmation and no error either
/// way. Nothing caught it because the redirect still returned a valid 302 to a URL
/// that served the app.
/// </summary>
public class SpotifyCallbackRedirectTests
{
    private const string Base = "https://ugreen-nas.example.ts.net";

    [Theory]
    [InlineData("connected")]
    [InlineData("authorization_failed")]
    [InlineData("invalid_state")]
    public void PutsTheResultInARealQueryStringNotAFragment(string result)
    {
        var redirect = SpotifyEndpoints.BuildCallbackRedirect(result, Base);

        redirect.Should().Be($"{Base}/spotifinator?spotify={result}");
    }

    [Fact]
    public void NeverEmitsAHashRoute()
    {
        // The specific regression. A '#' anywhere in this URL means the query
        // string lands in the fragment and the Spotifinator page silently loses
        // the outcome — which is exactly how the bug presented.
        var redirect = SpotifyEndpoints.BuildCallbackRedirect("connected", Base);

        redirect.Should().NotContain("#");
    }

    [Fact]
    public void SendsTheBrowserToTheSpotifinatorPageItself()
    {
        // Landing on the default route loses the result even without a '#'.
        var redirect = SpotifyEndpoints.BuildCallbackRedirect("connected", Base);

        new Uri(redirect).AbsolutePath.Should().Be("/spotifinator");
    }

    [Theory]
    [InlineData("https://host.example/", "https://host.example/spotifinator?spotify=connected")]
    [InlineData("https://host.example///", "https://host.example/spotifinator?spotify=connected")]
    [InlineData("https://host.example", "https://host.example/spotifinator?spotify=connected")]
    public void DoesNotDoubleTheSlashWhenTheConfiguredBaseUrlHasATrailingOne(string baseUrl, string expected)
    {
        SpotifyEndpoints.BuildCallbackRedirect("connected", baseUrl).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToARelativePathWhenNoFrontendBaseUrlIsConfigured(string? baseUrl)
    {
        // Local dev has no Spotify:FrontendBaseUrl; a relative redirect still
        // resolves against whatever host served the callback.
        var redirect = SpotifyEndpoints.BuildCallbackRedirect("connected", baseUrl);

        redirect.Should().Be("/spotifinator?spotify=connected");
    }

    [Fact]
    public void EscapesAResultContainingUrlSignificantCharacters()
    {
        // Spotify error codes are echoed back into this URL; an unescaped '&'
        // would split one value into two parameters.
        var redirect = SpotifyEndpoints.BuildCallbackRedirect("bad thing&more=1", Base);

        redirect.Should().Be($"{Base}/spotifinator?spotify=bad%20thing%26more%3D1");
    }
}
