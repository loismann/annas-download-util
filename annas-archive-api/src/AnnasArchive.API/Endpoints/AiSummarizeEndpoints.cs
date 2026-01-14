using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Microsoft.AspNetCore.Mvc;

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
        // POST /api/ai/summarize - Generate summary for text passage
        app.MapPost("/api/ai/summarize", HandleSummarize)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/summarize/chapter/stream - Generate full chapter summary with SSE progress
        app.MapPost("/api/ai/summarize/chapter/stream", HandleChapterSummaryStream)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/ai/summarize/chapter - Get cached chapter summary
        app.MapGet("/api/ai/summarize/chapter", HandleGetChapterSummary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/ai/summarize/chapter - Delete cached chapter summary
        app.MapDelete("/api/ai/summarize/chapter", HandleDeleteChapterSummary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/summarize/chapter/dummy - Generate "I'm a Dummy" chapter summary
        app.MapPost("/api/ai/summarize/chapter/dummy", HandleDummySummary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/ai/summarize/chapter/dummy - Get cached "I'm a Dummy" summary
        app.MapGet("/api/ai/summarize/chapter/dummy", HandleGetDummySummary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/ai/summarize/book - Get all cached summaries for a book
        app.MapGet("/api/ai/summarize/book", HandleGetBookSummaries)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleSummarize(
        HttpContext context,
        [FromBody] SummarizeRequest request,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IValidationService validation,
        ITextProcessingService textProcessing)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return Results.BadRequest(new { error = "Text is required." });

        if (!validation.IsValidTextLength(request.Text))
            return Results.BadRequest(new { error = "Text too long. Maximum 1,000,000 characters." });

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            using var http = httpFactory.CreateClient("OpenAI");
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

            // Build the system prompt with known words exclusion
            var systemPromptBase = @"You are an advanced literary analysis assistant with deep knowledge of philosophy, critical theory, and cultural studies. Provide a rich, thoughtful analysis (max 200 words) that goes beyond surface-level reading:

**Analysis should include:**
- What's happening narratively and conceptually
- Philosophical undertones and implicit arguments the author is making
- Literary techniques and their rhetorical effect
- How this passage connects to broader themes in the work
- Academic interpretations and critical perspectives (if applicable)
- Cultural, historical, or political context that enriches understanding
- Connections to other philosophical or literary traditions

Then add a 'Definitions:' section. BE EXTREMELY THOROUGH with definitions - include ALL words/phrases a typical high school student might not know: archaic terms, foreign words/phrases, technical jargon, sophisticated vocabulary, philosophical concepts, brand names, historical items, British/European terms, proper nouns needing context, academic terminology. Err on the side of over-defining.";

            string systemPrompt;
            if (request.KnownWords != null && request.KnownWords.Count > 0)
            {
                var knownWordsList = string.Join(", ", request.KnownWords);
                systemPrompt = $"{systemPromptBase}\n\nIMPORTANT: The user already knows these words, so DO NOT define them: {knownWordsList}. Total response can be up to 600 words.";
            }
            else
            {
                systemPrompt = $"{systemPromptBase}\n\nTotal response can be up to 600 words.";
            }

            var userPrompt = textProcessing.BuildAnalysisPrompt(contextBlock, previousAnalyses, request.Text);
            var fullInput = $"{systemPrompt}\n\n{userPrompt}";

            var payload = new
            {
                model = model,
                input = fullInput,
                reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:Vocabulary") },
                max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:Vocabulary"),
                temperature = cfg.GetValue<double>("AI:Temperature:Vocabulary")
            };

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ OpenAI summarize failed status={(int)response.StatusCode} body={body}");
                return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var summary = aiResponseParser.ExtractText(doc.RootElement);

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("input_tokens").GetInt32();
                var completionTokens = usage.GetProperty("output_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

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
        catch (Exception ex)
        {
            Console.WriteLine($"❌ OpenAI summarize failed: {ex.Message}");
            return ApiResponse.InternalError("Failed to summarize text.");
        }
    }

    private static async Task HandleChapterSummaryStream(
        HttpContext context,
        [FromBody] FullChapterSummaryRequest request,
        DropboxClient dropbox,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        ITextProcessingService textProcessing,
        IAiJobLockService jobLock)
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
            Console.WriteLine($"📦 Returning cached chapter summary for {request.DropboxPath} chapter {request.ChapterId}");
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.Append("Cache-Control", "no-cache");
            context.Response.Headers.Append("Connection", "keep-alive");

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

            // Set up SSE headers
            context.Response.Headers.Append("Content-Type", "text/event-stream");
            context.Response.Headers.Append("Cache-Control", "no-cache");
            context.Response.Headers.Append("Connection", "keep-alive");

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

            using var http = httpFactory.CreateClient("OpenAI");
            var model = modelSelection.GetModelDeep();

            // TIER 1: Summarize chunks using helper
            var (chunkSummaries, tier1PromptTokens, tier1CompletionTokens) =
                await AiSummaryHelpers.SummarizeChunksAsync(http, model, chunks, contextLine, context.Response, cfg, aiResponseParser, tokenUsage);

            // TIER 2: Synthesize sections using helper
            var (sectionSummaries, tier2PromptTokens, tier2CompletionTokens) =
                await AiSummaryHelpers.SynthesizeSectionsAsync(http, model, chunkSummaries, contextLine, context.Response, cfg, aiResponseParser, tokenUsage);

            // TIER 3: Create final summary using helper
            var (finalSummary, tier3PromptTokens, tier3CompletionTokens) =
                await AiSummaryHelpers.CreateFinalSummaryAsync(http, model, sectionSummaries, contextParts, context.Response, cfg, aiResponseParser);

            // Calculate total tokens
            var promptTokensTotal = tier1PromptTokens + tier2PromptTokens + tier3PromptTokens;
            var completionTokensTotal = tier1CompletionTokens + tier2CompletionTokens + tier3CompletionTokens;

            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, (int)promptTokensTotal, (int)completionTokensTotal);
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
            Console.WriteLine($"❌ Full-chapter summary failed: {ex.Message}");
            await ServerSentEventsHelper.SendEventAsync(context.Response, new { message = "Failed to summarize chapter.", error = ex.Message }, "error");
        }
        finally
        {
            jobLock.EndJob(chapterSummaryLockKey);
        }
    }

    private static IResult HandleGetChapterSummary(
        [FromQuery] string dropboxPath,
        [FromQuery] int chapterId)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
            return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

        var cached = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(dropboxPath, chapterId);
        if (cached == null)
            return ApiResponse.NotFound("No summary cached for this chapter.");

        return Results.Ok(cached);
    }

    private static IResult HandleDeleteChapterSummary(
        [FromQuery] string dropboxPath,
        [FromQuery] int chapterId)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
            return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

        AiContentCache.DeleteChapterSummary(dropboxPath, chapterId);
        return Results.Ok(new { message = "Cached summary deleted." });
    }

    private static async Task<IResult> HandleDummySummary(
        HttpContext context,
        [FromBody] UltraChapterSummaryRequest request,
        DropboxClient dropbox,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath) || request.ChapterId < 0)
            return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

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

        var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.Problem("OpenAI API key not configured.");

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

        var systemPrompt = @"You are a friendly teacher who makes hard ideas feel obvious.
Write in a warm, conversational tone for a smart reader with zero background knowledge.
Use 3–5 short paragraphs. No headings, no bullet points, no numbered lists.";

        var userPrompt = $@"Explain this chapter in the clearest, most human way possible.
Focus on:
- why this matters
- what the author is really getting at
- why someone should care
- how it connects (or doesn't) to modern life

Be direct, vivid, and helpful without dumbing it down.

{contextLine}

Chapter summary:
{baseSummaryText}";

        using var http = httpFactory.CreateClient("OpenAI");
        var model = cfg["OpenAI:ModelUltra"]
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL_ULTRA")
            ?? modelSelection.GetModelDeep();

        var reasoningEffort = cfg.GetValue<string>("AI:ReasoningEffort:UltraSummary") ?? "high";
        var maxCompletion = cfg.GetValue<int?>("AI:MaxCompletionTokens:UltraChapterSummary")
            ?? cfg.GetValue<int?>("AI:MaxCompletionTokens:FullChapterSummary")
            ?? 1400;

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: maxCompletion,
            temperature: null,
            reasoningEffort: reasoningEffort
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI ultra summary failed: {response.StatusCode}");
            Console.WriteLine($"   Response body: {body}");
            return Results.Problem($"Ultra summary failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var summary = aiResponseParser.ExtractText(doc.RootElement);
        if (string.IsNullOrWhiteSpace(summary))
            return Results.Problem("Ultra summary response was empty.");

        var promptTokens = 0;
        var completionTokens = 0;
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId != null)
                tokenUsage.AddUsage(userId, promptTokens, completionTokens);
        }

        // Note: No longer calculating global allowance stats (now tracked per-user)
        var summaryData = new
        {
            summary = summary,
            promptTokens,
            completionTokens,
            totalTokens = promptTokens + completionTokens,
            cachedAt = DateTime.UtcNow
        };

        AiContentCache.SaveUltraChapterSummary(request.DropboxPath, request.ChapterId, summaryData);
        return Results.Ok(summaryData);
    }

    private static IResult HandleGetDummySummary(
        [FromQuery] string dropboxPath,
        [FromQuery] int chapterId)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
            return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

        var cached = AiContentCache.LoadUltraChapterSummary<Dictionary<string, object>>(dropboxPath, chapterId);
        if (cached == null)
            return ApiResponse.NotFound("No dummy summary cached for this chapter.");

        return Results.Ok(cached);
    }

    private static IResult HandleGetBookSummaries([FromQuery] string dropboxPath)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath))
            return Results.BadRequest(new { error = "dropboxPath is required." });

        var summaries = AiContentCache.LoadAllChapterSummaries(dropboxPath);
        return Results.Ok(summaries);
    }
}
