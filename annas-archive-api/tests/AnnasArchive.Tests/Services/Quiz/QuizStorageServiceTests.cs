using System.Text.Json;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AnnasArchive.Tests.Services.Quiz;

public class QuizStorageServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly QuizStorageService _service;
    private readonly QuizValidationService _validator = new();

    public QuizStorageServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"QuizStorageTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Quiz:StoragePath"] = _testRoot
            })
            .Build();

        _service = new QuizStorageService(config, _validator);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }

    private QuizSubject CreateTestSubject(string id, string title, int questionCount = 5)
    {
        var questions = Enumerable.Range(1, questionCount)
            .Select(i => new QuizQuestion
            {
                Id = $"{id}-{i:D3}",
                Type = "multiple-choice",
                Prompt = $"Question {i}?",
                Options = new List<QuizOption>
                {
                    new() { Id = "a", Text = "Option A" },
                    new() { Id = "b", Text = "Option B" },
                    new() { Id = "c", Text = "Option C" }
                },
                CorrectOptionIds = new List<string> { "a" }
            })
            .ToList();

        return new QuizSubject
        {
            Id = id,
            Title = title,
            QuestionSets = new List<QuizQuestionSet>
            {
                new()
                {
                    Id = "core",
                    Title = "Core Questions",
                    Questions = questions
                }
            }
        };
    }

    #region GetIndexAsync Tests

    [Fact]
    public async Task GetIndexAsync_WithNoSubjects_ReturnsEmptyIndex()
    {
        var index = await _service.GetIndexAsync();

        Assert.NotNull(index);
        Assert.Empty(index.Subjects);
    }

    [Fact]
    public async Task GetIndexAsync_AfterSavingSubject_ReturnsSubjectInIndex()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");
        await _service.SaveSubjectAsync("test-subject", subject);

        var index = await _service.GetIndexAsync();

        Assert.Single(index.Subjects);
        Assert.Equal("test-subject", index.Subjects[0].Id);
        Assert.Equal("Test Subject", index.Subjects[0].Title);
        Assert.Equal(5, index.Subjects[0].QuestionCount);
    }

    #endregion

    #region GetSubjectAsync Tests

    [Fact]
    public async Task GetSubjectAsync_WithNonexistentId_ReturnsNull()
    {
        var subject = await _service.GetSubjectAsync("nonexistent");
        Assert.Null(subject);
    }

    [Fact]
    public async Task GetSubjectAsync_AfterSave_ReturnsSubject()
    {
        var original = CreateTestSubject("test-subject", "Test Subject");
        await _service.SaveSubjectAsync("test-subject", original);

        var retrieved = await _service.GetSubjectAsync("test-subject");

        Assert.NotNull(retrieved);
        Assert.Equal("test-subject", retrieved.Id);
        Assert.Equal("Test Subject", retrieved.Title);
        Assert.Single(retrieved.QuestionSets);
        Assert.Equal(5, retrieved.QuestionSets[0].Questions.Count);
    }

    #endregion

    #region SaveSubjectAsync Tests

    [Fact]
    public async Task SaveSubjectAsync_CreatesSubjectFile()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");

        await _service.SaveSubjectAsync("test-subject", subject);

        var filePath = Path.Combine(_testRoot, "subjects", "test-subject.json");
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task SaveSubjectAsync_UpdatesExistingSubject()
    {
        var original = CreateTestSubject("test-subject", "Original Title");
        await _service.SaveSubjectAsync("test-subject", original);

        var updated = CreateTestSubject("test-subject", "Updated Title");
        await _service.SaveSubjectAsync("test-subject", updated);

        var retrieved = await _service.GetSubjectAsync("test-subject");
        Assert.Equal("Updated Title", retrieved?.Title);
    }

    [Fact]
    public async Task SaveSubjectAsync_SetsUpdatedAtTimestamp()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");
        var before = DateTime.UtcNow.AddSeconds(-1);

        var saved = await _service.SaveSubjectAsync("test-subject", subject);

        Assert.True(saved.UpdatedAt >= before);
        Assert.True(saved.UpdatedAt <= DateTime.UtcNow.AddSeconds(1));
    }

    #endregion

    #region DeleteSubjectAsync Tests

    [Fact]
    public async Task DeleteSubjectAsync_WithNonexistentId_ReturnsFalse()
    {
        var result = await _service.DeleteSubjectAsync("nonexistent");
        Assert.False(result);
    }

    [Fact]
    public async Task DeleteSubjectAsync_WithExistingSubject_ReturnsTrue()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");
        await _service.SaveSubjectAsync("test-subject", subject);

        var result = await _service.DeleteSubjectAsync("test-subject");

        Assert.True(result);
    }

    [Fact]
    public async Task DeleteSubjectAsync_RemovesSubjectFile()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");
        await _service.SaveSubjectAsync("test-subject", subject);

        await _service.DeleteSubjectAsync("test-subject");

        var filePath = Path.Combine(_testRoot, "subjects", "test-subject.json");
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task DeleteSubjectAsync_RemovesFromIndex()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");
        await _service.SaveSubjectAsync("test-subject", subject);

        await _service.DeleteSubjectAsync("test-subject");

        var index = await _service.GetIndexAsync();
        Assert.Empty(index.Subjects);
    }

    #endregion

    #region GetInvalidQuestionsAsync Tests

    [Fact]
    public async Task GetInvalidQuestionsAsync_WithNoInvalidQuestions_ReturnsEmptyFile()
    {
        var result = await _service.GetInvalidQuestionsAsync();

        Assert.NotNull(result);
        Assert.Empty(result.Questions);
    }

    #endregion

    #region MarkQuestionInvalidAsync Tests

    [Fact]
    public async Task MarkQuestionInvalidAsync_WithNonexistentSubject_ReturnsFalse()
    {
        var result = await _service.MarkQuestionInvalidAsync("nonexistent", "q-001", "Bad question");
        Assert.False(result);
    }

    [Fact]
    public async Task MarkQuestionInvalidAsync_WithNonexistentQuestion_ReturnsFalse()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");
        await _service.SaveSubjectAsync("test-subject", subject);

        var result = await _service.MarkQuestionInvalidAsync("test-subject", "nonexistent-question", "Bad question");

        Assert.False(result);
    }

    [Fact]
    public async Task MarkQuestionInvalidAsync_WithValidQuestion_ReturnsTrue()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");
        await _service.SaveSubjectAsync("test-subject", subject);

        var result = await _service.MarkQuestionInvalidAsync("test-subject", "test-subject-001", "Bad question");

        Assert.True(result);
    }

    [Fact]
    public async Task MarkQuestionInvalidAsync_MovesQuestionToInvalidFile()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");
        await _service.SaveSubjectAsync("test-subject", subject);

        await _service.MarkQuestionInvalidAsync("test-subject", "test-subject-001", "Bad question");

        var invalidQuestions = await _service.GetInvalidQuestionsAsync();
        Assert.Single(invalidQuestions.Questions);
        Assert.Equal("test-subject-001", invalidQuestions.Questions[0].Question.Id);
        Assert.Equal("Bad question", invalidQuestions.Questions[0].Reason);
        Assert.Equal("test-subject", invalidQuestions.Questions[0].SubjectId);
    }

    [Fact]
    public async Task MarkQuestionInvalidAsync_RemovesQuestionFromSubject()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject", questionCount: 3);
        await _service.SaveSubjectAsync("test-subject", subject);

        await _service.MarkQuestionInvalidAsync("test-subject", "test-subject-001", "Bad question");

        var updatedSubject = await _service.GetSubjectAsync("test-subject");
        Assert.NotNull(updatedSubject);
        Assert.Equal(2, updatedSubject.QuestionSets[0].Questions.Count);
        Assert.DoesNotContain(updatedSubject.QuestionSets[0].Questions, q => q.Id == "test-subject-001");
    }

    [Fact]
    public async Task MarkQuestionInvalidAsync_UpdatesIndexQuestionCount()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject", questionCount: 5);
        await _service.SaveSubjectAsync("test-subject", subject);

        await _service.MarkQuestionInvalidAsync("test-subject", "test-subject-001", null);

        var index = await _service.GetIndexAsync();
        Assert.Equal(4, index.Subjects[0].QuestionCount);
    }

    [Fact]
    public async Task MarkQuestionInvalidAsync_WithNullReason_StillWorks()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject");
        await _service.SaveSubjectAsync("test-subject", subject);

        var result = await _service.MarkQuestionInvalidAsync("test-subject", "test-subject-001", null);

        Assert.True(result);
        var invalidQuestions = await _service.GetInvalidQuestionsAsync();
        Assert.Single(invalidQuestions.Questions);
        Assert.Null(invalidQuestions.Questions[0].Reason);
    }

    [Fact]
    public async Task MarkQuestionInvalidAsync_MultipleQuestions_AccumulatesInFile()
    {
        var subject = CreateTestSubject("test-subject", "Test Subject", questionCount: 5);
        await _service.SaveSubjectAsync("test-subject", subject);

        await _service.MarkQuestionInvalidAsync("test-subject", "test-subject-001", "Reason 1");
        await _service.MarkQuestionInvalidAsync("test-subject", "test-subject-002", "Reason 2");
        await _service.MarkQuestionInvalidAsync("test-subject", "test-subject-003", "Reason 3");

        var invalidQuestions = await _service.GetInvalidQuestionsAsync();
        Assert.Equal(3, invalidQuestions.Questions.Count);

        var updatedSubject = await _service.GetSubjectAsync("test-subject");
        Assert.Equal(2, updatedSubject?.QuestionSets[0].Questions.Count);
    }

    #endregion
}
