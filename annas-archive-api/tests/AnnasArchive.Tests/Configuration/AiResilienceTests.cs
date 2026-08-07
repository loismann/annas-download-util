using AnnasArchive.API.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;

namespace AnnasArchive.Tests.Configuration;

/// <summary>
/// OpenAI returns 429 for both a real rate limit and an exhausted credit balance.
/// Retrying the second one cannot succeed, and the retries were enough to trip the
/// shared circuit breaker — so a billing problem reached the reader as "the circuit
/// is now open and is not allowing calls", 60 seconds after the fact, with every
/// other AI feature broken alongside it.
/// </summary>
public class AiResilienceTests
{
    private const string QuotaBody = """
        {"error":{"message":"You have no credits remaining.","type":"insufficient_quota","code":"credit_balance_exhausted"}}
        """;

    private const string RateLimitBody = """
        {"error":{"message":"Rate limit reached for gpt-5.","type":"requests","code":"rate_limit_exceeded"}}
        """;

    [Fact]
    public async Task DoesNotRetryA429ThatMeansTheCreditBalanceIsExhausted()
    {
        var (client, stub) = BuildClient(HttpStatusCode.TooManyRequests, QuotaBody);

        var response = await client.GetAsync("https://api.openai.com/v1/responses");

        stub.Attempts.Should().Be(1, "no number of retries produces credits");
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task LeavesTheBodyReadableAfterTheRetryPredicateInspectsIt()
    {
        // The predicate reads the body to classify the 429; the endpoint then reads
        // it again to build the message shown to the reader. Buffering is what makes
        // the second read work.
        var (client, _) = BuildClient(HttpStatusCode.TooManyRequests, QuotaBody);

        var response = await client.GetAsync("https://api.openai.com/v1/responses");

        (await response.Content.ReadAsStringAsync()).Should().Contain("no credits remaining");
    }

    [Fact]
    public async Task KeepsTheCircuitClosedAcrossRepeatedQuotaFailures()
    {
        // The breaker opens on 3+ handled failures at a 50% ratio. Quota 429s are not
        // failures it can do anything about, and letting them count took flashcards,
        // quiz and vocab down with the chapter summary that hit the limit.
        var (client, stub) = BuildClient(HttpStatusCode.TooManyRequests, QuotaBody);

        for (var i = 0; i < 6; i++)
        {
            var act = async () => await client.GetAsync("https://api.openai.com/v1/responses");
            await act.Should().NotThrowAsync<BrokenCircuitException>();
        }

        stub.Attempts.Should().Be(6, "every call should have reached OpenAI");
    }

    [Fact]
    public async Task StillRetriesA429ThatIsAGenuineRateLimit()
    {
        // The guard against over-correcting: a rate limit does clear on its own, so
        // backing off and trying again is still the right response to it.
        var (client, stub) = BuildClient(HttpStatusCode.TooManyRequests, RateLimitBody);

        await client.GetAsync("https://api.openai.com/v1/responses");

        stub.Attempts.Should().Be(3, "the initial call plus two configured retries");
    }

    [Fact]
    public async Task DoesNotRetryASuccessfulCall()
    {
        var (client, stub) = BuildClient(HttpStatusCode.OK, "{}");

        await client.GetAsync("https://api.openai.com/v1/responses");

        stub.Attempts.Should().Be(1);
    }

    private static (HttpClient Client, CountingHandler Stub) BuildClient(
        HttpStatusCode status, string body)
    {
        var stub = new CountingHandler(status, body);

        var services = new ServiceCollection();
        services.AddHttpClient("ai")
            .AddAiResilience("ai")
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        var client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("ai");

        return (client, stub);
    }

    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body)
            });
        }
    }
}
