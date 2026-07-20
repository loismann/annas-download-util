using System.Text.RegularExpressions;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services;

public interface IQuizValidationService
{
    QuizValidationResult ValidateSubject(QuizSubject subject);
}

public class QuizValidationService : IQuizValidationService
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "multiple-choice",
        "multi-select",
        "true-false",
        "short-answer"
    };

    public QuizValidationResult ValidateSubject(QuizSubject subject)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(subject.Id) || !IdPattern.IsMatch(subject.Id))
            errors.Add("Subject id is required and must be lowercase alphanumeric with dashes/underscores.");

        if (string.IsNullOrWhiteSpace(subject.Title))
            errors.Add("Subject title is required.");

        if (subject.QuestionSets.Count == 0)
            errors.Add("Subject must include at least one question set.");

        var questionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var set in subject.QuestionSets)
        {
            if (string.IsNullOrWhiteSpace(set.Id) || !IdPattern.IsMatch(set.Id))
                errors.Add($"Question set id '{set.Id}' is invalid.");

            foreach (var question in set.Questions)
            {
                if (string.IsNullOrWhiteSpace(question.Id))
                    errors.Add("Question id is required.");
                else if (!questionIds.Add(question.Id))
                    errors.Add($"Duplicate question id '{question.Id}'.");

                if (string.IsNullOrWhiteSpace(question.Prompt))
                    errors.Add($"Question '{question.Id}' is missing a prompt.");

                if (!AllowedTypes.Contains(question.Type))
                    errors.Add($"Question '{question.Id}' has unsupported type '{question.Type}'.");

                if (string.Equals(question.Type, "short-answer", StringComparison.OrdinalIgnoreCase))
                {
                    if (question.AcceptedAnswers.Count == 0)
                        errors.Add($"Short answer question '{question.Id}' must include accepted answers.");
                }
                else
                {
                    if (question.Options.Count == 0)
                        errors.Add($"Question '{question.Id}' must include answer options.");

                    if (question.CorrectOptionIds.Count == 0)
                        errors.Add($"Question '{question.Id}' must include correct option ids.");

                    var optionIds = new HashSet<string>(question.Options.Select(o => o.Id), StringComparer.OrdinalIgnoreCase);
                    foreach (var correctId in question.CorrectOptionIds)
                    {
                        if (!optionIds.Contains(correctId))
                            errors.Add($"Question '{question.Id}' has correct option id '{correctId}' not found in options.");
                    }
                }
            }
        }

        return new QuizValidationResult { Errors = errors };
    }
}
