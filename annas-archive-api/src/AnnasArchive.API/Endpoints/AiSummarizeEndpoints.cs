using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping AI Summarize endpoints.
/// </summary>
public static class AiSummarizeEndpoints
{
    /// <summary>
    /// Maps AI Summarize endpoints to the application.
    /// </summary>
    public static WebApplication MapAiSummarizeEndpoints(this WebApplication app)
    {
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/ai")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/summarize - Generate summary for text passage
        group.MapPost("/summarize", HandleSummarize);

        // POST /api/ai/summarize/chapter/stream - Generate full chapter summary with SSE progress
        group.MapPost("/summarize/chapter/stream", HandleChapterSummaryStream);

        // GET /api/ai/summarize/chapter - Get cached chapter summary
        group.MapGet("/summarize/chapter", HandleGetChapterSummary);

        // DELETE /api/ai/summarize/chapter - Delete cached chapter summary
        group.MapDelete("/summarize/chapter", HandleDeleteChapterSummary);

        // POST /api/ai/summarize/chapter/dummy - Generate "I'm a Dummy" chapter summary
        group.MapPost("/summarize/chapter/dummy", HandleDummySummary);

        // GET /api/ai/summarize/chapter/dummy - Get cached "I'm a Dummy" summary
        group.MapGet("/summarize/chapter/dummy", HandleGetDummySummary);

        // GET /api/ai/summarize/book - Get all cached summaries for a book
        group.MapGet("/summarize/book", HandleGetBookSummaries);

        return app;
    }

    private static async Task<IResult> HandleSummarize(
        HttpContext context,
        [FromBody] SummarizeRequest request,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IModelSelectionService modelSelection,
        IValidationService validation,
        ITextProcessingService textProcessing,
        IAiResponsesCompletion ai)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return ApiResponse.BadRequest("Text is required.");

        if (!validation.IsValidTextLength(request.Text))
            return ApiResponse.BadRequest("Text too long. Maximum 1,000,000 characters.");

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            var model = modelSelection.GetModelFast();

            string? previousAnalyses = null;
            string? cacheDirForSummary = null;

            if (!string.IsNullOrWhiteSpace(request.DropboxPath))
            {
                cacheDirForSummary = Path.Combine(DropboxEpubCache.GetCacheRoot(), DropboxEpubCache.ComputeHashPublic(request.DropboxPath));
                Directory.CreateDirectory(cacheDirForSummary);

                if (request.ChapterId.HasValue)
                {
                    // Load ALL previous analyses for this chapter (sorted chronologically by word offset)
                    var existingFiles = Directory.EnumerateFiles(cacheDirForSummary, $"summary-{request.ChapterId.Value}-*.txt")
                        .Select(f => new
                        {
                            Path = f,
                            Offset = textProcessing.ExtractWordOffset(Path.GetFileNameWithoutExtension(f))
                        })
                        .Where(x => x.Offset < (request.WordOffset ?? int.MaxValue)) // Only include analyses from earlier in the chapter
                        .OrderBy(x => x.Offset)
                        .ToList();

                    if (existingFiles.Any())
                    {
                        var analyses = new List<string>();
                        foreach (var file in existingFiles)
                        {
                            var content = await File.ReadAllTextAsync(file.Path);
                            if (!string.IsNullOrWhiteSpace(content))
                                analyses.Add(content);
                        }

                        if (analyses.Count > 0)
                            previousAnalyses = string.Join("\n\n---\n\n", analyses);
                    }
                }
            }

            var contextParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.BookTitle))
                contextParts.Add($"Title: {request.BookTitle}");
            if (!string.IsNullOrWhiteSpace(request.Author))
                contextParts.Add($"Author: {request.Author}");
            if (request.Year.HasValue)
                contextParts.Add($"Year: {request.Year.Value}");
            if (!string.IsNullOrWhiteSpace(request.Premise))
                contextParts.Add($"Premise: {request.Premise}");

            var contextBlock = contextParts.Count > 0
                ? $"Book context -> {string.Join(" | ", contextParts)}"
                : "Book context -> (not provided)";

            var outcome = await ai.CompleteAsync(
                ChapterSummaryPrompts.PassageAnalysis(
                    model: model,
                    userPrompt: textProcessing.BuildAnalysisPrompt(contextBlock, previousAnalyses, request.Text),
                    knownWords: request.KnownWords,
                    maxOutputTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:Vocabulary"),
                    reasoningEffort: cfg.GetValue<string>("AI:ReasoningEffort:Vocabulary"),
                    temperature: cfg.GetValue<double>("AI:Temperature:Vocabulary")),
                context);

            if (!outcome.Succeeded) return outcome.Failure!;

            var summary = outcome.Text;

            if (cacheDirForSummary != null && request.ChapterId.HasValue)
            {
                var offsetLabel = request.WordOffset?.ToString() ?? DateTime.UtcNow.Ticks.ToString();
                var fileName = $"summary-{request.ChapterId.Value}-{offsetLabel}.txt";
                var savePath = Path.Combine(cacheDirForSummary, fileName);
                try
                {
                    await File.WriteAllTextAsync(savePath, summary ?? string.Empty);
                }
                catch { /* ignore */ }
            }

            return Results.Ok(new SummarizeResponse(summary ?? "No summary returned."));
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ OpenAI summarize failed");
            return ApiResponse.InternalError(AiFailureMessage.ForException(ex));
        }
    }

    private static async Task HandleChapterSummaryStream(
        HttpContext context,
        [FromBody] FullChapterSummaryRequest request,
        DropboxClient dropbox,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IModelSelectionService modelSelection,
        ITextProcessingService textProcessing,
        IAiJobLockService jobLock,
        IAiResponsesCompletion ai)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "dropboxPath is required." });
            return;
        }
        if (request.ChapterId < 0)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "chapterId must be zero or positive." });
            return;
        }

        if (request.ForceRegenerate)
        {
            AiContentCache.DeleteChapterSummary(request.DropboxPath, request.ChapterId);
        }

        // Check if cached summary exists
        var cached = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
        if (cached != null)
        {
            Log.Information("📦 Returning cached chapter summary for {RequestDropboxPath} chapter {RequestChapterId}", request.DropboxPath, request.ChapterId);
            ServerSentEventsHelper.BeginStream(context.Response);

            static long ToLong(object? value)
            {
                if (value == null) return 0L;
                if (value is long l) return l;
                if (value is int i) return i;
                if (value is double d) return (long)d;
                if (value is string s && long.TryParse(s, out var parsed)) return parsed;
                if (value is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var num)) return num;
                    if (je.ValueKind == JsonValueKind.String && long.TryParse(je.GetString(), out var numFromString)) return numFromString;
                }
                return 0L;
            }

            static DateTime ToDateTime(object? value)
            {
                if (value == null) return DateTime.UtcNow;
                if (value is DateTime dt) return dt;
                if (value is string s && DateTime.TryParse(s, out var parsed)) return parsed;
                if (value is JsonElement je && je.ValueKind == JsonValueKind.String && DateTime.TryParse(je.GetString(), out var parsedJe)) return parsedJe;
                return DateTime.UtcNow;
            }

            var completeEvent = new
            {
                summary = cached.GetValueOrDefault("summary", ""),
                promptTokens = cached.TryGetValue("promptTokens", out var pt) ? ToLong(pt) : 0L,
                completionTokens = cached.TryGetValue("completionTokens", out var ct) ? ToLong(ct) : 0L,
                totalTokens = cached.TryGetValue("totalTokens", out var tt) ? ToLong(tt) : 0L,
                cachedAt = cached.TryGetValue("cachedAt", out var cachedAt) ? ToDateTime(cachedAt) : DateTime.UtcNow
            };

            await ServerSentEventsHelper.SendEventAsync(context.Response, completeEvent, "complete");
            return;
        }

        var chapterSummaryLockKey = $"chapter-summary:{request.DropboxPath}:{request.ChapterId}";
        if (!jobLock.TryStartJob(chapterSummaryLockKey))
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new { error = "Chapter summary already in progress." });
            return;
        }

        try
        {
            // Check token limit
            var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
            if (tokenLimitResult is not null)
            {
                await tokenLimitResult.ExecuteAsync(context);
                return;
            }

            var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = "OpenAI API key not configured." });
                return;
            }

            ServerSentEventsHelper.BeginStream(context.Response);

            // Load chapter content using helper
            var content = await AiSummaryHelpers.LoadChapterContentAsync(dropbox, request.DropboxPath, request.ChapterId);
            if (content is null)
            {
                await ServerSentEventsHelper.SendEventAsync(context.Response, new { message = "Chapter not found or empty." }, "error");
                return;
            }

            // Prepare context for AI
            var index = await AiSummaryHelpers.LoadChapterIndexAsync(dropbox, request.DropboxPath);
            var chapter = index?.Chapters.FirstOrDefault(c => c.Id == request.ChapterId);

            var contextParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.BookTitle))
                contextParts.Add($"Book: {request.BookTitle}");

            // Use DisplayChapterNumber if provided (filtered chapters), otherwise fall back to ChapterId + 1
            var chapterNum = request.DisplayChapterNumber ?? (request.ChapterId + 1);
            var chapterTitle = !string.IsNullOrWhiteSpace(chapter?.Title)
                ? $"Chapter {chapterNum}: {chapter.Title}"
                : $"Chapter {chapterNum}";
            contextParts.Add(chapterTitle);
            var contextLine = string.Join(" | ", contextParts);

            // Split into chunks
            var chunkSize = cfg.GetValue<int>("AI:ChunkSize");
            var chunks = textProcessing.SplitIntoChunks(content, chunkSize);

            var model = modelSelection.GetModelDeep();
            var userId = UserHelpers.GetUserIdFromContext(context);

            // TIER 1: Summarize chunks using helper
            var (chunkSummaries, tier1PromptTokens, tier1CompletionTokens) =
                await AiSummaryHelpers.SummarizeChunksAsync(ai, model, chunks, contextLine, context.Response, cfg, userId);

            // TIER 2: Synthesize sections using helper
            var (sectionSummaries, tier2PromptTokens, tier2CompletionTokens) =
                await AiSummaryHelpers.SynthesizeSectionsAsync(ai, model, chunkSummaries, contextLine, context.Response, cfg, userId);

            // TIER 3: Create final summary using helper
            var (finalSummary, tier3PromptTokens, tier3CompletionTokens) =
                await AiSummaryHelpers.CreateFinalSummaryAsync(ai, model, sectionSummaries, contextParts, context.Response, cfg, userId);

            // Each tier bills as it goes, so these totals are for the progress
            // report only — adding them again here would double-charge a summary
            // that can run to twenty-odd calls.
            var promptTokensTotal = tier1PromptTokens + tier2PromptTokens + tier3PromptTokens;
            var completionTokensTotal = tier1CompletionTokens + tier2CompletionTokens + tier3CompletionTokens;

            var totals = tokenUsage.GetTotals(userId ?? "");
            var monthlyAllowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
            double? percent = null;
            long? remaining = null;
            if (monthlyAllowance.HasValue && monthlyAllowance.Value > 0)
            {
                percent = Math.Round((double)totals.TotalTokens / monthlyAllowance.Value * 100, 2);
                remaining = monthlyAllowance.Value - totals.TotalTokens;
            }

            // Save summary to cache
            var summaryData = new
            {
                summary = finalSummary,
                promptTokens = promptTokensTotal,
                completionTokens = completionTokensTotal,
                totalTokens = promptTokensTotal + completionTokensTotal,
                allowanceUsedPercent = percent,
                tokensRemaining = remaining,
                cachedAt = DateTime.UtcNow
            };

            AiContentCache.SaveChapterSummary(request.DropboxPath, request.ChapterId, summaryData);

            // Send completion event with full summary
            await ServerSentEventsHelper.SendEventAsync(context.Response, summaryData, "complete");
        }
        catch (Exception ex)
        {
            // The only place a chapter-summary failure reaches the browser. What
            // goes out is the reason phrased for a reader — ex.Message here is an
            // internal detail (after a tripped breaker, Polly's own "the circuit is
            // now open"), which named neither the cause nor the fix.
            Log.Error(ex, "❌ Full-chapter summary failed for {DropboxPath} chapter {ChapterId}",
                request.DropboxPath, request.ChapterId);
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                message = "Failed to summarize chapter.",
                error = AiFailureMessage.ForException(ex)
            }, "error");
        }
        finally
        {
            jobLock.EndJob(chapterSummaryLockKey);
        }
    }

    private static IResult HandleGetChapterSummary(
        [FromQuery] string? dropboxPath,
        [FromQuery] int chapterId)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
            return ApiResponse.BadRequest("dropboxPath and valid chapterId are required.");

        var cached = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(dropboxPath, chapterId);
        if (cached == null)
            return ApiResponse.NotFound("No summary cached for this chapter.");

        return Results.Ok(cached);
    }

    private static IResult HandleDeleteChapterSummary(
        [FromQuery] string? dropboxPath,
        [FromQuery] int chapterId)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
            return ApiResponse.BadRequest("dropboxPath and valid chapterId are required.");

        AiContentCache.DeleteChapterSummary(dropboxPath, chapterId);
        return Results.Ok(new { message = "Cached summary deleted." });
    }

    private static async Task<IResult> HandleDummySummary(
        HttpContext context,
        [FromBody] UltraChapterSummaryRequest request,
        DropboxClient dropbox,
        IConfiguration cfg,
        IModelSelectionService modelSelection,
        IAiChatCompletion chat)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath) || request.ChapterId < 0)
            return ApiResponse.BadRequest("dropboxPath and valid chapterId are required.");

        if (request.ForceRegenerate)
        {
            AiContentCache.DeleteUltraChapterSummary(request.DropboxPath, request.ChapterId);
        }

        var cached = AiContentCache.LoadUltraChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
        if (cached != null)
            return Results.Ok(cached);

        var baseSummaryData = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
        var baseSummaryText = baseSummaryData != null && baseSummaryData.TryGetValue("summary", out var summaryObj)
            ? summaryObj?.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(baseSummaryText))
            return ApiResponse.NotFound("Full chapter summary is required before generating the dummy explanation.");

        var index = await AiSummaryHelpers.LoadChapterIndexAsync(dropbox, request.DropboxPath);
        var chapter = index?.Chapters.FirstOrDefault(c => c.Id == request.ChapterId);
        var chapterTitle = chapter?.Title;

        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.BookTitle))
            contextParts.Add($"Book: {request.BookTitle}");

        // Use DisplayChapterNumber if provided (filtered chapters), otherwise fall back to ChapterId + 1
        var chapterNum = request.DisplayChapterNumber ?? (request.ChapterId + 1);
        if (!string.IsNullOrWhiteSpace(chapterTitle))
            contextParts.Add($"Chapter {chapterNum}: {chapterTitle}");
        else
            contextParts.Add($"Chapter {chapterNum}");

        var contextLine = contextParts.Count > 0 ? string.Join(" | ", contextParts) : "Chapter context";

        var outcome = await chat.CompleteAsync(
            ReaderPrompts.DummyChapterSummary(
                model: cfg["OpenAI:ModelUltra"]
                    ?? Environment.GetEnvironmentVariable("OPENAI_MODEL_ULTRA")
                    ?? modelSelection.GetModelDeep(),
                chapterContext: contextLine,
                baseSummaryText: baseSummaryText,
                maxCompletionTokens: cfg.GetValue<int?>("AI:MaxCompletionTokens:UltraChapterSummary")
                    ?? cfg.GetValue<int?>("AI:MaxCompletionTokens:FullChapterSummary")
                    ?? 1400,
                reasoningEffort: cfg.GetValue<string>("AI:ReasoningEffort:UltraSummary") ?? "high"),
            context);

        if (!outcome.Succeeded) return outcome.Failure!;
        if (string.IsNullOrWhiteSpace(outcome.Text))
            return Results.Problem("Ultra summary response was empty.");

        // Note: No longer calculating global allowance stats (now tracked per-user)
        var summaryData = new
        {
            summary = outcome.Text,
            promptTokens = outcome.Usage.PromptTokens,
            completionTokens = outcome.Usage.CompletionTokens,
            totalTokens = outcome.Usage.TotalTokens,
            cachedAt = DateTime.UtcNow
        };

        AiContentCache.SaveUltraChapterSummary(request.DropboxPath, request.ChapterId, summaryData);
        return Results.Ok(summaryData);
    }

    private static IResult HandleGetDummySummary(
        [FromQuery] string? dropboxPath,
        [FromQuery] int chapterId)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
            return ApiResponse.BadRequest("dropboxPath and valid chapterId are required.");

        var cached = AiContentCache.LoadUltraChapterSummary<Dictionary<string, object>>(dropboxPath, chapterId);
        if (cached == null)
            return ApiResponse.NotFound("No dummy summary cached for this chapter.");

        return Results.Ok(cached);
    }

    private static IResult HandleGetBookSummaries([FromQuery] string? dropboxPath)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath))
            return ApiResponse.BadRequest("dropboxPath is required.");

        var summaries = AiContentCache.LoadAllChapterSummaries(dropboxPath);
        return Results.Ok(summaries);
    }
}
