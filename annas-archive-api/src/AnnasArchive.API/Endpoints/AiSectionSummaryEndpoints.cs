using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping AI Section Summary and Chunk Boundaries endpoints.
/// </summary>
public static class AiSectionSummaryEndpoints
{
    /// <summary>
    /// Maps AI Section Summary and Chunk Boundaries endpoints to the application.
    /// </summary>
    public static WebApplication MapAiSectionSummaryEndpoints(this WebApplication app)
    {
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/ai")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/ai/chunk-boundaries - Detect chunk boundaries with SSE progress
        group.MapGet("/chunk-boundaries", HandleChunkBoundaries);

        // GET /api/ai/section-summary - Get cached section summary
        group.MapGet("/section-summary", HandleGetSectionSummary);

        // POST /api/ai/section-summary - Generate section summary
        group.MapPost("/section-summary", HandleGenerateSectionSummary);

        return app;
    }

    /// <summary>
    /// Splits a chapter into sections. The response is JSON when the answer is
    /// already cached and an SSE stream otherwise, because a first read may have
    /// to index the whole book before it can answer — that indexing is the only
    /// slow part left, and it is what the progress events are for.
    /// </summary>
    /// <remarks>
    /// This used to charge the caller's AI allowance, and no longer does: the
    /// split is arithmetic over paragraph breaks (see <see cref="SectionChunker"/>),
    /// so there is no spend to gate and the allowance check was removed with it.
    /// </remarks>
    private static async Task HandleChunkBoundaries(
        HttpContext context,
        [FromQuery] string? dropboxPath,
        [FromQuery] int chapterId,
        DropboxClient dropbox,
        IAiJobLockService jobLock)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "dropboxPath and valid chapterId are required." });
            return;
        }

        // Check cache first
        var cached = AiContentCache.LoadChunkBoundaries(dropboxPath, chapterId);
        if (cached != null)
        {
            Log.Information("✅ Returning cached chunk boundaries for chapter {ChapterId}", chapterId);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(cached);
            return;
        }

        var chunkBoundaryLockKey = $"chunk-boundaries:{dropboxPath}:{chapterId}";
        if (!jobLock.TryStartJob(chunkBoundaryLockKey))
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new { error = "Chunk boundary detection already in progress." });
            return;
        }

        try
        {
            // Not cached - detect boundaries with SSE progress
            Log.Information("🔍 Detecting chunk boundaries for chapter {ChapterId}...", chapterId);

            ServerSentEventsHelper.BeginStream(context.Response);
            var progress = new SseProgress(context.Response);

            // Load chapter content (index if needed)
            var existingKeys = AiContentCache.GetExistingSummaryKeys();
            var isLibrary = AiSummaryHelpers.TryResolveLibraryFileForReaderKey(dropboxPath, existingKeys, out _, out var libraryPath);
            var cacheRoot = isLibrary ? LibraryEpubCache.GetCacheRoot() : DropboxEpubCache.GetCacheRoot();
            var epubHash = isLibrary
                ? LibraryEpubCache.ComputeHashPublic(dropboxPath)
                : DropboxEpubCache.ComputeHashPublic(dropboxPath);
            var chapterPath = Path.Combine(cacheRoot, epubHash, $"chapter-{chapterId:D4}.txt");

            if (!File.Exists(chapterPath))
            {
                // Chapter not indexed - index it now
                await progress.StepAsync("indexing", 0, 1, "Indexing book (first time only)...");
                Log.Information("📑 Chapter {ChapterId} not indexed - indexing entire book now...", chapterId);

                try
                {
                    var cacheDir = Path.Combine(cacheRoot, epubHash);
                    if (isLibrary)
                    {
                        await LibraryEpubCache.EnsureCacheBuildAsync(libraryPath, dropboxPath, cacheDir);
                    }
                    else
                    {
                        await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, dropboxPath, cacheDir);
                    }
                    await progress.StepAsync("indexing", 1, 1, "Book indexed successfully");
                    Log.Information("✅ Book indexed successfully");
                }
                catch (Exception ex)
                {
                    Log.Error("❌ Failed to index book: {ExMessage}", ex.Message);
                    await progress.ErrorAsync($"Failed to index book: {ex.Message}");
                    return;
                }

                // Verify chapter file now exists
                if (!File.Exists(chapterPath))
                {
                    await progress.ErrorAsync("Chapter file not found after indexing");
                    return;
                }
            }

            var chapterText = await File.ReadAllTextAsync(chapterPath);

            await progress.StepAsync("detecting", 0, 1, "Finding section breaks...");

            var chunks = SectionChunker.Detect(chapterText);

            // Save to cache
            AiContentCache.SaveChunkBoundaries(dropboxPath, chapterId, chunks);

            // Send completion event
            var result = new
            {
                chapterId,
                chunks,
                cachedAt = DateTime.UtcNow
            };
            await ServerSentEventsHelper.SendEventAsync(context.Response, result);

            Log.Information("✅ Detected {ChunksCount} sections for chapter {ChapterId}", chunks.Count, chapterId);
        }
        // The one ArgumentException catch that deliberately stays. Everywhere else
        // these were deleted so the exception reaches the global handler, which
        // maps it to a 400 — but this is an SSE stream, so the response has
        // already started and the global handler can do nothing except log
        // (see MiddlewareExtensions.HandleExceptionAsync's HasStarted guard).
        // Letting it through would leave the browser holding an open stream that
        // simply stops, with no `error` event to render.
        catch (ArgumentException ex)
        {
            Log.Error("❌ Invalid argument for chunk boundary detection: {Message}", ex.Message);
            await new SseProgress(context.Response).ErrorAsync($"Invalid parameter: {ex.ParamName ?? "unknown"}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Chunk boundary detection failed for chapter {ChapterId}", chapterId);
            await new SseProgress(context.Response).ErrorAsync($"Detection failed: {ex.Message}");
        }
        finally
        {
            jobLock.EndJob(chunkBoundaryLockKey);
        }
    }

    private static IResult HandleGetSectionSummary(
        [FromQuery] string? dropboxPath,
        [FromQuery] int chapterId,
        [FromQuery] int sectionIndex)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath))
            return ApiResponse.BadRequest("dropboxPath is required.");
        if (chapterId < 0 || sectionIndex < 0)
            return ApiResponse.BadRequest("chapterId and sectionIndex must be zero or positive.");

        var cached = AiContentCache.LoadSectionSummary(dropboxPath, chapterId, sectionIndex);
        if (cached != null)
        {
            // Load associated vocab if it exists
            var vocab = AiContentCache.LoadSectionVocab(dropboxPath, chapterId, sectionIndex);

            // Filter out known AND study words from vocab
            if (vocab != null && vocab.Count > 0)
            {
                Log.Information("🔍 [GET /api/ai/section-summary] Loading {VocabCount} vocab cards from cache", vocab.Count);
                var knownWords = AiContentCache.LoadKnownWords();
                var studyWords = AiContentCache.LoadStudyWordsWithBooks();
                Log.Information("📚 [GET /api/ai/section-summary] Loaded {KnownWordsCount} known words and {StudyWordsCount} study words from server", knownWords.Count, studyWords.Count);

                var beforeCount = vocab.Count;
                var filteredVocab = vocab.Where(card =>
                {
                    var normalized = AiContentCache.NormalizeTerm(card.Term);
                    var isKnown = knownWords.Contains(normalized);
                    var isStudy = studyWords.ContainsKey(normalized);

                    if (isKnown)
                    {
                        Log.Information("  🚫 Filtering out known word: '{CardTerm}' (normalized: '{Normalized}')", card.Term, normalized);
                    }
                    else if (isStudy)
                    {
                        Log.Information("  🚫 Filtering out study word: '{CardTerm}' (normalized: '{Normalized}')", card.Term, normalized);
                    }

                    return !isKnown && !isStudy;
                }).ToList();

                var removedCount = beforeCount - filteredVocab.Count;
                Log.Information("✅ [GET /api/ai/section-summary] Filtered vocab: {BeforeCount} cards → {FilteredVocabCount} cards (removed {RemovedCount} known/study words)", beforeCount, filteredVocab.Count, removedCount);
                vocab = filteredVocab;
            }
            else
            {
                Log.Information("ℹ️ [GET /api/ai/section-summary] No vocab to filter (vocab={VocabCount})", vocab?.Count ?? 0);
            }

            // Create new response with filtered vocab included
            var response = cached with { Vocab = vocab };

            Log.Information("✅ Returning cached section summary for chapter {ChapterId}, section {SectionIndex} (vocab: {VocabCount} cards)",
                chapterId, sectionIndex, vocab?.Count ?? 0);
            return Results.Ok(response);
        }

        return ApiResponse.NotFound("No cached summary found for this section.");
    }

    private static async Task<IResult> HandleGenerateSectionSummary(
        HttpContext context,
        [FromBody] SectionSummaryRequest request,
        DropboxClient dropbox,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IModelSelectionService modelSelection,
        IAiChatCompletion chat,
        IAiJobLockService jobLock)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
            return ApiResponse.BadRequest("dropboxPath is required.");
        if (request.ChapterId < 0 || request.SectionIndex < 0)
            return ApiResponse.BadRequest("chapterId and sectionIndex must be zero or positive.");

        // Check if summary already cached
        var cached = AiContentCache.LoadSectionSummary(request.DropboxPath, request.ChapterId, request.SectionIndex);
        if (cached != null)
        {
            Log.Information("✅ Returning cached section summary for chapter {RequestChapterId}, section {RequestSectionIndex}", request.ChapterId, request.SectionIndex);
            return Results.Ok(cached);
        }

        var sectionSummaryLockKey = $"section-summary:{request.DropboxPath}:{request.ChapterId}:{request.SectionIndex}";
        if (!jobLock.TryStartJob(sectionSummaryLockKey))
        {
            return ApiResponse.Conflict("Section summary already in progress.");
        }

        try
        {
            // Check token limit
            var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
            if (tokenLimitResult is not null) return tokenLimitResult;

            // Load chunk boundaries
            var boundaries = AiContentCache.LoadChunkBoundaries(request.DropboxPath, request.ChapterId);
            if (boundaries == null || request.SectionIndex >= boundaries.Chunks.Count)
                return ApiResponse.BadRequest("Invalid sectionIndex or chunk boundaries not detected.");

            // Load chapter content
            var existingKeys = AiContentCache.GetExistingSummaryKeys();
            var isLibrary = AiSummaryHelpers.TryResolveLibraryFileForReaderKey(request.DropboxPath, existingKeys, out _, out var libraryPath);
            var cacheRoot = isLibrary ? LibraryEpubCache.GetCacheRoot() : DropboxEpubCache.GetCacheRoot();
            var epubHash = isLibrary
                ? LibraryEpubCache.ComputeHashPublic(request.DropboxPath)
                : DropboxEpubCache.ComputeHashPublic(request.DropboxPath);
            var chapterPath = Path.Combine(cacheRoot, epubHash, $"chapter-{request.ChapterId:D4}.txt");

            if (!File.Exists(chapterPath))
            {
                if (isLibrary)
                {
                    await LibraryEpubCache.EnsureCacheBuildAsync(libraryPath, request.DropboxPath, Path.Combine(cacheRoot, epubHash));
                }
                else
                {
                    await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, request.DropboxPath, Path.Combine(cacheRoot, epubHash));
                }
            }

            if (!File.Exists(chapterPath))
                return ApiResponse.NotFound("Chapter not indexed.");

            var chapterText = await File.ReadAllTextAsync(chapterPath);
            var words = chapterText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            var chunk = boundaries.Chunks[request.SectionIndex];
            var sectionWords = words.Skip(chunk.Start).Take(chunk.WordCount).ToArray();
            var sectionText = string.Join(" ", sectionWords);

            Log.Information("📝 Generating summary for chapter {RequestChapterId}, section {RequestSectionIndex} ({ChunkWordCount} words)", request.ChapterId, request.SectionIndex, chunk.WordCount);

            var outcome = await chat.CompleteAsync(
                ReaderPrompts.SectionSummary(
                    model: modelSelection.GetModelDeep(),
                    bookTitle: request.BookTitle,
                    sectionText: sectionText,
                    maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:SectionSummary", 2000),
                    temperature: cfg.GetValue<double>("AI:Temperature:SectionSummary", 0.5)),
                context);

            if (!outcome.Succeeded) return outcome.Failure!;

            Log.Information("✅ Summary generated: {SummaryLength} characters", outcome.Text?.Length ?? 0);

            var result = new SectionSummaryResponse(
                outcome.Text ?? "No summary generated.",
                request.SectionIndex,
                outcome.Usage.PromptTokens,
                outcome.Usage.CompletionTokens,
                outcome.Usage.TotalTokens,
                DateTime.UtcNow
            );

            AiContentCache.SaveSectionSummary(request.DropboxPath, request.ChapterId, request.SectionIndex, result);
            Log.Information("💾 Section summary cached successfully");

            return Results.Ok(result);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error("❌ Section summary generation failed: {ExMessage}", ex.Message);
            Log.Information("   Stack trace: {ExStackTrace}", ex.StackTrace);
            return Results.Problem("Failed to generate section summary.");
        }
        finally
        {
            jobLock.EndJob(sectionSummaryLockKey);
        }
    }
}
