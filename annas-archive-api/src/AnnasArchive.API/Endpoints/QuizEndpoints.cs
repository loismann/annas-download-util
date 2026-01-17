using AnnasArchive.API.Models;
using AnnasArchive.API.Services;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping quiz-related endpoints.
/// </summary>
public static class QuizEndpoints
{
    /// <summary>
    /// Maps quiz endpoints to the application (admin only).
    /// </summary>
    public static WebApplication MapQuizEndpoints(this WebApplication app)
    {
        var quizGroup = app.MapGroup("/api/quiz")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        quizGroup.MapGet("/subjects", async (IQuizStorageService storage, CancellationToken token) =>
        {
            var index = await storage.GetIndexAsync(token);
            return Results.Ok(index);
        });

        quizGroup.MapGet("/subjects/{subjectId}", async (string subjectId, IQuizStorageService storage, CancellationToken token) =>
        {
            var subject = await storage.GetSubjectAsync(subjectId, token);
            return subject == null ? Results.NotFound() : Results.Ok(subject);
        });

        quizGroup.MapPut("/subjects/{subjectId}", async (
            string subjectId,
            QuizSubject subject,
            IQuizStorageService storage,
            IQuizValidationService validator,
            CancellationToken token) =>
        {
            if (!string.Equals(subjectId, subject.Id, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Subject id in route must match payload id." });

            var validation = validator.ValidateSubject(subject);
            if (!validation.IsValid)
                return Results.BadRequest(new { errors = validation.Errors });

            try
            {
                var saved = await storage.SaveSubjectAsync(subjectId, subject, token);
                return Results.Ok(saved);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        quizGroup.MapDelete("/subjects/{subjectId}", async (string subjectId, IQuizStorageService storage, CancellationToken token) =>
        {
            var deleted = await storage.DeleteSubjectAsync(subjectId, token);
            return deleted ? Results.Ok(new { removed = true }) : Results.NotFound();
        });

        quizGroup.MapGet("/invalid-questions", async (IQuizStorageService storage, CancellationToken token) =>
        {
            var invalidQuestions = await storage.GetInvalidQuestionsAsync(token);
            return Results.Ok(invalidQuestions);
        });

        quizGroup.MapPost("/subjects/{subjectId}/questions/{questionId}/mark-invalid", async (
            string subjectId,
            string questionId,
            MarkInvalidRequest? request,
            IQuizStorageService storage,
            CancellationToken token) =>
        {
            var success = await storage.MarkQuestionInvalidAsync(subjectId, questionId, request?.Reason, token);
            return success ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        return app;
    }
}

public record MarkInvalidRequest
{
    public string? Reason { get; init; }
}
