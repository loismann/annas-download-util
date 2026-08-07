using AnnasArchive.API.Helpers;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// What the reader is told when an AI call fails. The endpoints used to forward
/// <c>ex.Message</c>, so the browser showed Polly's internals while the sentence
/// OpenAI returned went only to the log.
/// </summary>
public class AiFailureMessageTests
{
    [Fact]
    public void PrefersTheProvidersOwnExplanation()
    {
        var body = """
            {"error":{"message":"You have no credits remaining.","type":"insufficient_quota"}}
            """;

        AiFailureMessage.ForResponse(HttpStatusCode.TooManyRequests, body)
            .Should().Be("You have no credits remaining.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("""{"error":"a bare string, not an object"}""")]
    [InlineData("""{"detail":"no error property at all"}""")]
    public void FallsBackToTheStatusWhenTheBodyCarriesNoMessage(string? body)
    {
        AiFailureMessage.ForResponse(HttpStatusCode.TooManyRequests, body)
            .Should().Be("The AI service is rate limiting requests. Try again shortly.");
    }

    [Fact]
    public void NamesAnUnrecognisedStatusRatherThanSayingNothing()
    {
        AiFailureMessage.ForResponse(HttpStatusCode.InternalServerError, null)
            .Should().Be("The AI service returned HTTP 500.");
    }

    [Fact]
    public void TranslatesATrippedCircuitIntoTheCauseAndTheFix()
    {
        // The whole point: "the circuit is now open and is not allowing calls" told
        // the reader neither what went wrong nor what to do about it.
        var message = AiFailureMessage.ForException(new BrokenCircuitException());

        message.Should().NotContain("circuit");
        message.Should().Contain("credit balance");
    }

    [Fact]
    public void CarriesThroughAReasonThatWasAlreadyPhrasedForTheUser()
    {
        var message = AiFailureMessage.ForException(
            new AiServiceException("You have no credits remaining."));

        message.Should().Be("You have no credits remaining.");
    }

    [Theory]
    [InlineData(typeof(TimeoutRejectedException))]
    [InlineData(typeof(TaskCanceledException))]
    public void ReportsATimeoutAsATimeout(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        AiFailureMessage.ForException(ex).Should().Be("The AI service took too long to respond.");
    }

    [Fact]
    public void FallsBackToTheGenericMessageForAnythingUnrecognised()
    {
        AiFailureMessage.ForException(new InvalidOperationException("internal detail"))
            .Should().Be(AiFailureMessage.Generic);
    }

    [Fact]
    public void NeverLeaksAnInternalExceptionMessage()
    {
        AiFailureMessage.ForException(new InvalidOperationException("connection string: secret"))
            .Should().NotContain("secret");
    }
}
