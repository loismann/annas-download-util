using System.Text.Json;
using AnnasArchive.API.Helpers;
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
        // POST /api/ai/vocab/learn-more - Get detailed info about a vocab term
        app.MapPost("/api/ai/vocab/learn-more", HandleLearnMore)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/section-vocab - Save section vocabulary to cache
        app.MapPost("/api/ai/section-vocab", HandleSaveSectionVocab)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleLearnMore(
        HttpContext context,
        [FromBody] LearnMoreRequest request,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Term))
            return Results.BadRequest(new { error = "Term is required." });

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            using var http = httpFactory.CreateClient("OpenAI");
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

            var payload = new
            {
                model,
                input = fullInput,
                reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:WikiImages") },
                max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:WikiImages"),
                temperature = cfg.GetValue<double>("AI:Temperature:WikiImages")
            };

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Information("❌ OpenAI learn-more failed status={(int)response.StatusCode} body={body}");
                return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var detail = aiResponseParser.ExtractText(doc.RootElement);

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("input_tokens").GetInt32();
                var completionTokens = usage.GetProperty("output_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            return Results.Ok(new LearnMoreResponse(detail ?? "No details returned."));
        }
        catch (ArgumentException ex)
        {
            Log.Information("❌ Invalid argument for learn-more: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("❌ OpenAI learn-more failed: {ex.Message}");
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

        Log.Information("💾 Saving {request.Vocab.Count} vocab cards for chapter {request.ChapterId}, section {request.SectionIndex}");

        AiContentCache.SaveSectionVocab(request.DropboxPath, request.ChapterId, request.SectionIndex, request.Vocab);

        return Results.Ok(new { success = true, vocabCount = request.Vocab.Count });
    }
}
