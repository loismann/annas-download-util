using System.Text.Json;
using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Services.Ai;

/// <summary>
/// Records what an OpenAI call actually cost, from whichever response shape it
/// came back in.
///
/// This exists because the numbers were being dropped. Six call sites read the
/// model's answer and threw the <c>usage</c> block away, so their spend never
/// reached the totals — and those totals are what
/// <c>TokenLimitHelpers.CheckTokenLimit</c> enforces a monthly allowance
/// against. An allowance computed from an incomplete count does not fail
/// loudly; it just stops being an allowance.
///
/// The two API shapes name the same two numbers differently, which is most of
/// why hand-written copies got it wrong: Chat Completions says
/// <c>prompt_tokens</c>/<c>completion_tokens</c>, the Responses API says
/// <c>input_tokens</c>/<c>output_tokens</c>.
/// </summary>
public static class AiSpend
{
    /// <summary>
    /// Where work nobody asked for is billed. Background enrichment is real
    /// money spent on the household's behalf, and attributing it to whoever
    /// happened to be signed in would be worse than not attributing it — it
    /// would consume one person's allowance for a scan they did not start.
    /// </summary>
    public const string BackgroundAccount = "system-background";

    public const string BackgroundDisplayName = "Background jobs";

    /// <summary>
    /// Charges <paramref name="billTo"/> for the call described by
    /// <paramref name="responseRoot"/>. A null or blank account, or a response
    /// carrying no usable numbers, records nothing.
    /// </summary>
    /// <remarks>
    /// Every field is probed rather than demanded. An unbilled call is a wrong
    /// figure in a report; a throw here would discard an answer the account was
    /// already charged for, which is strictly worse.
    /// </remarks>
    public static void Record(ITokenUsageService tokenUsage, string? billTo, JsonElement responseRoot)
    {
        if (string.IsNullOrWhiteSpace(billTo)) return;
        if (responseRoot.ValueKind != JsonValueKind.Object) return;
        if (!responseRoot.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;

        var promptTokens = Count(usage, "prompt_tokens", "input_tokens");
        var completionTokens = Count(usage, "completion_tokens", "output_tokens");
        if (promptTokens == 0 && completionTokens == 0) return;

        tokenUsage.AddUsage(billTo, promptTokens, completionTokens);
    }

    private static int Count(JsonElement usage, params string[] names)
    {
        foreach (var name in names)
        {
            if (usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return value.GetInt32();
        }

        return 0;
    }
}
