using AnnasArchive.API.Services.Ai;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// AI-powered book description generation.
/// </summary>
public static class AiDescriptionHelpers
{
    /// <summary>
    /// Generates a brief, spoiler-free description for a book. Returns an empty
    /// string if the model could not be reached — a missing description is a
    /// gap on a page, not a reason to fail whatever asked for it.
    /// </summary>
    /// <param name="billTo">
    /// Owner key to charge, or <see cref="AiSpend.BackgroundAccount"/> for work
    /// nobody requested. This used to have no parameter at all: the helper
    /// discarded the response's <c>usage</c> block, so one caller
    /// (<c>AnnaDownloadEndpoints</c>) hardcoded <c>AddUsage(userId, 150, 50)</c>
    /// as an estimate — charged at a flat rate whether the call returned three
    /// words or failed outright — and the other three callers recorded nothing.
    /// </param>
    public static async Task<string> GenerateNoSpoilerDescriptionAsync(
        string title,
        string author,
        string model,
        IAiChatCompletion chat,
        string? billTo,
        CancellationToken cancellationToken = default)
    {
        var outcome = await chat.CompleteAsync(
            new AiChatCall(
                Endpoint: "book-description",
                Model: model,
                SystemPrompt: "You are a literary assistant. Generate brief, spoiler-free book descriptions.",
                UserPrompt: $@"Generate a single-sentence, no-spoiler description (max 15 words) for:
""{title}"" by {author}

Focus on genre, themes, and general premise without revealing plot details or twists.",
                MaxCompletionTokens: 50,
                Temperature: 0.5),
            billTo,
            cancellationToken);

        if (!outcome.Succeeded || string.IsNullOrWhiteSpace(outcome.Text))
            return "";

        return Unquote(outcome.Text.Trim());
    }

    /// <summary>Models wrap a one-line answer in quotes about half the time,
    /// and the quotes end up rendered on the book card.</summary>
    private static string Unquote(string description) =>
        description.Length > 1 && description.StartsWith('"') && description.EndsWith('"')
            ? description[1..^1]
            : description;
}
