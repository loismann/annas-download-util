using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq.Protected;

namespace AnnasArchive.Tests.Services.Ai;

/// <summary>
/// Five endpoints share this one round trip, so the two failures it exists to
/// prevent are worth pinning: a failure message that hides the provider's own
/// reason, and a token-usage read that throws after the account was already
/// billed. Both were live in the hand-written copies this replaced.
/// </summary>
public class AiChatCompletionTests
{
    private const string UserId = "acct-abc123";

    private readonly Mock<ITokenUsageService> _tokenUsage = new();

    // ─── Success ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReturnsTheModelsText()
    {
        var outcome = await Complete(Ok(Completion("Here is your answer.")));

        outcome.Succeeded.Should().BeTrue();
        outcome.Failure.Should().BeNull();
        outcome.Text.Should().Be("Here is your answer.");
    }

    [Fact]
    public async Task ChargesTheTokensToTheSignedInUser()
    {
        await Complete(Ok(Completion("answer", promptTokens: 120, completionTokens: 45)));

        _tokenUsage.Verify(t => t.AddUsage(UserId, 120, 45), Times.Once);
    }

    [Fact]
    public async Task ChargesNobodyWhenNobodyIsSignedIn()
    {
        await Complete(Ok(Completion("answer", promptTokens: 120, completionTokens: 45)), anonymous: true);

        _tokenUsage.Verify(t => t.AddUsage(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task StillReturnsTheAnswerWhenTheUsageBlockIsMalformed()
    {
        // The bug this replaces: every copy read usage.prompt_tokens without
        // checking it was there, so an unexpected shape threw *after* OpenAI
        // had answered and the account had been billed — turning a good answer
        // into a 500. A missing usage number is a wrong figure in a report; a
        // throw here loses work already paid for.
        var body = """
        {"choices":[{"message":{"content":"answer"}}],"usage":{"total_tokens":165}}
        """;

        var outcome = await Complete(Ok(body));

        outcome.Succeeded.Should().BeTrue();
        outcome.Text.Should().Be("answer");
        _tokenUsage.Verify(t => t.AddUsage(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task StillReturnsTheAnswerWhenThereIsNoUsageBlockAtAll()
    {
        var outcome = await Complete(Ok("""{"choices":[{"message":{"content":"answer"}}]}"""));

        outcome.Succeeded.Should().BeTrue();
        outcome.Text.Should().Be("answer");
    }

    // ─── Failure ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SurfacesTheProvidersOwnReasonForFailing()
    {
        // The sentence that names the actual problem, and the fix, is the one
        // OpenAI wrote. Copies that returned "OpenAI request failed: 429"
        // logged this and showed the browser a status code.
        var body = """
        {"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota"}}
        """;

        var outcome = await Complete(Fail(HttpStatusCode.TooManyRequests, body));

        outcome.Succeeded.Should().BeFalse();
        outcome.Text.Should().BeNull();
        (await ProblemDetail(outcome)).Should().Contain("exceeded your current quota");
    }

    [Fact]
    public async Task FallsBackToAStatusSentenceWhenTheBodySaysNothing()
    {
        var outcome = await Complete(Fail(HttpStatusCode.Unauthorized, "<html>gateway error</html>"));

        (await ProblemDetail(outcome)).Should().Contain("rejected the configured API key");
    }

    [Fact]
    public async Task DoesNotBillForAFailedCall()
    {
        await Complete(Fail(HttpStatusCode.BadRequest, """{"error":{"message":"context_length_exceeded"}}"""));

        _tokenUsage.Verify(t => t.AddUsage(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    // ─── Request shape ───────────────────────────────────────────────────

    [Fact]
    public async Task SendsBothPromptsInOrder()
    {
        var handler = Ok(Completion("answer"));

        await Complete(handler);

        var sent = await CapturedBody(handler);
        sent.Should().Contain("\"role\":\"system\"").And.Contain("You are a test.");
        sent.Should().Contain("\"role\":\"user\"").And.Contain("Do the thing.");
        sent.IndexOf("You are a test.", StringComparison.Ordinal)
            .Should().BeLessThan(sent.IndexOf("Do the thing.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbandonsTheCallWhenTheCallerCancels()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage();
            });

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => Complete(handler, cancellationToken: cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string Completion(string content, int? promptTokens = null, int? completionTokens = null)
    {
        var usage = promptTokens is null && completionTokens is null
            ? null
            : new { prompt_tokens = promptTokens ?? 0, completion_tokens = completionTokens ?? 0 };

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
            usage
        });
    }

    private static Mock<HttpMessageHandler> Ok(string body) => Responding(HttpStatusCode.OK, body);

    private static Mock<HttpMessageHandler> Fail(HttpStatusCode status, string body) => Responding(status, body);

    private static Mock<HttpMessageHandler> Responding(HttpStatusCode status, string body)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = status,
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        return handler;
    }

    private static async Task<string> CapturedBody(Mock<HttpMessageHandler> handler)
    {
        var request = handler.Invocations
            .Single(i => i.Method.Name == "SendAsync")
            .Arguments[0] as HttpRequestMessage;

        return await request!.Content!.ReadAsStringAsync();
    }

    private static async Task<string> ProblemDetail(AiChatOutcome outcome)
    {
        // Results.Problem writes ProblemDetails to the response body; reading it
        // back is the only way to assert what the browser is actually told.
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton<Microsoft.AspNetCore.Http.IProblemDetailsService, StubProblemDetailsService>()
            .BuildServiceProvider();

        await outcome.Failure!.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private Task<AiChatOutcome> Complete(
        Mock<HttpMessageHandler> handler,
        bool anonymous = false,
        CancellationToken cancellationToken = default)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object));

        var service = new AiChatCompletion(
            factory.Object,
            new OpenAiModelHelper(),
            new AiResponseParser(),
            _tokenUsage.Object);

        var context = new DefaultHttpContext();
        if (!anonymous)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "test"));
        }

        return service.CompleteAsync(
            new AiChatCall(
                Endpoint: "test",
                Model: "gpt-4o",
                SystemPrompt: "You are a test.",
                UserPrompt: "Do the thing.",
                MaxCompletionTokens: 100,
                Temperature: 0.3),
            context,
            cancellationToken);
    }

    /// <summary>ProblemDetails needs a service registered; this one just writes
    /// the detail text so the assertion has something to read.</summary>
    private sealed class StubProblemDetailsService : Microsoft.AspNetCore.Http.IProblemDetailsService
    {
        public async ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            await context.HttpContext.Response.WriteAsync(context.ProblemDetails.Detail ?? "");
            return true;
        }

        public ValueTask WriteAsync(ProblemDetailsContext context) => new(TryWriteAsync(context).AsTask());
    }
}
