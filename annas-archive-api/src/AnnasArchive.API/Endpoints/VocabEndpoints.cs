using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Microsoft.AspNetCore.Mvc;
using Serilog;

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
        Log.Information("🔍 [GET /api/vocab/known] Loading known words from server...");
        var knownWords = AiContentCache.LoadKnownWordsWithBooks();
        Log.Information("📊 [GET /api/vocab/known] Returning {KnownWordsCount} known words with book associations", knownWords.Count);
        return Results.Ok(knownWords);
    }

    private static IResult HandleAddKnownWord([FromBody] AddVocabWordRequest request)
    {
        Log.Information("➕ [POST /api/vocab/known] Request received: term='{Term}', bookId='{BookId}'", request?.Term, request?.BookId);

        if (request is null || string.IsNullOrWhiteSpace(request.Term))
        {
            Log.Information("❌ [POST /api/vocab/known] Invalid request: term is null or empty");
            return Results.BadRequest(new { error = "term is required." });
        }

        var knownWords = AiContentCache.LoadKnownWordsWithBooks();
        var normalized = request.Term.Trim().ToLowerInvariant();
        var bookId = request.BookId ?? "global";
        Log.Information("🔤 [POST /api/vocab/known] Normalized term: '{Normalized}', bookId: '{BookId}'", normalized, bookId);

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
            Log.Information("💾 [POST /api/vocab/known] Saved to file. Term now known in {BooksCount} books", books.Count);
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
                Log.Information("🔄 [POST /api/vocab/known] Removed '{Normalized}' from study list entirely", normalized);
            }
            else
            {
                studyWords[normalized] = studyInfo;
                Log.Information("🔄 [POST /api/vocab/known] Removed book '{BookId}' from study list for '{Normalized}'", bookId, normalized);
            }
            AiContentCache.SaveStudyWordsWithBooks(studyWords);
        }

        Log.Information("✅ [POST /api/vocab/known] Added '{Normalized}' to known words for book '{BookId}' (total: {KnownWordsCount} unique terms)", normalized, bookId, knownWords.Count);
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
            Log.Information("🗑️ Removed '{Normalized}' from known words entirely", normalized);
            return Results.Ok(new { success = true, removed = true, word = normalized, totalKnown = knownWords.Count });
        }

        // DELETE is idempotent: the word is not in the list, which is exactly the
        // state the caller asked for. That is a success, so it no longer reports
        // `success = false` — the flag was the only thing claiming a failure, and
        // nothing reads it (the client types this call as returning nothing).
        // `removed` distinguishes "we took it out" from "it was already gone" for
        // anyone who ever wants to know.
        return Results.Ok(new { success = true, removed = false, word = normalized, totalKnown = knownWords.Count });
    }

    private static IResult HandleGetStudyWords()
    {
        Log.Information("🔍 [GET /api/vocab/study] Loading study words from server...");
        var studyWords = AiContentCache.LoadStudyWordsWithBooks();

        // Convert to API response format
        var response = new Dictionary<string, object>();
        foreach (var kvp in studyWords)
        {
            response[kvp.Key] = new { definition = kvp.Value.definition, books = kvp.Value.books };
        }

        Log.Information("📊 [GET /api/vocab/study] Returning {StudyWordsCount} study words with book associations", studyWords.Count);
        return Results.Ok(response);
    }

    private static IResult HandleAddStudyWord([FromBody] AddStudyWordRequest request)
    {
        Log.Information("➕ [POST /api/vocab/study] Request received: term='{Term}', definition='{Definition}', bookId='{BookId}'",
            request?.Term, request?.Definition, request?.BookId);

        if (request is null || string.IsNullOrWhiteSpace(request.Term))
        {
            Log.Information("❌ [POST /api/vocab/study] Invalid request: term is null or empty");
            return Results.BadRequest(new { error = "term is required." });
        }

        var studyWords = AiContentCache.LoadStudyWordsWithBooks();
        var normalized = request.Term.Trim().ToLowerInvariant();
        var definition = request.Definition?.Trim() ?? "";
        var bookId = request.BookId ?? "global";
        Log.Information("🔤 [POST /api/vocab/study] Normalized term: '{Normalized}', bookId: '{BookId}'", normalized, bookId);

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
        Log.Information("💾 [POST /api/vocab/study] Saved to file. Term now studied in {BooksCount} books", books.Count);

        // Remove from known list if it was there
        var knownWords = AiContentCache.LoadKnownWordsWithBooks();
        if (knownWords.ContainsKey(normalized))
        {
            var knownBooks = knownWords[normalized];
            knownBooks.Remove(bookId);
            if (knownBooks.Count == 0)
            {
                knownWords.Remove(normalized);
                Log.Information("🔄 [POST /api/vocab/study] Removed '{Normalized}' from known list entirely", normalized);
            }
            else
            {
                knownWords[normalized] = knownBooks;
                Log.Information("🔄 [POST /api/vocab/study] Removed book '{BookId}' from known list for '{Normalized}'", bookId, normalized);
            }
            AiContentCache.SaveKnownWordsWithBooks(knownWords);
        }

        Log.Information("✅ [POST /api/vocab/study] Added '{Normalized}' to study list for book '{BookId}' (total: {StudyWordsCount} unique terms)", normalized, bookId, studyWords.Count);
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
            Log.Information("🗑️ Removed '{Normalized}' from study list entirely", normalized);
            return Results.Ok(new { success = true, removed = true, word = normalized, totalStudy = studyWords.Count });
        }

        // Idempotent, same as the known-words delete above.
        return Results.Ok(new { success = true, removed = false, word = normalized, totalStudy = studyWords.Count });
    }

    private static IResult HandleDeleteBookVocab(string bookId)
    {
        Log.Information("🗑️ [DELETE /api/vocab/book/{BookId}] Deleting all vocabulary for book '{BookId}'", bookId, bookId);

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
        Log.Information("🗑️ [DELETE /api/vocab/book/{BookId}] Removed {KnownRemoved} known words (deleted {KnownToRemoveCount} entirely)", bookId, knownRemoved, knownToRemove.Count);

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
        Log.Information("🗑️ [DELETE /api/vocab/book/{BookId}] Removed {StudyRemoved} study words (deleted {StudyToRemoveCount} entirely)", bookId, studyRemoved, studyToRemove.Count);

        Log.Information("✅ [DELETE /api/vocab/book/{BookId}] Cleanup complete: {KnownRemoved} known + {StudyRemoved} study words affected", bookId, knownRemoved, studyRemoved);
        return Results.Ok(new {
            success = true,
            bookId,
            knownWordsAffected = knownRemoved,
            studyWordsAffected = studyRemoved,
            totalRemoved = knownRemoved + studyRemoved
        });
    }
}
