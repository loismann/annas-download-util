using System.Text.Json;
using System.Text.RegularExpressions;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping AI Flashcards endpoints.
/// </summary>
public static class AiFlashcardsEndpoints
{
    /// <summary>
    /// Maps AI Flashcards endpoints to the application.
    /// </summary>
    public static WebApplication MapAiFlashcardsEndpoints(this WebApplication app)
    {
        // GET /api/ai/flashcards - Get flashcards for a book
        app.MapGet("/api/ai/flashcards", HandleGetFlashcards)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/flashcards - Generate flashcards from text
        app.MapPost("/api/ai/flashcards", HandleCreateFlashcards)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/ai/flashcards - Clear all flashcards for a book
        app.MapDelete("/api/ai/flashcards", HandleClearFlashcards)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/ai/flashcard - Delete a single flashcard
        app.MapDelete("/api/ai/flashcard", HandleDeleteFlashcard)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleGetFlashcards([FromQuery] string path, IFlashcardService flashcardService)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "Query parameter 'path' is required." });

        var flashcards = flashcardService.LoadFlashcards(path);
        return Results.Ok(flashcards);
    }

    private static async Task<IResult> HandleCreateFlashcards(
        HttpContext context,
        [FromBody] FlashcardRequest request,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IFlashcardService flashcardService)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Term))
            return Results.BadRequest(new { error = "Term is required." });

        var shouldSave = request.SaveToLibrary ?? true;
        if (shouldSave && string.IsNullOrWhiteSpace(request.DropboxPath))
            return Results.BadRequest(new { error = "dropboxPath is required when saving flashcards." });

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            using var http = httpFactory.CreateClient("OpenAI");

            // Truncate very long passages to avoid overwhelming the model
            var maxInputLength = cfg.GetValue<int>("AI:MaxInputLength");
            var inputText = request.Term.Length > maxInputLength
                ? request.Term.Substring(0, maxInputLength) + "..."
                : request.Term;

            var systemPrompt = @"You are a vocabulary flashcard generator. Your job is to extract INDIVIDUAL WORDS or SHORT PHRASES from text and create a separate flashcard for EACH ONE.

CRITICAL: Extract MULTIPLE individual terms from the passage. DO NOT create a single flashcard with the entire passage. Each flashcard should be for ONE specific word or short phrase.

Return ONLY valid JSON, no markdown or explanation.

JSON Structure (ARRAY of flashcards):
[
  { ""term"": ""audacity"", ""definition"": ""bold or rude behavior"", ""etymology"": ""Latin audax (bold)"", ""usageExamples"": [""She had the audacity to criticize."", ""His audacity was shocking.""], ""notes"": """" },
  { ""term"": ""rhizome"", ""definition"": ""(philosophy) a non-hierarchical network structure, as opposed to a tree-like hierarchy"", ""etymology"": ""Greek rhizoma (mass of roots)"", ""usageExamples"": [""Deleuze uses rhizome as a metaphor."", ""A rhizomatic structure has no center.""], ""notes"": ""Specific philosophical meaning by Deleuze & Guattari"" },
  ...
]

What to extract (BE VERY SELECTIVE):
- College-level or graduate-level vocabulary (words beyond typical high school reading)
- Foreign words/phrases used in the text
- Specialized academic, philosophical, or technical terms
- Subject-specific jargon that requires domain knowledge
- Neologisms or terms with specialized meaning in this work (e.g., philosophy terms that are also common English words but have specific meaning here)
- Archaic or literary words rarely used in modern English
- Historical/cultural references requiring background knowledge

DO NOT extract:
- Common words that high school students would know (e.g., ""said"", ""walked"", ""important"", ""although"", ""necessary"")
- Basic academic words taught in high school (e.g., ""analyze"", ""demonstrate"", ""significant"")
- Simple vocabulary regardless of context

BE STRICT: Only select words that would genuinely challenge someone with a high school education or require specific domain knowledge.

Rules:
- Extract 3-10 individual terms from the passage (fewer is better than including common words)
- Each term should be a SINGLE WORD or SHORT PHRASE (2-4 words max)
- Definitions: 1-2 sentences, clear and concise (include subject-specific meaning if applicable)
- Usage examples: 2 brief sentences showing the word in context
- Etymology: Short phrase (""Unknown"" if unclear)
- Notes: Include context if the word has a specific meaning in this discipline/work";

            var knownWordsContext = request.KnownWords != null && request.KnownWords.Count > 0
                ? $"\n\nEXCLUDE these words (user already knows them): {string.Join(", ", request.KnownWords)}"
                : "";

            // Add custom context instructions if provided (for intelligent selection handling)
            var customInstructions = !string.IsNullOrWhiteSpace(request.Context)
                ? $"\n\nSPECIAL INSTRUCTIONS:\n{request.Context}\n"
                : "";

            var userPrompt = $@"Extract vocabulary terms from this passage:

""{inputText}""

Context: {request.BookTitle ?? "Unknown book"}{knownWordsContext}{customInstructions}

Return JSON array of flashcards for individual terms found in the passage.";

            var model = "gpt-4o"; // Use GPT-4o for cost-effective vocab extraction
            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:LearnMore"),
                temperature: cfg.GetValue<double>("AI:Temperature:LearnMore")
            );

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Information("❌ OpenAI flashcard failed status={(int)response.StatusCode} body={body}");
                return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var content = aiResponseParser.ExtractText(doc.RootElement) ?? "{}";

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            List<FlashcardItem> cardsParsed;
            try
            {
                // Try to clean the content first - remove markdown code blocks if present
                var cleanedContent = content.Trim();
                if (cleanedContent.StartsWith("```"))
                {
                    var lines = cleanedContent.Split('\n');
                    cleanedContent = string.Join('\n', lines.Skip(1).SkipLast(1));
                }

                // Try to extract JSON array from the content
                var jsonMatch = Regex.Match(cleanedContent, @"\[[\s\S]*\]");
                if (jsonMatch.Success)
                {
                    cleanedContent = jsonMatch.Value;
                }

                cardsParsed = JsonSerializer.Deserialize<List<FlashcardItem>>(cleanedContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new Exception("Invalid flashcard JSON array");

                Log.Information("✅ Successfully parsed {cardsParsed.Count} flashcards from AI response");
            }
            catch (Exception parseEx)
            {
                Log.Information("⚠️ Failed to parse flashcards as array: {parseEx.Message}");
                Log.Information("   AI response: {content.Substring(0, Math.Min(200, content.Length))}...");

                try
                {
                    // Try parsing as a single object
                    var single = JsonSerializer.Deserialize<FlashcardItem>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (single != null)
                    {
                        cardsParsed = new List<FlashcardItem> { single };
                        Log.Information("✅ Parsed single flashcard");
                    }
                    else
                        throw new Exception("Invalid flashcard JSON");
                }
                catch (Exception singleEx)
                {
                    Log.Information("❌ Failed to parse flashcards: {singleEx.Message}");
                    // Don't create a fallback card - return empty list
                    // This prevents creating giant vocab cards with entire text
                    cardsParsed = new List<FlashcardItem>();
                    Log.Information("⚠️ Returning empty flashcard list due to parsing failure");
                }
            }

            if (shouldSave && !string.IsNullOrWhiteSpace(request.DropboxPath))
            {
                var list = flashcardService.LoadFlashcards(request.DropboxPath);
                foreach (var card in cardsParsed)
                {
                    var existing = list.FindIndex(x => string.Equals(x.Term, card.Term, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0)
                        list[existing] = card;
                    else
                        list.Add(card);
                }

                flashcardService.SaveFlashcards(request.DropboxPath, list);
            }
            return Results.Ok(new FlashcardResult(cardsParsed));
        }
        catch (Exception ex)
        {
            Log.Information("❌ Flashcard create failed: {ex.Message}");
            return ApiResponse.InternalError("Failed to create flashcard.");
        }
    }

    private static IResult HandleClearFlashcards([FromQuery] string path, IFlashcardService flashcardService)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "Query parameter 'path' is required." });

        try
        {
            var (_, filePath) = flashcardService.GetFlashcardPath(path);
            if (System.IO.File.Exists(filePath))
                System.IO.File.Delete(filePath);
            return Results.Ok(new { cleared = true });
        }
        catch
        {
            return ApiResponse.InternalError("Failed to clear flashcards.");
        }
    }

    private static IResult HandleDeleteFlashcard([FromQuery] string path, [FromQuery] string term, IFlashcardService flashcardService)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "Query parameter 'path' is required." });
        if (string.IsNullOrWhiteSpace(term))
            return Results.BadRequest(new { error = "Query parameter 'term' is required." });

        try
        {
            var deleted = flashcardService.DeleteFlashcard(path, term);
            if (deleted)
                return Results.Ok(new { deleted = true });
            return Results.NotFound(new { error = "Flashcard not found." });
        }
        catch
        {
            return ApiResponse.InternalError("Failed to delete flashcard.");
        }
    }
}
