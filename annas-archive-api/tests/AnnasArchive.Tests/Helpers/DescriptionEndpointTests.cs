using System.Text.Json;
using AnnasArchive.API.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// The shape shared by all four description endpoints (Google Books,
/// OpenLibrary, Wikipedia, GPT-4), which were four separate copies until they
/// were collapsed into one.
/// </summary>
public sealed class DescriptionEndpointTests
{
    /// <summary>Runs an <see cref="IResult"/> the way the pipeline would and hands
    /// back what the caller would actually receive.</summary>
    private static async Task<(int Status, JsonElement Body)> Run(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        await using var stream = new MemoryStream();
        context.Response.Body = stream;

        await result.ExecuteAsync(context);

        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream);
        return (context.Response.StatusCode, doc.RootElement.Clone());
    }

    private static Func<string, string?, Task<string?>> Returns(string? description) =>
        (_, _) => Task.FromResult(description);

    private static Task<IResult> Fetch(string? title, string? author = null, string? description = "A book.") =>
        DescriptionEndpoint.FetchAsync("TestSource", title, author, Returns(description));

    // ── A description came back ──────────────────────────────────────────

    [Fact]
    public async Task AFoundDescriptionIsReturnedVerbatim()
    {
        var (status, body) = await Run(await Fetch("Dune", "Frank Herbert", "Spice."));

        status.Should().Be(StatusCodes.Status200OK);
        body.GetProperty("description").GetString().Should().Be("Spice.");
    }

    /// <summary>
    /// A source with nothing to say is a normal answer, not a failure — the
    /// caller just tries the next source. A 404 here would force the frontend to
    /// special-case "missing" per source and would fill the error logs with
    /// ordinary misses.
    /// </summary>
    [Fact]
    public async Task ASourceWithNoDescriptionStillAnswers200()
    {
        var (status, body) = await Run(await Fetch("Dune", description: null));

        status.Should().Be(StatusCodes.Status200OK);
        body.GetProperty("description").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// The AI source signals "nothing" with an empty string rather than null. The
    /// body must carry that through unchanged — only the log line treats blank as
    /// a miss.
    /// </summary>
    [Fact]
    public async Task AnEmptyDescriptionIsPassedThroughRatherThanNormalised()
    {
        var (status, body) = await Run(await Fetch("Dune", description: ""));

        status.Should().Be(StatusCodes.Status200OK);
        body.GetProperty("description").GetString().Should().BeEmpty();
    }

    // ── A title is required ──────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ABlankTitleIsRejected(string? title)
    {
        var (status, body) = await Run(await Fetch(title));

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.GetProperty("error").GetString().Should().Be("title is required.");
    }

    /// <summary>
    /// The guard has to come before the lookup, not after. Every one of these
    /// sources is a remote call and one of them is billed, so a malformed request
    /// must not reach it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankTitleNeverCostsAnUpstreamCall(string? title)
    {
        var asked = false;

        await DescriptionEndpoint.FetchAsync("TestSource", title, "Frank Herbert",
            (_, _) => { asked = true; return Task.FromResult<string?>("Spice."); });

        asked.Should().BeFalse("a request that was rejected must not have been sent anywhere");
    }

    // ── What the source is asked ─────────────────────────────────────────

    /// <summary>
    /// The title reaches the source exactly as received. Trimming or normalising
    /// it here would silently change the search a source performs.
    /// </summary>
    [Fact]
    public async Task TheTitleReachesTheSourceUnchanged()
    {
        string? seen = null;

        await DescriptionEndpoint.FetchAsync("TestSource", "  Dune  ", null,
            (t, _) => { seen = t; return Task.FromResult<string?>(null); });

        seen.Should().Be("  Dune  ");
    }

    /// <summary>
    /// The author is passed through raw, including null. The four sources
    /// disagree about whether they accept a null author — two require a non-null
    /// string, two do not — so coercing it here would impose one source's
    /// contract on the other three. Each caller adapts its own.
    /// </summary>
    [Theory]
    [InlineData("Frank Herbert")]
    [InlineData("")]
    [InlineData(null)]
    public async Task TheAuthorIsPassedThroughRawIncludingNull(string? author)
    {
        string? seen = "sentinel";

        await DescriptionEndpoint.FetchAsync("TestSource", "Dune", author,
            (_, a) => { seen = a; return Task.FromResult<string?>(null); });

        seen.Should().Be(author);
    }

    // ── The defect all four copies had ───────────────────────────────────

    /// <summary>
    /// All four copies logged their outcome by interpolating the title into the
    /// Serilog message template. A title containing braces is then parsed as a
    /// placeholder, and the line is mangled or dropped — and scraped titles do
    /// contain braces.
    ///
    /// <para>This asserts the handler survives such a title and still answers
    /// correctly. It cannot assert on the rendered log line without swapping the
    /// global <c>Log.Logger</c>, which would be unsafe in a parallel suite; the
    /// template itself is guarded by
    /// <c>ErrorContractConventionTests.NoLogCallBuildsItsTemplateByInterpolation</c>,
    /// which reads the source across every endpoint.</para>
    /// </summary>
    [Theory]
    [InlineData("The {0} Problem")]
    [InlineData("Braces {Title} in the name")]
    [InlineData("A }{ mess")]
    [InlineData("100% {complete}")]
    public async Task ATitleContainingBracesIsHandledNormally(string title)
    {
        var (status, body) = await Run(await Fetch(title, description: "Fine."));

        status.Should().Be(StatusCodes.Status200OK);
        body.GetProperty("description").GetString().Should().Be("Fine.");
    }

    /// <summary>A source name with braces is equally hostile to the template.</summary>
    [Fact]
    public async Task ASourceNameContainingBracesIsHandledNormally()
    {
        var result = await DescriptionEndpoint.FetchAsync("{Weird} Source", "Dune", null, Returns("Fine."));
        var (status, _) = await Run(result);

        status.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// A source that throws is not caught here. These endpoints have no local
    /// try/catch by design — the failure belongs to the global exception handler,
    /// which owns the one error contract. Swallowing it here would answer 200
    /// with a null description and make an outage look like a book nobody has
    /// written about.
    /// </summary>
    [Fact]
    public async Task AFailingSourceIsNotSwallowed()
    {
        var act = async () => await DescriptionEndpoint.FetchAsync("TestSource", "Dune", null,
            (_, _) => throw new HttpRequestException("upstream is down"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
