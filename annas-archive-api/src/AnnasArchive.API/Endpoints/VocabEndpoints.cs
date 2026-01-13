using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Microsoft.AspNetCore.Mvc;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Vocabulary-related endpoints.
/// </summary>
public static class VocabEndpoints
{
    /// <summary>
    /// Maps Vocabulary endpoints to the application.
    /// </summary>
    public static WebApplication MapVocabEndpoints(this WebApplication app)
    {
        // GET /api/vocab/known - Get known vocabulary words with book associations
        app.MapGet("/api/vocab/known", HandleGetKnownWords)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/vocab/known - Add word to known list with book association
        app.MapPost("/api/vocab/known", HandleAddKnownWord)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/vocab/known/{term} - Remove word from known list
        app.MapDelete("/api/vocab/known/{term}", HandleRemoveKnownWord)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/vocab/study - Get study vocabulary words with book associations
        app.MapGet("/api/vocab/study", HandleGetStudyWords)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/vocab/study - Add word to study list with book association
        app.MapPost("/api/vocab/study", HandleAddStudyWord)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/vocab/study/{term} - Remove word from study list
        app.MapDelete("/api/vocab/study/{term}", HandleRemoveStudyWord)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/vocab/book/{bookId} - Delete all vocabulary words for a specific book
        app.MapDelete("/api/vocab/book/{bookId}", HandleDeleteBookVocab)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleGetKnownWords()
    {
        Console.WriteLine("🔍 [GET /api/vocab/known] Loading known words from server...");
        var knownWords = AiContentCache.LoadKnownWordsWithBooks();
        Console.WriteLine($"📊 [GET /api/vocab/known] Returning {knownWords.Count} known words with book associations");
        return Results.Ok(knownWords);
    }

    private static IResult HandleAddKnownWord([FromBody] AddVocabWordRequest request)
    {
        Console.WriteLine($"➕ [POST /api/vocab/known] Request received: term='{request?.Term}', bookId='{request?.BookId}'");

        if (request is null || string.IsNullOrWhiteSpace(request.Term))
        {
            Console.WriteLine("❌ [POST /api/vocab/known] Invalid request: term is null or empty");
            return Results.BadRequest(new { error = "term is required." });
        }

        var knownWords = AiContentCache.LoadKnownWordsWithBooks();
        var normalized = request.Term.Trim().ToLowerInvariant();
        var bookId = request.BookId ?? "global";
        Console.WriteLine($"🔤 [POST /api/vocab/known] Normalized term: '{normalized}', bookId: '{bookId}'");

        // Get or create the list of books for this term
        if (!knownWords.ContainsKey(normalized))
        {
            knownWords[normalized] = new List<string>();
        }

        var books = knownWords[normalized];
        var wasNew = !books.Contains(bookId);
        if (wasNew)
        {
            books.Add(bookId);
            AiContentCache.SaveKnownWordsWithBooks(knownWords);
            Console.WriteLine($"💾 [POST /api/vocab/known] Saved to file. Term now known in {books.Count} books");
        }

        // Remove from study list if it was there
        var studyWords = AiContentCache.LoadStudyWordsWithBooks();
        if (studyWords.ContainsKey(normalized))
        {
            var studyInfo = studyWords[normalized];
            studyInfo.books.Remove(bookId);
            if (studyInfo.books.Count == 0)
            {
                studyWords.Remove(normalized);
                Console.WriteLine($"🔄 [POST /api/vocab/known] Removed '{normalized}' from study list entirely");
            }
            else
            {
                studyWords[normalized] = studyInfo;
                Console.WriteLine($"🔄 [POST /api/vocab/known] Removed book '{bookId}' from study list for '{normalized}'");
            }
            AiContentCache.SaveStudyWordsWithBooks(studyWords);
        }

        Console.WriteLine($"✅ [POST /api/vocab/known] Added '{normalized}' to known words for book '{bookId}' (total: {knownWords.Count} unique terms)");
        return Results.Ok(new { success = true, word = normalized, bookId, totalKnown = knownWords.Count, wasNew });
    }

    private static IResult HandleRemoveKnownWord(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Results.BadRequest(new { error = "term is required." });

        var knownWords = AiContentCache.LoadKnownWordsWithBooks();
        var normalized = term.Trim().ToLowerInvariant();

        if (knownWords.Remove(normalized))
        {
            AiContentCache.SaveKnownWordsWithBooks(knownWords);
            Console.WriteLine($"🗑️ Removed '{normalized}' from known words entirely");
            return Results.Ok(new { success = true, word = normalized, totalKnown = knownWords.Count });
        }

        return Results.Ok(new { success = false, word = normalized, message = "Word was not in known list" });
    }

    private static IResult HandleGetStudyWords()
    {
        Console.WriteLine("🔍 [GET /api/vocab/study] Loading study words from server...");
        var studyWords = AiContentCache.LoadStudyWordsWithBooks();

        // Convert to API response format
        var response = new Dictionary<string, object>();
        foreach (var kvp in studyWords)
        {
            response[kvp.Key] = new { definition = kvp.Value.definition, books = kvp.Value.books };
        }

        Console.WriteLine($"📊 [GET /api/vocab/study] Returning {studyWords.Count} study words with book associations");
        return Results.Ok(response);
    }

    private static IResult HandleAddStudyWord([FromBody] AddStudyWordRequest request)
    {
        Console.WriteLine($"➕ [POST /api/vocab/study] Request received: term='{request?.Term}', definition='{request?.Definition}', bookId='{request?.BookId}'");

        if (request is null || string.IsNullOrWhiteSpace(request.Term))
        {
            Console.WriteLine("❌ [POST /api/vocab/study] Invalid request: term is null or empty");
            return Results.BadRequest(new { error = "term is required." });
        }

        var studyWords = AiContentCache.LoadStudyWordsWithBooks();
        var normalized = request.Term.Trim().ToLowerInvariant();
        var definition = request.Definition?.Trim() ?? "";
        var bookId = request.BookId ?? "global";
        Console.WriteLine($"🔤 [POST /api/vocab/study] Normalized term: '{normalized}', bookId: '{bookId}'");

        // Get or create the entry for this term
        if (!studyWords.ContainsKey(normalized))
        {
            studyWords[normalized] = (definition, new List<string>());
        }

        var (existingDef, books) = studyWords[normalized];
        var wasNew = !books.Contains(bookId);
        if (wasNew)
        {
            books.Add(bookId);
        }
        // Update definition if provided (use most recent)
        if (!string.IsNullOrWhiteSpace(definition))
        {
            existingDef = definition;
        }
        studyWords[normalized] = (existingDef, books);

        AiContentCache.SaveStudyWordsWithBooks(studyWords);
        Console.WriteLine($"💾 [POST /api/vocab/study] Saved to file. Term now studied in {books.Count} books");

        // Remove from known list if it was there
        var knownWords = AiContentCache.LoadKnownWordsWithBooks();
        if (knownWords.ContainsKey(normalized))
        {
            var knownBooks = knownWords[normalized];
            knownBooks.Remove(bookId);
            if (knownBooks.Count == 0)
            {
                knownWords.Remove(normalized);
                Console.WriteLine($"🔄 [POST /api/vocab/study] Removed '{normalized}' from known list entirely");
            }
            else
            {
                knownWords[normalized] = knownBooks;
                Console.WriteLine($"🔄 [POST /api/vocab/study] Removed book '{bookId}' from known list for '{normalized}'");
            }
            AiContentCache.SaveKnownWordsWithBooks(knownWords);
        }

        Console.WriteLine($"✅ [POST /api/vocab/study] Added '{normalized}' to study list for book '{bookId}' (total: {studyWords.Count} unique terms)");
        return Results.Ok(new { success = true, word = normalized, definition = existingDef, bookId, totalStudy = studyWords.Count, wasNew });
    }

    private static IResult HandleRemoveStudyWord(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Results.BadRequest(new { error = "term is required." });

        var studyWords = AiContentCache.LoadStudyWordsWithBooks();
        var normalized = term.Trim().ToLowerInvariant();

        if (studyWords.Remove(normalized))
        {
            AiContentCache.SaveStudyWordsWithBooks(studyWords);
            Console.WriteLine($"🗑️ Removed '{normalized}' from study list entirely");
            return Results.Ok(new { success = true, word = normalized, totalStudy = studyWords.Count });
        }

        return Results.Ok(new { success = false, word = normalized, message = "Word was not in study list" });
    }

    private static IResult HandleDeleteBookVocab(string bookId)
    {
        Console.WriteLine($"🗑️ [DELETE /api/vocab/book/{bookId}] Deleting all vocabulary for book '{bookId}'");

        if (string.IsNullOrWhiteSpace(bookId))
        {
            return Results.BadRequest(new { error = "bookId is required." });
        }

        int knownRemoved = 0;
        int studyRemoved = 0;

        // Remove book from known words
        var knownWords = AiContentCache.LoadKnownWordsWithBooks();
        var knownToRemove = new List<string>();

        foreach (var (term, books) in knownWords)
        {
            if (books.Remove(bookId))
            {
                knownRemoved++;
                if (books.Count == 0)
                {
                    knownToRemove.Add(term);
                }
            }
        }

        foreach (var term in knownToRemove)
        {
            knownWords.Remove(term);
        }

        AiContentCache.SaveKnownWordsWithBooks(knownWords);
        Console.WriteLine($"🗑️ [DELETE /api/vocab/book/{bookId}] Removed {knownRemoved} known words (deleted {knownToRemove.Count} entirely)");

        // Remove book from study words
        var studyWords = AiContentCache.LoadStudyWordsWithBooks();
        var studyToRemove = new List<string>();

        foreach (var (term, info) in studyWords)
        {
            if (info.books.Remove(bookId))
            {
                studyRemoved++;
                if (info.books.Count == 0)
                {
                    studyToRemove.Add(term);
                }
                else
                {
                    studyWords[term] = info;
                }
            }
        }

        foreach (var term in studyToRemove)
        {
            studyWords.Remove(term);
        }

        AiContentCache.SaveStudyWordsWithBooks(studyWords);
        Console.WriteLine($"🗑️ [DELETE /api/vocab/book/{bookId}] Removed {studyRemoved} study words (deleted {studyToRemove.Count} entirely)");

        Console.WriteLine($"✅ [DELETE /api/vocab/book/{bookId}] Cleanup complete: {knownRemoved} known + {studyRemoved} study words affected");
        return Results.Ok(new {
            success = true,
            bookId,
            knownWordsAffected = knownRemoved,
            studyWordsAffected = studyRemoved,
            totalRemoved = knownRemoved + studyRemoved
        });
    }
}
