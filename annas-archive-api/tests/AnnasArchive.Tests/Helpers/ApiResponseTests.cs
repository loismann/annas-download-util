using System.Text.Json;
using AnnasArchive.API.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// The error body every endpoint answers with.
///
/// <para>These assert the <em>serialised</em> response, not the method's return
/// type, because the thing that matters is the JSON the browser receives. The
/// frontend interceptor accepts a body only when it has a top-level
/// <c>error</c> whose value is a string; anything else falls through to a bare
/// "Http failure response … 400 Bad Request" and the real message is lost. That
/// is not hypothetical — a quiz endpoint answered <c>{ errors }</c> and had been
/// throwing its validation messages away.</para>
/// </summary>
public sealed class ApiResponseTests
{
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

    // ── status codes ─────────────────────────────────────────────────────

    [Fact]
    public async Task BadRequestIs400() =>
        (await Run(ApiResponse.BadRequest("nope"))).Status.Should().Be(StatusCodes.Status400BadRequest);

    [Fact]
    public async Task NotFoundIs404() =>
        (await Run(ApiResponse.NotFound("nope"))).Status.Should().Be(StatusCodes.Status404NotFound);

    /// <summary>
    /// 409, and it exists at all because 23 call sites already answered Conflict by
    /// hand while this class claimed to be the standard and had no method for it.
    /// </summary>
    [Fact]
    public async Task ConflictIs409() =>
        (await Run(ApiResponse.Conflict("nope"))).Status.Should().Be(StatusCodes.Status409Conflict);

    [Fact]
    public async Task ValidationFailedIs400() =>
        (await Run(ApiResponse.ValidationFailed("bad", ["a"]))).Status.Should().Be(StatusCodes.Status400BadRequest);

    // ── the one field the frontend actually reads ────────────────────────

    /// <summary>
    /// Every error body carries a top-level <c>error</c> holding a string. This is
    /// the whole contract; a body that fails it is silently discarded by the
    /// interceptor and the user is shown a generic HTTP failure instead.
    /// </summary>
    [Fact]
    public async Task EveryErrorBodyCarriesAStringErrorField()
    {
        IResult[] all =
        [
            ApiResponse.BadRequest("a"),
            ApiResponse.NotFound("b"),
            ApiResponse.Conflict("c"),
            ApiResponse.ValidationFailed("d", ["x", "y"]),
        ];

        foreach (var result in all)
        {
            var (_, body) = await Run(result);

            body.TryGetProperty("error", out var error).Should().BeTrue(
                "the interceptor looks for a top-level 'error' and discards the body without one");
            error.ValueKind.Should().Be(JsonValueKind.String,
                "the interceptor also requires it to be a string, not an array or object");
        }
    }

    [Theory]
    [InlineData("Invalid MD5 format.")]
    [InlineData("Braces {0} and {name} in the message")]
    [InlineData("")]
    public async Task TheMessageIsCarriedVerbatim(string message)
    {
        var (_, body) = await Run(ApiResponse.BadRequest(message));

        body.GetProperty("error").GetString().Should().Be(message);
    }

    // ── validation failures ──────────────────────────────────────────────

    /// <summary>
    /// The individual failures land under <c>details.errors</c>, matching the
    /// <c>Record&lt;string, string[]&gt;</c> shape the interceptor already reads
    /// from the global exception handler.
    /// </summary>
    [Fact]
    public async Task ValidationFailuresLandWhereTheInterceptorLooks()
    {
        var (_, body) = await Run(
            ApiResponse.ValidationFailed("Quiz subject is not valid.", ["Title required", "Needs a question"]));

        body.GetProperty("error").GetString().Should().Be("Quiz subject is not valid.");
        body.GetProperty("errorCode").GetString().Should().Be("VALIDATION_ERROR");

        var errors = body.GetProperty("details").GetProperty("errors");
        errors.ValueKind.Should().Be(JsonValueKind.Array);
        errors.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Title required", "Needs a question");
    }

    /// <summary>
    /// The summary sentence survives even when the list is empty, so a caller that
    /// only knows the standard shape still shows something useful.
    /// </summary>
    [Fact]
    public async Task AValidationFailureWithNoListedErrorsStillCarriesASummary()
    {
        var (_, body) = await Run(ApiResponse.ValidationFailed("Not valid.", []));

        body.GetProperty("error").GetString().Should().Be("Not valid.");
        body.GetProperty("details").GetProperty("errors").GetArrayLength().Should().Be(0);
    }

    /// <summary>The source is enumerated once — a lazy query must not be re-run.</summary>
    [Fact]
    public async Task TheErrorSourceIsEnumeratedOnlyOnce()
    {
        var enumerations = 0;

        IEnumerable<string> CountingErrors()
        {
            enumerations++;
            yield return "only once";
        }

        await Run(ApiResponse.ValidationFailed("bad", CountingErrors()));

        enumerations.Should().Be(1);
    }
}
