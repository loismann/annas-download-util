using System.Text.Json;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;

namespace AnnasArchive.Tests.Services.Ai;

/// <summary>
/// These numbers are what <c>TokenLimitHelpers.CheckTokenLimit</c> enforces a
/// monthly allowance against. Six call sites used to read the model's answer and
/// throw the usage block away, so their spend never reached the totals — an
/// allowance computed from an incomplete count does not fail loudly, it just
/// stops being an allowance.
/// </summary>
public class AiSpendTests
{
    private const string Account = "acct-paul";

    private readonly Mock<ITokenUsageService> _tokenUsage = new();

    [Fact]
    public void ReadsTheChatCompletionsShape()
    {
        Record("""{"usage":{"prompt_tokens":1200,"completion_tokens":340}}""", Account);

        _tokenUsage.Verify(t => t.AddUsage(Account, 1200, 340), Times.Once);
    }

    [Fact]
    public void ReadsTheResponsesApiShape()
    {
        // Same two numbers, different names. Hand-written copies read one shape
        // and silently recorded nothing for calls that came back in the other.
        Record("""{"usage":{"input_tokens":1200,"output_tokens":340}}""", Account);

        _tokenUsage.Verify(t => t.AddUsage(Account, 1200, 340), Times.Once);
    }

    [Fact]
    public void BillsBackgroundWorkToItsOwnAccount()
    {
        // Not to whoever happened to be signed in: a library scan nobody started
        // must not eat one person's allowance.
        Record("""{"usage":{"input_tokens":400,"output_tokens":120}}""", AiSpend.BackgroundAccount);

        _tokenUsage.Verify(t => t.AddUsage(AiSpend.BackgroundAccount, 400, 120), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordsNothingWithNobodyToCharge(string? billTo)
    {
        Record("""{"usage":{"prompt_tokens":1200,"completion_tokens":340}}""", billTo);

        _tokenUsage.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"usage":null}""")]
    [InlineData("""{"usage":"lots"}""")]
    [InlineData("""{"usage":{}}""")]
    [InlineData("""{"usage":{"prompt_tokens":"many","completion_tokens":"some"}}""")]
    [InlineData("""{"usage":{"prompt_tokens":0,"completion_tokens":0}}""")]
    [InlineData("""[1,2,3]""")]
    public void RecordsNothingRatherThanThrowingOnAnUnexpectedShape(string json)
    {
        // A throw here would discard an answer the account was already charged
        // for, which is strictly worse than an unrecorded call.
        var record = () => Record(json, Account);

        record.Should().NotThrow();
        _tokenUsage.VerifyNoOtherCalls();
    }

    [Fact]
    public void RecordsAHalfPresentUsageBlock()
    {
        // Better a partial figure than none: the call happened and cost money.
        Record("""{"usage":{"completion_tokens":340}}""", Account);

        _tokenUsage.Verify(t => t.AddUsage(Account, 0, 340), Times.Once);
    }

    private void Record(string json, string? billTo)
    {
        using var doc = JsonDocument.Parse(json);
        AiSpend.Record(_tokenUsage.Object, billTo, doc.RootElement);
    }
}
