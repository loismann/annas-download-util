using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Reader2.Ai;

/// <summary>An AI call that failed, in words a reader can be shown.</summary>
public sealed class ReaderAiException(string message) : Exception(message);

/// <summary>
/// Builds and sends every model call Reader II makes.
///
/// <para>Building and sending are one job here rather than two, because the
/// thing worth guaranteeing is that they always happen together: the budget, the
/// model tier, the failure wording, and the token accounting are attached in one
/// place, so no caller can assemble a call and forget one of them.</para>
///
/// <para>This is the join the spec insists on — <b>a prompt and its budget travel
/// together through the call, but the number lives in config.</b> A lens class
/// holds no token counts, so tuning a deployment never edits reviewed prompt
/// text.</para>
///
/// <para>Book text is always the user prompt and never the system prompt. Not a
/// style preference: folding a passage into the instructions lets the book issue
/// instructions, and makes the prompt unpinnable by a golden.</para>
/// </summary>
public sealed class ModelCalls(Reader2Options options, IModelSelectionService models, IAiChatCompletion completions)
{
    /// <summary>Sends a call using a lens's wording for this kind.</summary>
    /// <exception cref="InvalidOperationException">
    /// The lens has no wording for this kind. The registry rejects that at boot
    /// for required kinds, so reaching here means asking a lens that builds no
    /// story model to extract one.
    /// </exception>
    public Task<Produced<Prose>> AskLensAsync(
        ReaderContext ctx, CallKind kind, string bookText, CancellationToken ct) =>
        SendAsync(
            Build(kind,
                ctx.Lens.Prompts[kind] ?? throw new InvalidOperationException(
                    $"Lens '{ctx.Lens.Key}' supplies no {kind} prompt."),
                bookText),
            ctx, text => new Prose(text), ct);

    /// <summary>Sends a call for a kind no lens owns — chapter labels today.</summary>
    public Task<Produced<T>> AskSharedAsync<T>(
        ReaderContext ctx, CallKind kind, string systemPrompt, string userContent,
        Func<string, T> interpret, CancellationToken ct) =>
        SendAsync(Build(kind, systemPrompt, userContent), ctx, interpret, ct);

    /// <summary>
    /// The one place a model is actually called, and the one place a failed call
    /// becomes a sentence a reader can be shown rather than a stack trace.
    /// </summary>
    private async Task<Produced<T>> SendAsync<T>(
        AiChatCall call, ReaderContext ctx, Func<string, T> interpret, CancellationToken ct)
    {
        var outcome = await completions.CompleteAsync(call, ctx.Http, ct);

        if (!outcome.Succeeded || string.IsNullOrWhiteSpace(outcome.Text))
            throw new ReaderAiException($"The {call.Endpoint} call did not come back with anything usable.");

        return new Produced<T>(
            interpret(outcome.Text.Trim()), call.Model,
            outcome.Usage.PromptTokens, outcome.Usage.CompletionTokens);
    }

    private AiChatCall Build(CallKind kind, string systemPrompt, string userContent)
    {
        var budget = options[kind];

        return new AiChatCall(
            Endpoint: EndpointName(kind),
            Model: budget.Model == ModelTier.Deep ? models.GetModelDeep() : models.GetModelFast(),
            SystemPrompt: systemPrompt,
            UserPrompt: userContent,
            MaxCompletionTokens: budget.MaxCompletionTokens,
            Temperature: budget.Temperature,
            ReasoningEffort: budget.ReasoningEffort);
    }

    /// <summary>
    /// What this call is called in the usage log, derived rather than listed so a
    /// new kind cannot arrive un-named or share another kind's line in the spend
    /// breakdown.
    /// </summary>
    public static string EndpointName(CallKind kind) =>
        "reader2-" + string.Concat(kind.ToString()
            .Select((c, i) => char.IsUpper(c) && i > 0
                ? "-" + char.ToLowerInvariant(c)
                : char.ToLowerInvariant(c).ToString()));
}
