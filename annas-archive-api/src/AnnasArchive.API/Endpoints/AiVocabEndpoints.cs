using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping AI Vocabulary endpoints.
/// </summary>
public static class AiVocabEndpoints
{
    /// <summary>
    /// Maps AI Vocabulary endpoints to the application.
    /// </summary>
    public static WebApplication MapAiVocabEndpoints(this WebApplication app)
    {
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/ai")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/vocab/learn-more - Get detailed info about a vocab term
        group.MapPost("/vocab/learn-more", HandleLearnMore);

        // POST /api/ai/section-vocab - Save section vocabulary to cache
        group.MapPost("/section-vocab", HandleSaveSectionVocab);

        return app;
    }

    private static async Task<IResult> HandleLearnMore(
        HttpContext context,
        [FromBody] LearnMoreRequest request,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IModelSelectionService modelSelection,
        IAiResponsesCompletion ai)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Term))
            return Results.BadRequest(new { error = "Term is required." });

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            var model = modelSelection.GetModelDeep();

            var contextParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.BookTitle))
                contextParts.Add($"Book: {request.BookTitle}");
            if (!string.IsNullOrWhiteSpace(request.DropboxPath))
                contextParts.Add($"Source path: {request.DropboxPath}");

            var prompt = $@"Provide a rich, scholarly 300-400 word deep dive on the term/phrase ""{request.Term}"" that goes beyond dictionary definitions.

Respond as concise HTML with paragraphs, <ul>, <strong>, and include up to 2-3 reliable image URLs and 1-2 reference links (e.g., Wikipedia) that help explain the term.

**Your analysis should explore:**
- Core meaning and etymology
- Historical development and evolution of the concept
- How this term/concept is understood in different academic disciplines (philosophy, literature, sociology, etc.)
- Key thinkers, works, or movements associated with it
- How it appears in popular culture vs. academic discourse
- Common misconceptions or debates surrounding the term
- Relevance to contemporary discussions or current events (if applicable)
- Interesting facts or notable usage examples

IMAGE RULES (strict):
- Prefer upload.wikimedia.org or commons.wikimedia.org images; use fully-qualified HTTPS URLs with underscores instead of spaces.
- Do NOT include images unless you are confident the URL exists and is directly fetchable (ending in .jpg/.png/.jpeg).
- If unsure about an image URL, skip images entirely.

Structure:
- Rich overview paragraph (2-3 sentences)
- Bullet list covering the points above
- A ""Resources"" section with authoritative hyperlinks (plain <a href=""..."">text</a>)
- After the text, include a line ""Images:"" followed by <img src=""..."" alt=""..."" loading=""lazy"" /> for each image (absolute URLs only). Use images that are likely to be stable (e.g., Wikimedia, Wikipedia, major news/edu sites). No base64.

Context: {string.Join(" | ", contextParts)}
Definition (if given): {request.Definition ?? "(none)"}
Relevant passage/context: {request.Context ?? "(none)"}";

            var systemInstructions = "You are a scholarly explainer with expertise in philosophy, critical theory, literature, history, and cultural studies. Provide nuanced, intellectually rich analysis that bridges academic and accessible discourse.";
            var fullInput = $"{systemInstructions}\n\n{prompt}";

            var outcome = await ai.CompleteAsync(
                new AiResponsesCall(
                    Endpoint: "learn-more",
                    Model: model,
                    Input: fullInput,
                    MaxOutputTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:WikiImages"),
                    ReasoningEffort: cfg.GetValue<string>("AI:ReasoningEffort:WikiImages"),
                    Temperature: cfg.GetValue<double>("AI:Temperature:WikiImages")),
                context);

            if (!outcome.Succeeded) return outcome.Failure!;

            return Results.Ok(new LearnMoreResponse(outcome.Text ?? "No details returned."));
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Information("❌ OpenAI learn-more failed: {ExMessage}", ex.Message);
            return ApiResponse.InternalError("Failed to fetch details.");
        }
    }

    private static IResult HandleSaveSectionVocab([FromBody] SaveSectionVocabRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
            return Results.BadRequest(new { error = "dropboxPath is required." });
        if (request.ChapterId < 0 || request.SectionIndex < 0)
            return Results.BadRequest(new { error = "chapterId and sectionIndex must be zero or positive." });
        if (request.Vocab == null)
            return Results.BadRequest(new { error = "vocab is required." });

        Log.Information("💾 Saving {RequestVocabCount} vocab cards for chapter {RequestChapterId}, section {RequestSectionIndex}", request.Vocab.Count, request.ChapterId, request.SectionIndex);

        AiContentCache.SaveSectionVocab(request.DropboxPath, request.ChapterId, request.SectionIndex, request.Vocab);

        return Results.Ok(new { success = true, vocabCount = request.Vocab.Count });
    }
}
