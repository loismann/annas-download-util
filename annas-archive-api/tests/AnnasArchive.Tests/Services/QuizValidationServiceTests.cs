using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using FluentAssertions;

namespace AnnasArchive.Tests.Services;

public class QuizValidationServiceTests
{
    private readonly QuizValidationService _service = new();

    [Fact]
    public void ValidateSubject_ReturnsErrorsForMissingOptions()
    {
        var subject = new QuizSubject
        {
            Id = "science",
            Title = "Science",
            QuestionSets = new List<QuizQuestionSet>
            {
                new()
                {
                    Id = "core",
                    Title = "Core",
                    Questions = new List<QuizQuestion>
                    {
                        new()
                        {
                            Id = "science-001",
                            Type = "multiple-choice",
                            Prompt = "Test question?",
                            CorrectOptionIds = new List<string> { "a" }
                        }
                    }
                }
            }
        };

        var result = _service.ValidateSubject(subject);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("must include answer options", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateSubject_AllowsShortAnswerWithAcceptedAnswers()
    {
        var subject = new QuizSubject
        {
            Id = "science",
            Title = "Science",
            QuestionSets = new List<QuizQuestionSet>
            {
                new()
                {
                    Id = "core",
                    Title = "Core",
                    Questions = new List<QuizQuestion>
                    {
                        new()
                        {
                            Id = "science-002",
                            Type = "short-answer",
                            Prompt = "What is 2+2?",
                            AcceptedAnswers = new List<string> { "4", "four" }
                        }
                    }
                }
            }
        };

        var result = _service.ValidateSubject(subject);

        result.IsValid.Should().BeTrue();
    }
}
