using System.Text.Json;
using System.Text.RegularExpressions;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Helpers;
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
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/ai")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/ai/flashcards - Get flashcards for a book
        group.MapGet("/flashcards", HandleGetFlashcards);

        // POST /api/ai/flashcards - Generate flashcards from text
        group.MapPost("/flashcards", HandleCreateFlashcards);

        // DELETE /api/ai/flashcards - Clear all flashcards for a book
        group.MapDelete("/flashcards", HandleClearFlashcards);

        // DELETE /api/ai/flashcard - Delete a single flashcard
        group.MapDelete("/flashcard", HandleDeleteFlashcard);

        return app;
    }

    private static IResult HandleGetFlashcards([FromQuery] string? path, IFlashcardService flashcardService)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ApiResponse.BadRequest("Query parameter 'path' is required.");

        var flashcards = flashcardService.LoadFlashcards(path);
        return Results.Ok(flashcards);
    }

    private static async Task<IResult> HandleCreateFlashcards(
        HttpContext context,
        [FromBody] FlashcardRequest request,
        IAiChatCompletion chat,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IFlashcardService flashcardService)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Term))
            return ApiResponse.BadRequest("Term is required.");

        var shouldSave = request.SaveToLibrary ?? true;
        if (shouldSave && string.IsNullOrWhiteSpace(request.DropboxPath))
            return ApiResponse.BadRequest("dropboxPath is required when saving flashcards.");

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            // Truncate very long passages to avoid overwhelming the model
            var maxInputLength = cfg.GetValue<int>("AI:MaxInputLength");
            var inputText = request.Term.Length > maxInputLength
                ? request.Term.Substring(0, maxInputLength) + "..."
                : request.Term;

            var userPrompt = FlashcardPrompts.UserPrompt(
                inputText, request.BookTitle, request.KnownWords, request.Context);


            var outcome = await chat.CompleteAsync(
                new AiChatCall(
                    Endpoint: "flashcards",
                    // gpt-4o rather than the configured deep model: vocabulary
                    // extraction is a recognition task, and this runs per passage.
                    Model: "gpt-4o",
                    SystemPrompt: FlashcardPrompts.SystemPrompt,
                    UserPrompt: userPrompt,
                    MaxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:LearnMore"),
                    Temperature: cfg.GetValue<double>("AI:Temperature:LearnMore")),
                context);

            if (!outcome.Succeeded) return outcome.Failure!;

            var cardsParsed = ParseFlashcards(outcome.Text ?? "{}");

            if (shouldSave && !string.IsNullOrWhiteSpace(request.DropboxPath))
                MergeIntoLibrary(cardsParsed, request.DropboxPath, flashcardService);

            return Results.Ok(new FlashcardResult(cardsParsed));
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Flashcard create failed");
            return ApiResponse.InternalError("Failed to create flashcard.");
        }
    }

    private static readonly JsonSerializerOptions FlashcardJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads the model's reply as flashcards, degrading rather than throwing.
    ///
    /// Three outcomes, in order of preference: the JSON array it was asked for,
    /// a single object if it returned one card unwrapped, or nothing. Nothing is
    /// deliberate — an earlier version fell back to a card containing the whole
    /// passage, which is how the library filled up with giant unusable entries.
    /// A failed extraction should cost the reader a click, not the library.
    /// </summary>
    private static List<FlashcardItem> ParseFlashcards(string content)
    {
        try
        {
            var cleaned = AiText.StripCodeFences(content);

            // The model sometimes wraps the array in a sentence of explanation.
            var jsonMatch = Regex.Match(cleaned, @"\[[\s\S]*\]");
            if (jsonMatch.Success)
                cleaned = jsonMatch.Value;

            var cards = JsonSerializer.Deserialize<List<FlashcardItem>>(cleaned, FlashcardJson)
                ?? throw new JsonException("Flashcard array deserialised to null");

            Log.Information("Parsed {CardCount} flashcards from the model response", cards.Count);
            return cards;
        }
        catch (Exception arrayEx)
        {
            Log.Warning(arrayEx, "Flashcards did not parse as an array; trying a single card. Response began: {Preview}",
                content[..Math.Min(200, content.Length)]);
        }

        try
        {
            var single = JsonSerializer.Deserialize<FlashcardItem>(content, FlashcardJson);
            if (single != null)
            {
                Log.Information("Parsed a single flashcard");
                return [single];
            }
        }
        catch (Exception singleEx)
        {
            Log.Warning(singleEx, "Flashcards did not parse as a single card either");
        }

        Log.Warning("Returning no flashcards: the model response could not be parsed");
        return [];
    }

    /// <summary>
    /// Adds the new cards to the book's saved set, replacing any card for a term
    /// that is already there. Term match is case-insensitive.
    /// </summary>
    private static void MergeIntoLibrary(
        List<FlashcardItem> cards,
        string dropboxPath,
        IFlashcardService flashcardService)
    {
        var list = flashcardService.LoadFlashcards(dropboxPath);

        foreach (var card in cards)
        {
            var existing = list.FindIndex(x => string.Equals(x.Term, card.Term, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                list[existing] = card;
            else
                list.Add(card);
        }

        flashcardService.SaveFlashcards(dropboxPath, list);
    }

    private static IResult HandleClearFlashcards([FromQuery] string? path, IFlashcardService flashcardService)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ApiResponse.BadRequest("Query parameter 'path' is required.");

        try
        {
            var (_, filePath) = flashcardService.GetFlashcardPath(path);
            if (System.IO.File.Exists(filePath))
                System.IO.File.Delete(filePath);
            return Results.Ok(new { cleared = true });
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Failed to clear flashcards");
            return ApiResponse.InternalError("Failed to clear flashcards.");
        }
    }

    private static IResult HandleDeleteFlashcard([FromQuery] string? path, [FromQuery] string? term, IFlashcardService flashcardService)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ApiResponse.BadRequest("Query parameter 'path' is required.");
        if (string.IsNullOrWhiteSpace(term))
            return ApiResponse.BadRequest("Query parameter 'term' is required.");

        try
        {
            var deleted = flashcardService.DeleteFlashcard(path, term);
            if (deleted)
                return Results.Ok(new { deleted = true });
            return ApiResponse.NotFound("Flashcard not found.");
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Failed to delete flashcard");
            return ApiResponse.InternalError("Failed to delete flashcard.");
        }
    }
}
