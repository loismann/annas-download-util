using System.Text.Json;
using System.Text.RegularExpressions;
using AnnasArchive.API.Configuration;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
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
        // GET /api/ai/chunk-boundaries - Detect chunk boundaries with SSE progress
        app.MapGet("/api/ai/chunk-boundaries", HandleChunkBoundaries)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/ai/section-summary - Get cached section summary
        app.MapGet("/api/ai/section-summary", HandleGetSectionSummary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/section-summary - Generate section summary
        app.MapPost("/api/ai/section-summary", HandleGenerateSectionSummary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task HandleChunkBoundaries(
        HttpContext context,
        [FromQuery] string? dropboxPath,
        [FromQuery] int chapterId,
        DropboxClient dropbox,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
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
            Log.Information("✅ Returning cached chunk boundaries for chapter {chapterId}");
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
            Log.Information("🔍 Detecting chunk boundaries for chapter {chapterId}...");

            // Set up SSE
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";

            // Check token limit
            if (TokenLimitHelpers.IsTokenLimitExceeded(cfg, tokenUsage, context))
            {
                await ServerSentEventsHelper.SendEventAsync(context.Response, new
                {
                    stage = "error",
                    stepNumber = 0,
                    totalSteps = 1,
                    message = "Monthly AI usage allowance exceeded"
                });
                return;
            }

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
                await ServerSentEventsHelper.SendEventAsync(context.Response, new
                {
                    stage = "indexing",
                    stepNumber = 0,
                    totalSteps = 1,
                    message = "Indexing book (first time only)..."
                });
                Log.Information("📑 Chapter {chapterId} not indexed - indexing entire book now...");

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
                    await ServerSentEventsHelper.SendEventAsync(context.Response, new
                    {
                        stage = "indexing",
                        stepNumber = 1,
                        totalSteps = 1,
                        message = "Book indexed successfully"
                    });
                    Log.Information("✅ Book indexed successfully");
                }
                catch (Exception ex)
                {
                    Log.Information("❌ Failed to index book: {ex.Message}");
                    await ServerSentEventsHelper.SendEventAsync(context.Response, new
                    {
                        stage = "error",
                        stepNumber = 0,
                        totalSteps = 1,
                        message = $"Failed to index book: {ex.Message}"
                    });
                    return;
                }

                // Verify chapter file now exists
                if (!File.Exists(chapterPath))
                {
                    await ServerSentEventsHelper.SendEventAsync(context.Response, new
                    {
                        stage = "error",
                        stepNumber = 0,
                        totalSteps = 1,
                        message = "Chapter file not found after indexing"
                    });
                    return;
                }
            }

            var chapterText = await File.ReadAllTextAsync(chapterPath);
            var words = chapterText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var totalWords = words.Length;

            Log.Information("📖 Chapter has {totalWords} words");

            // Estimate total chunks
            var estimatedChunks = Math.Max(1, (int)Math.Ceiling(totalWords / 500.0));
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "detecting",
                stepNumber = 0,
                totalSteps = estimatedChunks,
                message = $"Analyzing {totalWords:N0} words..."
            });

            // Use GPT-4o to detect chunk boundaries
            var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                await ServerSentEventsHelper.SendEventAsync(context.Response, new
                {
                    stage = "error",
                    stepNumber = 0,
                    totalSteps = 1,
                    message = "OpenAI API key not configured"
                });
                return;
            }

            var chunks = new List<ChunkBoundary>();
            var currentStart = 0;
            var targetChunkSize = 500;
            var maxChunkSize = 600;

            using var http = httpFactory.CreateClient("OpenAI");
            var model = "gpt-4o"; // Use GPT-4o for cost-effective chunking

            Log.Information("🤖 Using model for chunk detection: {model}");
            Log.Information("   Model info: {modelHelper.GetModelDescription(model)}");

            while (currentStart < totalWords)
            {
                var chunkIndex = chunks.Count + 1;
                await ServerSentEventsHelper.SendEventAsync(context.Response, new
                {
                    stage = "detecting",
                    stepNumber = chunkIndex,
                    totalSteps = estimatedChunks,
                    message = $"Detecting section {chunkIndex} of ~{estimatedChunks}..."
                });

                // Extract text window (target 500-600 words)
                var endWord = Math.Min(currentStart + maxChunkSize, totalWords);
                var windowWords = words.Skip(currentStart).Take(endWord - currentStart).ToArray();
                var windowText = string.Join(" ", windowWords);

                if (endWord >= totalWords)
                {
                    // Last chunk - just add it
                    chunks.Add(new ChunkBoundary(currentStart, totalWords, totalWords - currentStart));
                    break;
                }

                // Ask GPT-4o to find the best break point
                var prompt = $@"You are analyzing a section of a book chapter to find the best place to split it into readable chunks.

The text below is approximately {windowWords.Length} words. I need to split this into a chunk of around 500 words (±100 words flexibility).

Your task:
1. Read through the text and identify natural breaking points (paragraph boundaries, topic shifts, scene breaks)
2. Find the best break point between word 400 and word 600 that:
   - Ends at a paragraph boundary (double newline)
   - Completes a thought or topic
   - Does NOT cut off mid-sentence or mid-paragraph
3. Return ONLY a JSON object with the word index where the break should occur

Text to analyze:
{windowText}

Return format (JSON only, no explanation):
{{
  ""breakWordIndex"": <number between 400 and {windowWords.Length}>
}}";

                // Safely read config values with fallbacks
                int maxTokens = 100; // Default for chunk boundary detection
                double temp = 0.3;   // Default temperature
                try
                {
                    maxTokens = cfg.GetValue<int>("AI:MaxCompletionTokens:ChunkBoundary", 100);
                    temp = cfg.GetValue<double>("AI:Temperature:ChunkBoundary", 0.3);
                }
                catch (Exception configEx)
                {
                    Log.Information("⚠️ Config read error (using defaults): {configEx.Message}");
                }

                var payload = modelHelper.BuildChatCompletionPayload(
                    model,
                    new object[]
                    {
                        new { role = "user", content = prompt }
                    },
                    maxCompletionTokens: maxTokens,
                    temperature: temp
                );

                // Retry logic for rate limiting (429 errors)
                HttpResponseMessage? response = null;
                int maxRetries = 3;
                int retryCount = 0;
                double baseDelaySeconds = 1.5;

                while (retryCount <= maxRetries)
                {
                    response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);

                    if (response.IsSuccessStatusCode)
                    {
                        break; // Success, exit retry loop
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < maxRetries)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        Log.Information("⚠️  Rate limited (attempt {retryCount + 1}/{maxRetries + 1}): {body}");

                        // Extract retry-after time from error message
                        double retryAfterSeconds = baseDelaySeconds * Math.Pow(2, retryCount); // Exponential backoff
                        try
                        {
                            // Parse "Please try again in X.XXs" from error message
                            var match = Regex.Match(body, @"try again in ([\d.]+)s");
                            if (match.Success && double.TryParse(match.Groups[1].Value, out var parsedDelay))
                            {
                                retryAfterSeconds = Math.Max(parsedDelay, retryAfterSeconds);
                            }
                        }
                        catch { /* Use exponential backoff if parsing fails */ }

                        Log.Information("⏳ Waiting {retryAfterSeconds:F2}s before retry...");
                        await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds));
                        retryCount++;
                        continue;
                    }

                    // Non-retryable error
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Log.Information("❌ OpenAI chunk detection failed: {errorBody}");
                    await ServerSentEventsHelper.SendEventAsync(context.Response, new
                    {
                        stage = "error",
                        stepNumber = chunkIndex,
                        totalSteps = estimatedChunks,
                        message = $"Detection failed: {(int)response.StatusCode}"
                    });
                    return;
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    var body = response != null ? await response.Content.ReadAsStringAsync() : "No response";
                    Log.Information("❌ OpenAI chunk detection failed after {maxRetries} retries: {body}");
                    await ServerSentEventsHelper.SendEventAsync(context.Response, new
                    {
                        stage = "error",
                        stepNumber = chunkIndex,
                        totalSteps = estimatedChunks,
                        message = $"Detection failed after {maxRetries} retries (rate limited)"
                    });
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                var aiText = aiResponseParser.ExtractText(doc.RootElement);

                // Track token usage
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                    var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                    var userId = UserHelpers.GetUserIdFromContext(context);
                    if (userId != null)
                        tokenUsage.AddUsage(userId, promptTokens, completionTokens);
                }

                // Parse the break point
                int breakPoint = targetChunkSize; // Default fallback
                if (!string.IsNullOrWhiteSpace(aiText))
                {
                    Log.Information("🤖 AI response: {aiText}");
                    try
                    {
                        // Try to extract JSON from response (handle markdown code blocks)
                        var cleanedText = aiText.Trim();

                        // Remove markdown code blocks if present
                        if (cleanedText.StartsWith("```"))
                        {
                            var lines = cleanedText.Split('\n');
                            cleanedText = string.Join('\n', lines.Skip(1).SkipLast(1));
                        }

                        // Try direct JSON parse first
                        JsonDocument? jsonDoc = null;
                        try
                        {
                            jsonDoc = JsonDocument.Parse(cleanedText);
                        }
                        catch
                        {
                            // Fall back to regex extraction
                            var jsonMatch = Regex.Match(cleanedText, @"\{[^\}]*""breakWordIndex""[^\}]*\}");
                            if (jsonMatch.Success)
                            {
                                jsonDoc = JsonDocument.Parse(jsonMatch.Value);
                            }
                        }

                        if (jsonDoc != null && jsonDoc.RootElement.TryGetProperty("breakWordIndex", out var idx))
                        {
                            breakPoint = idx.GetInt32();
                            // Clamp to valid range
                            breakPoint = Math.Max(400, Math.Min(breakPoint, windowWords.Length));
                            Log.Information("✂️ AI suggested break at word {breakPoint}");
                        }
                        else
                        {
                            Log.Information("⚠️ No breakWordIndex found in response, using default: {breakPoint}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Information("⚠️ Failed to parse break point: {ex.Message}, using default: {breakPoint}");
                    }
                }
                else
                {
                    Log.Information("⚠️ Empty AI response, using default break point: {breakPoint}");
                }

                // If we're still at default (500), try to find a paragraph boundary as fallback
                if (breakPoint == targetChunkSize)
                {
                    Log.Information("⚠️ Using fallback: finding nearest paragraph boundary around word {breakPoint}");

                    // Look for paragraph breaks (double newlines) near the target position
                    var searchStart = Math.Max(400, breakPoint - 50);
                    var searchEnd = Math.Min(windowWords.Length, breakPoint + 50);

                    // Reconstruct text to find paragraph boundaries
                    var searchText = string.Join(" ", windowWords.Skip(searchStart).Take(searchEnd - searchStart));
                    var paragraphs = searchText.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.None);

                    if (paragraphs.Length > 1)
                    {
                        // Find the paragraph break closest to target position
                        var currentPos = searchStart;
                        var bestBreak = breakPoint;
                        var bestDistance = int.MaxValue;

                        foreach (var para in paragraphs.Take(paragraphs.Length - 1)) // Don't include last paragraph
                        {
                            var paraWordCount = para.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                            currentPos += paraWordCount;

                            var distance = Math.Abs(currentPos - breakPoint);
                            if (distance < bestDistance)
                            {
                                bestDistance = distance;
                                bestBreak = currentPos;
                            }
                        }

                        if (bestBreak >= 400 && bestBreak <= windowWords.Length)
                        {
                            breakPoint = bestBreak;
                            Log.Information("✂️ Found paragraph boundary at word {breakPoint} (distance from target: {bestDistance})");
                        }
                    }
                }

                var chunkEnd = currentStart + breakPoint;
                chunks.Add(new ChunkBoundary(currentStart, chunkEnd, chunkEnd - currentStart));
                currentStart = chunkEnd;

                Log.Information("✂️ Chunk detected: words {chunks[^1].Start}-{chunks[^1].End} ({chunks[^1].WordCount} words)");

                // Proactive throttling between chunks to prevent rate limiting
                if (currentStart < totalWords)
                {
                    await AiThrottlingConfiguration.ThrottleBetweenItemsAsync();
                }
            }

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

            Log.Information("✅ Detected {chunks.Count} sections for chapter {chapterId}");
        }
        catch (ArgumentException ex)
        {
            Log.Information("❌ Invalid argument for chunk boundary detection: {Message}", ex.Message);
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "error",
                stepNumber = 0,
                totalSteps = 1,
                message = $"Invalid parameter: {ex.ParamName ?? "unknown"}"
            });
        }
        catch (Exception ex)
        {
            Log.Information("❌ Chunk boundary detection failed: {ex.Message}");
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "error",
                stepNumber = 0,
                totalSteps = 1,
                message = $"Detection failed: {ex.Message}"
            });
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
            return Results.BadRequest(new { error = "dropboxPath is required." });
        if (chapterId < 0 || sectionIndex < 0)
            return Results.BadRequest(new { error = "chapterId and sectionIndex must be zero or positive." });

        var cached = AiContentCache.LoadSectionSummary(dropboxPath, chapterId, sectionIndex);
        if (cached != null)
        {
            // Load associated vocab if it exists
            var vocab = AiContentCache.LoadSectionVocab(dropboxPath, chapterId, sectionIndex);

            // Filter out known AND study words from vocab
            if (vocab != null && vocab.Count > 0)
            {
                Log.Information("🔍 [GET /api/ai/section-summary] Loading {vocab.Count} vocab cards from cache");
                var knownWords = AiContentCache.LoadKnownWords();
                var studyWords = AiContentCache.LoadStudyWordsWithBooks();
                Log.Information("📚 [GET /api/ai/section-summary] Loaded {knownWords.Count} known words and {studyWords.Count} study words from server");

                var beforeCount = vocab.Count;
                var filteredVocab = vocab.Where(card =>
                {
                    var normalized = AiContentCache.NormalizeTerm(card.Term);
                    var isKnown = knownWords.Contains(normalized);
                    var isStudy = studyWords.ContainsKey(normalized);

                    if (isKnown)
                    {
                        Log.Information("  🚫 Filtering out known word: '{card.Term}' (normalized: '{normalized}')");
                    }
                    else if (isStudy)
                    {
                        Log.Information("  🚫 Filtering out study word: '{card.Term}' (normalized: '{normalized}')");
                    }

                    return !isKnown && !isStudy;
                }).ToList();

                var removedCount = beforeCount - filteredVocab.Count;
                Log.Information("✅ [GET /api/ai/section-summary] Filtered vocab: {beforeCount} cards → {filteredVocab.Count} cards (removed {removedCount} known/study words)");
                vocab = filteredVocab;
            }
            else
            {
                Log.Information("ℹ️ [GET /api/ai/section-summary] No vocab to filter (vocab={vocab?.Count ?? 0})");
            }

            // Create new response with filtered vocab included
            var response = cached with { Vocab = vocab };

            Log.Information("✅ Returning cached section summary for chapter {chapterId}, section {sectionIndex} (vocab: {vocab?.Count ?? 0} cards)");
            return Results.Ok(response);
        }

        return Results.NotFound(new { error = "No cached summary found for this section." });
    }

    private static async Task<IResult> HandleGenerateSectionSummary(
        HttpContext context,
        [FromBody] SectionSummaryRequest request,
        DropboxClient dropbox,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IAiJobLockService jobLock)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
            return Results.BadRequest(new { error = "dropboxPath is required." });
        if (request.ChapterId < 0 || request.SectionIndex < 0)
            return Results.BadRequest(new { error = "chapterId and sectionIndex must be zero or positive." });

        // Check if summary already cached
        var cached = AiContentCache.LoadSectionSummary(request.DropboxPath, request.ChapterId, request.SectionIndex);
        if (cached != null)
        {
            Log.Information("✅ Returning cached section summary for chapter {request.ChapterId}, section {request.SectionIndex}");
            return Results.Ok(cached);
        }

        var sectionSummaryLockKey = $"section-summary:{request.DropboxPath}:{request.ChapterId}:{request.SectionIndex}";
        if (!jobLock.TryStartJob(sectionSummaryLockKey))
        {
            return Results.Conflict(new { error = "Section summary already in progress." });
        }

        try
        {
            // Check token limit
            var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
            if (tokenLimitResult is not null) return tokenLimitResult;

            // Load chunk boundaries
            var boundaries = AiContentCache.LoadChunkBoundaries(request.DropboxPath, request.ChapterId);
            if (boundaries == null || request.SectionIndex >= boundaries.Chunks.Count)
                return Results.BadRequest(new { error = "Invalid sectionIndex or chunk boundaries not detected." });

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
                return Results.NotFound(new { error = "Chapter not indexed." });

            var chapterText = await File.ReadAllTextAsync(chapterPath);
            var words = chapterText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            var chunk = boundaries.Chunks[request.SectionIndex];
            var sectionWords = words.Skip(chunk.Start).Take(chunk.WordCount).ToArray();
            var sectionText = string.Join(" ", sectionWords);

            Log.Information("📝 Generating summary for chapter {request.ChapterId}, section {request.SectionIndex} ({chunk.WordCount} words)");

            // Use GPT-5.2 (deep model) for high-quality summaries
            var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Information("❌ OpenAI API key not configured");
                return Results.Problem("OpenAI API key not configured.");
            }

            using var http = httpFactory.CreateClient("OpenAI");
            var model = modelSelection.GetModelDeep();

            Log.Information("🤖 Using model: {model}");
            Log.Information("   Model info: {modelHelper.GetModelDescription(model)}");

            var bookContext = !string.IsNullOrWhiteSpace(request.BookTitle)
                ? $" from the book \"{request.BookTitle}\""
                : "";

            // Build prompt for educational, explanatory summary
            var prompt = $@"You are an expert educator explaining this text section{bookContext} to someone who wants to deeply understand it.

Provide a comprehensive summary that:

1. **What Happens**: Summarize the key events, dialogue, and developments in this section

2. **Explain Concepts**: When you encounter complex ideas, philosophical terms, or specialized vocabulary:
   - Define and explain the concept in accessible language
   - Provide historical or cultural context
   - Explain WHY this concept matters and what problem it addresses
   - Connect abstract ideas to concrete examples

3. **Clarify References**: For any historical, literary, philosophical, or cultural references:
   - Identify who/what is being referenced
   - Explain the significance and context
   - Show how it relates to the current text

4. **Thematic Analysis**: Explain the deeper meaning and themes being explored

Your goal is to make this text comprehensible and meaningful. If the section discusses abstract theory, explain it in plain language. If it references obscure ideas, provide the background needed to understand them. Assume the reader is intelligent but may not be familiar with specialized academic or philosophical concepts.

Keep your summary thorough but focused (2-5 paragraphs depending on complexity).

Text to summarize:
{sectionText}";

            // Safely read config values with fallbacks
            int maxTokens = 2000; // Default for section summary
            double temp = 0.5;    // Default temperature
            try
            {
                maxTokens = cfg.GetValue<int>("AI:MaxCompletionTokens:SectionSummary", 2000);
                temp = cfg.GetValue<double>("AI:Temperature:SectionSummary", 0.5);
            }
            catch (Exception configEx)
            {
                Log.Information("⚠️ Config read error (using defaults): {configEx.Message}");
            }

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new object[]
                {
                    new { role = "user", content = prompt }
                },
                maxCompletionTokens: maxTokens,
                temperature: temp
            );

            Log.Information("📤 Sending request to OpenAI Chat Completions API...");
            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Information("❌ OpenAI section summary failed: {response.StatusCode}");
                Log.Information("   Response body: {body}");
                return Results.Problem($"Section summary failed: {(int)response.StatusCode}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var summary = aiResponseParser.ExtractText(doc.RootElement);
            Log.Information("✅ Summary generated: {summary?.Length ?? 0} characters");

            // Track token usage
            int promptTokens = 0, completionTokens = 0;
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
                Log.Information("📊 Token usage: {promptTokens} prompt + {completionTokens} completion = {promptTokens + completionTokens} total");
            }

            // Save to cache
            var result = new SectionSummaryResponse(
                summary ?? "No summary generated.",
                request.SectionIndex,
                promptTokens,
                completionTokens,
                promptTokens + completionTokens,
                DateTime.UtcNow
            );

            AiContentCache.SaveSectionSummary(request.DropboxPath, request.ChapterId, request.SectionIndex, result);
            Log.Information("💾 Section summary cached successfully");

            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            Log.Information("❌ Invalid argument for section summary: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("❌ Section summary generation failed: {ex.Message}");
            Log.Information("   Stack trace: {ex.StackTrace}");
            return Results.Problem("Failed to generate section summary.");
        }
        finally
        {
            jobLock.EndJob(sectionSummaryLockKey);
        }
    }
}
