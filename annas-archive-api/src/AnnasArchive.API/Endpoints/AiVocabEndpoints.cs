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
            return ApiResponse.BadRequest("Term is required.");

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            // The budget keys say WikiImages, not LearnMore, and that is not a
            // typo being preserved by accident — AI:MaxCompletionTokens:LearnMore
            // exists in appsettings and has never been read. Changing which key
            // this reads changes the answer's length in production, so it is a
            // deliberate decision rather than a rename, and it is not this one.
            var outcome = await ai.CompleteAsync(
                ReaderPrompts.LearnMore(
                    model: modelSelection.GetModelDeep(),
                    term: request.Term,
                    bookTitle: request.BookTitle,
                    sourcePath: request.DropboxPath,
                    definition: request.Definition,
                    passageContext: request.Context,
                    maxOutputTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:WikiImages"),
                    reasoningEffort: cfg.GetValue<string>("AI:ReasoningEffort:WikiImages"),
                    temperature: cfg.GetValue<double>("AI:Temperature:WikiImages")),
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
            return ApiResponse.BadRequest("dropboxPath is required.");
        if (request.ChapterId < 0 || request.SectionIndex < 0)
            return ApiResponse.BadRequest("chapterId and sectionIndex must be zero or positive.");
        if (request.Vocab == null)
            return ApiResponse.BadRequest("vocab is required.");

        Log.Information("💾 Saving {RequestVocabCount} vocab cards for chapter {RequestChapterId}, section {RequestSectionIndex}", request.Vocab.Count, request.ChapterId, request.SectionIndex);

        AiContentCache.SaveSectionVocab(request.DropboxPath, request.ChapterId, request.SectionIndex, request.Vocab);

        return Results.Ok(new { success = true, vocabCount = request.Vocab.Count });
    }
}
