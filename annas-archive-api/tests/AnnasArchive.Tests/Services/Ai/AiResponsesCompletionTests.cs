using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Http;
using Moq.Protected;

namespace AnnasArchive.Tests.Services.Ai;

/// <summary>
/// The Responses API is a different API, not a different spelling: <c>input</c>
/// rather than <c>messages</c>, <c>max_output_tokens</c> rather than
/// <c>max_completion_tokens</c>, and <c>input_tokens</c>/<c>output_tokens</c>
/// rather than <c>prompt_tokens</c>/<c>completion_tokens</c>. Eight call sites
/// wrote this out by hand and every one of them read the usage numbers without
/// checking they were there — including the three inside chapter summarisation,
/// the most expensive path in the app.
/// </summary>
public class AiResponsesCompletionTests
{
    private const string UserId = "acct-paul";

    private readonly Mock<ITokenUsageService> _tokenUsage = new();

    // ─── Payload shape ───────────────────────────────────────────────────

    [Fact]
    public async Task SendsAPlainStringInputWhenThereIsNoSystemPrompt()
    {
        var handler = Ok(Response("answer"));

        await Complete(handler, Call());

        var sent = JsonDocument.Parse(await CapturedBody(handler)).RootElement;
        sent.GetProperty("input").ValueKind.Should().Be(JsonValueKind.String);
        sent.GetProperty("input").GetString().Should().Be("Summarise this.");
        sent.GetProperty("max_output_tokens").GetInt32().Should().Be(500);
    }

    [Fact]
    public async Task SendsARoleArrayWhenThereIsASystemPrompt()
    {
        // Both forms are in use and the difference is not cosmetic — the array
        // is what separates instructions from the user's own text.
        var handler = Ok(Response("answer"));

        await Complete(handler, Call() with { SystemPrompt = "You are a librarian." });

        var input = JsonDocument.Parse(await CapturedBody(handler)).RootElement.GetProperty("input");
        input.ValueKind.Should().Be(JsonValueKind.Array);
        input[0].GetProperty("role").GetString().Should().Be("system");
        input[0].GetProperty("content").GetString().Should().Be("You are a librarian.");
        input[1].GetProperty("role").GetString().Should().Be("user");
        input[1].GetProperty("content").GetString().Should().Be("Summarise this.");
    }

    [Fact]
    public async Task OmitsTemperatureAndReasoningWhenTheCallerSetNeither()
    {
        // The reasoning models reject a temperature they did not ask for, so
        // "not set" and "set to a default" are different requests.
        var handler = Ok(Response("answer"));

        await Complete(handler, Call());

        var sent = JsonDocument.Parse(await CapturedBody(handler)).RootElement;
        sent.TryGetProperty("temperature", out _).Should().BeFalse();
        sent.TryGetProperty("reasoning", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SendsReasoningEffortWhenTuned()
    {
        // Per-stage effort is most of how a chapter summary is made affordable.
        var handler = Ok(Response("answer"));

        await Complete(handler, Call() with { ReasoningEffort = "low", Temperature = 0.3 });

        var sent = JsonDocument.Parse(await CapturedBody(handler)).RootElement;
        sent.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        sent.GetProperty("temperature").GetDouble().Should().Be(0.3);
    }

    [Fact]
    public async Task OmitsAnEffortSetToBlank()
    {
        // cfg.GetValue<string> returns null for an unset key, and the config
        // does not define an effort for every stage.
        var handler = Ok(Response("answer"));

        await Complete(handler, Call() with { ReasoningEffort = "  " });

        JsonDocument.Parse(await CapturedBody(handler)).RootElement
            .TryGetProperty("reasoning", out _).Should().BeFalse();
    }

    // ─── Usage ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReportsWhatTheCallCostSoCallersCanAggregate()
    {
        // The three-tier summary sums these across ~20 calls to show one figure.
        var outcome = await Complete(Ok(Response("answer", inputTokens: 8_000, outputTokens: 1_200)), Call());

        outcome.Usage.PromptTokens.Should().Be(8_000);
        outcome.Usage.CompletionTokens.Should().Be(1_200);
        _tokenUsage.Verify(t => t.AddUsage(UserId, 8_000, 1_200), Times.Once);
    }

    [Fact]
    public async Task StillReturnsTheSummaryWhenTheUsageBlockIsMalformed()
    {
        // The bug in all eight copies. A summary that reached this point has
        // already been paid for; throwing here loses it and returns a 500.
        var outcome = await Complete(
            Ok(WithUsageBlock(Response("answer"), """{"total_tokens":9200}""")), Call());

        outcome.Succeeded.Should().BeTrue();
        outcome.Text.Should().Be("answer");
        outcome.Usage.Should().Be(AiUsage.None);
        _tokenUsage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BillsBackgroundWorkToTheHouseholdAccount()
    {
        await Complete(
            Ok(Response("answer", inputTokens: 400, outputTokens: 100)),
            Call(),
            billTo: AiSpend.BackgroundAccount);

        _tokenUsage.Verify(t => t.AddUsage(AiSpend.BackgroundAccount, 400, 100), Times.Once);
    }

    // ─── Failure ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CarriesTheProvidersReasonOnBothChannels()
    {
        // Callers inside an SSE stream cannot return an IResult — they throw
        // AiServiceException — so the sentence has to be reachable as a string.
        var body = """{"error":{"message":"You have no credits remaining."}}""";

        var outcome = await Complete(Fail(HttpStatusCode.TooManyRequests, body), Call());

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().NotBeNull();
        outcome.FailureMessage.Should().Be("You have no credits remaining.");
        outcome.Usage.Should().Be(AiUsage.None);
    }

    [Fact]
    public async Task DoesNotBillForAFailedCall()
    {
        await Complete(Fail(HttpStatusCode.BadRequest, """{"error":{"message":"too long"}}"""), Call());

        _tokenUsage.VerifyNoOtherCalls();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static AiResponsesCall Call() => new(
        Endpoint: "test",
        Model: "gpt-5",
        Input: "Summarise this.",
        MaxOutputTokens: 500);

    private static string Response(string text, int? inputTokens = null, int? outputTokens = null)
    {
        var usage = inputTokens is null && outputTokens is null
            ? null
            : new { input_tokens = inputTokens ?? 0, output_tokens = outputTokens ?? 0 };

        // The real Responses shape: the output array carries reasoning items
        // alongside the message, so both discriminators are load-bearing.
        return JsonSerializer.Serialize(new
        {
            output = new object[]
            {
                new { type = "reasoning" },
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text } }
                }
            },
            usage
        });
    }

    /// <summary>Swaps in a usage block the parser cannot read, keeping the rest
    /// of the response valid.</summary>
    private static string WithUsageBlock(string response, string usageJson)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(response)!.AsObject();
        node["usage"] = System.Text.Json.Nodes.JsonNode.Parse(usageJson);
        return node.ToJsonString();
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

    private Task<AiResponsesOutcome> Complete(
        Mock<HttpMessageHandler> handler,
        AiResponsesCall call,
        string? billTo = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object));

        var service = new AiResponsesCompletion(factory.Object, new AiResponseParser(), _tokenUsage.Object);

        if (billTo is not null)
            return service.CompleteAsync(call, billTo);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "test"))
        };

        return service.CompleteAsync(call, context);
    }
}
