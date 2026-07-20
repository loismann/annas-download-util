using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services;

public class QuizStorageServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly QuizStorageService _service;

    public QuizStorageServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"quiz-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempRoot);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Quiz:StoragePath"] = _tempRoot
            })
            .Build();
        _service = new QuizStorageService(config, new QuizValidationService());
    }

    [Fact]
    public async Task SaveAndLoadSubject_WritesIndexAndSubject()
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
                            Options = new List<QuizOption>
                            {
                                new() { Id = "a", Text = "A" },
                                new() { Id = "b", Text = "B" }
                            },
                            CorrectOptionIds = new List<string> { "a" }
                        }
                    }
                }
            }
        };

        var saved = await _service.SaveSubjectAsync(subject.Id, subject);
        var loaded = await _service.GetSubjectAsync(subject.Id);
        var index = await _service.GetIndexAsync();

        loaded.Should().NotBeNull();
        loaded!.Title.Should().Be("Science");
        saved.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        index.Subjects.Should().ContainSingle(s => s.Id == "science" && s.QuestionCount == 1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
