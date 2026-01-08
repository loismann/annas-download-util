using System.Text.Json.Serialization;

namespace AnnasArchive.API.Models;

public record QuizIndex
{
    public int Version { get; init; } = 1;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    public List<QuizSubjectSummary> Subjects { get; init; } = new();
}

public record QuizSubjectSummary
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int QuestionCount { get; init; }
    public string? DefaultModeId { get; init; }
    public List<string> Tags { get; init; } = new();
}

public record QuizSubject
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Version { get; init; } = 1;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    public string? DefaultModeId { get; init; }
    public List<QuizMode> Modes { get; init; } = new();
    public List<QuizQuestionSet> QuestionSets { get; init; } = new();
}

public record QuizMode
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public int? QuestionCount { get; init; }
    public bool ShuffleQuestions { get; init; } = true;
    public bool ShuffleOptions { get; init; } = true;
    public int? TimeLimitSeconds { get; init; }
    public bool ShowFeedback { get; init; } = true;
    public bool AllowReview { get; init; } = true;
}

public record QuizQuestionSet
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<QuizQuestion> Questions { get; init; } = new();
}

public record QuizQuestion
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public List<QuizOption> Options { get; init; } = new();
    public List<string> CorrectOptionIds { get; init; } = new();
    public List<string> AcceptedAnswers { get; init; } = new();
    public string? Explanation { get; init; }
    public List<string> Tags { get; init; } = new();
    public string? Difficulty { get; init; }
    public double? Points { get; init; }
}

public record QuizOption
{
    public string Id { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
}

public record QuizValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; init; } = new();
}
