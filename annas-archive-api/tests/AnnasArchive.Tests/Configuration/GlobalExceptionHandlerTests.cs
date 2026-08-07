using System.Net;
using System.Text.Json;
using AnnasArchive.API.Configuration;
using AnnasArchive.Core.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace AnnasArchive.Tests.Configuration;

/// <summary>
/// The error contract that twenty-nine endpoint handlers now depend on.
///
/// Those handlers used to catch <see cref="ArgumentException"/> themselves and
/// answer 400. Each sat directly above a <c>catch (Exception)</c> returning 500,
/// so removing them without filtering the catch-all would have turned every one
/// of those 400s into a 500 — silently, since nothing asserted the status. These
/// tests are what make that refactor checkable rather than hopeful.
/// </summary>
public class GlobalExceptionHandlerTests
{
    /// <summary>
    /// Runs one request through a pipeline containing only the global handler and
    /// a terminal middleware that throws — the same arrangement Program.cs sets up
    /// around the endpoints, minus everything irrelevant.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string Body)> ThrowAsync(
        Exception exception,
        bool startResponseFirst = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseGlobalExceptionHandler();
        app.Run(async context =>
        {
            if (startResponseFirst)
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("data: partial\n\n");
                await context.Response.Body.FlushAsync();
            }
            throw exception;
        });

        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            using var response = await client.GetAsync("/anything");
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static JsonElement Parse(string body) => JsonDocument.Parse(body).RootElement;

    // ── the mapping the deleted catch blocks used to do by hand ──────────────

    [Fact]
    public async Task ArgumentException_Becomes400_NotThe500ItsCatchAllWouldHaveGiven()
    {
        var (status, body) = await ThrowAsync(new ArgumentException("fileName must be an epub", "fileName"));

        status.Should().Be(HttpStatusCode.BadRequest);
        Parse(body).GetProperty("errorCode").GetString().Should().Be("VALIDATION_ERROR");
    }

    /// <summary>
    /// The hand-written copies returned only <c>"Invalid parameter: fileName"</c>,
    /// discarding the exception's own sentence and the structured details the
    /// frontend's error interceptor reads for a 400.
    /// </summary>
    [Fact]
    public async Task ArgumentException_KeepsTheMessageAndParameterDetailTheCopiesDropped()
    {
        var (_, body) = await ThrowAsync(new ArgumentException("fileName must be an epub", "fileName"));
        var root = Parse(body);

        root.GetProperty("error").GetString().Should().Contain("must be an epub");
        root.GetProperty("details").GetProperty("fileName").EnumerateArray()
            .Should().NotBeEmpty();
    }

    /// <summary>
    /// Both derive from <see cref="ArgumentException"/>, so
    /// <c>when (ex is not ArgumentException)</c> excludes them too — which is what
    /// lets them reach their own better-worded arms here rather than a 500.
    /// </summary>
    [Fact]
    public async Task ArgumentNullException_Becomes400_WithItsOwnWording()
    {
        var (status, body) = await ThrowAsync(new ArgumentNullException("dropboxPath"));

        status.Should().Be(HttpStatusCode.BadRequest);
        Parse(body).GetProperty("error").GetString().Should().Contain("Missing required parameter");
    }

    [Fact]
    public async Task ArgumentOutOfRangeException_Becomes400_WithItsOwnWording()
    {
        var (status, body) = await ThrowAsync(new ArgumentOutOfRangeException("chapterId"));

        status.Should().Be(HttpStatusCode.BadRequest);
        Parse(body).GetProperty("error").GetString().Should().Contain("Invalid parameter value");
    }

    [Fact]
    public void TheFilterUsedOnTheCatchAlls_ExcludesAllThreeArgumentTypes()
    {
        // Declared as Exception on purpose: that is how the catch-alls see it, and
        // it forces a runtime type test rather than one the compiler folds away.
        //
        // Pins the premise of `when (ex is not ArgumentException)` on 29 catch-alls:
        // if this ever stopped holding, those handlers would start swallowing
        // argument failures as 500s again.
        Exception argument = new ArgumentException("x");
        Exception argumentNull = new ArgumentNullException("x");
        Exception argumentRange = new ArgumentOutOfRangeException("x");

        (argument is not ArgumentException).Should().BeFalse();
        (argumentNull is not ArgumentException).Should().BeFalse();
        (argumentRange is not ArgumentException).Should().BeFalse();

        // ...and lets everything else through to the endpoint's own catch-all.
        Exception invalidOperation = new InvalidOperationException("x");
        Exception httpRequest = new HttpRequestException("x");

        (invalidOperation is not ArgumentException).Should().BeTrue();
        (httpRequest is not ArgumentException).Should().BeTrue();
    }

    // ── the endpoints' own catch-alls still own everything else ──────────────

    [Fact]
    public async Task AnUnrecognisedException_IsStill500_AndSaysNothingAboutItself()
    {
        var (status, body) = await ThrowAsync(new InvalidOperationException("connection string is bad"));

        status.Should().Be(HttpStatusCode.InternalServerError);
        Parse(body).GetProperty("errorCode").GetString().Should().Be("INTERNAL_ERROR");
        body.Should().NotContain("connection string", "internal detail must not reach the client");
    }

    [Fact]
    public async Task TypedServiceExceptions_KeepTheirOwnStatus()
    {
        var (status, body) = await ThrowAsync(new NotFoundException("No such book"));

        status.Should().Be(HttpStatusCode.NotFound);
        Parse(body).GetProperty("errorCode").GetString().Should().Be("NOT_FOUND");
    }

    /// <summary>
    /// Why the SSE chunk-boundary handler in AiSectionSummaryEndpoints keeps its
    /// own ArgumentException catch: once the response has started, this middleware
    /// cannot change the status or write a body, so an uncaught exception there
    /// would leave the browser with a stream that simply stops.
    /// </summary>
    [Fact]
    public async Task OnceTheResponseHasStarted_TheHandlerCannotConvertItToA400()
    {
        var (status, body) = await ThrowAsync(
            new ArgumentException("bad", "chapterId"),
            startResponseFirst: true);

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("data: partial");
        body.Should().NotContain("VALIDATION_ERROR");
    }
}
