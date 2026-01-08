using System.Text.Json;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services;

public interface IQuizStorageService
{
    Task<QuizIndex> GetIndexAsync(CancellationToken token = default);
    Task<QuizSubject?> GetSubjectAsync(string subjectId, CancellationToken token = default);
    Task<QuizSubject> SaveSubjectAsync(string subjectId, QuizSubject subject, CancellationToken token = default);
    Task<bool> DeleteSubjectAsync(string subjectId, CancellationToken token = default);
}

public class QuizStorageService : IQuizStorageService
{
    private readonly string _rootPath;
    private readonly string _subjectsPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IQuizValidationService _validator;

    public QuizStorageService(IConfiguration cfg, IQuizValidationService validator)
    {
        _validator = validator;
        var configuredPath = cfg.GetValue<string>("Quiz:StoragePath");
        _rootPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "quiz-data")
            : (Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
        _subjectsPath = Path.Combine(_rootPath, "subjects");
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_subjectsPath);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<QuizIndex> GetIndexAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var indexPath = GetIndexPath();
            if (!File.Exists(indexPath))
                return await RebuildIndexAsync(token);

            var json = await File.ReadAllTextAsync(indexPath, token);
            var index = JsonSerializer.Deserialize<QuizIndex>(json, _jsonOptions);
            return index ?? await RebuildIndexAsync(token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuizSubject?> GetSubjectAsync(string subjectId, CancellationToken token = default)
    {
        var safeId = ValidateSubjectId(subjectId);
        var path = GetSubjectPath(safeId);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, token);
        return JsonSerializer.Deserialize<QuizSubject>(json, _jsonOptions);
    }

    public async Task<QuizSubject> SaveSubjectAsync(string subjectId, QuizSubject subject, CancellationToken token = default)
    {
        var safeId = ValidateSubjectId(subjectId);
        var validation = _validator.ValidateSubject(subject with { Id = safeId });
        if (!validation.IsValid)
            throw new InvalidOperationException(string.Join(" | ", validation.Errors));

        var subjectWithMeta = subject with { Id = safeId, UpdatedAt = DateTime.UtcNow };
        await _gate.WaitAsync(token);
        try
        {
            var path = GetSubjectPath(safeId);
            var json = JsonSerializer.Serialize(subjectWithMeta, _jsonOptions);
            await File.WriteAllTextAsync(path, json, token);
            await UpdateIndexAsync(subjectWithMeta, token);
            return subjectWithMeta;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteSubjectAsync(string subjectId, CancellationToken token = default)
    {
        var safeId = ValidateSubjectId(subjectId);
        var path = GetSubjectPath(safeId);
        if (!File.Exists(path))
            return false;

        await _gate.WaitAsync(token);
        try
        {
            File.Delete(path);
            var index = await LoadOrCreateIndexAsync(token);
            index.Subjects.RemoveAll(s => string.Equals(s.Id, safeId, StringComparison.OrdinalIgnoreCase));
            index = index with { UpdatedAt = DateTime.UtcNow };
            await WriteIndexAsync(index, token);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdateIndexAsync(QuizSubject subject, CancellationToken token)
    {
        var index = await LoadOrCreateIndexAsync(token);
        var questionCount = subject.QuestionSets.Sum(set => set.Questions.Count);
        var summary = new QuizSubjectSummary
        {
            Id = subject.Id,
            Title = subject.Title,
            Description = subject.Description,
            QuestionCount = questionCount,
            DefaultModeId = subject.DefaultModeId,
            Tags = subject.QuestionSets.SelectMany(set => set.Questions.SelectMany(q => q.Tags)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        var existingIndex = index.Subjects.FindIndex(s => string.Equals(s.Id, subject.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
            index.Subjects[existingIndex] = summary;
        else
            index.Subjects.Add(summary);

        index = index with { UpdatedAt = DateTime.UtcNow };
        await WriteIndexAsync(index, token);
    }

    private async Task<QuizIndex> LoadOrCreateIndexAsync(CancellationToken token)
    {
        var indexPath = GetIndexPath();
        if (!File.Exists(indexPath))
            return new QuizIndex { UpdatedAt = DateTime.UtcNow };

        var json = await File.ReadAllTextAsync(indexPath, token);
        return JsonSerializer.Deserialize<QuizIndex>(json, _jsonOptions) ?? new QuizIndex { UpdatedAt = DateTime.UtcNow };
    }

    private async Task WriteIndexAsync(QuizIndex index, CancellationToken token)
    {
        var indexPath = GetIndexPath();
        var json = JsonSerializer.Serialize(index, _jsonOptions);
        await File.WriteAllTextAsync(indexPath, json, token);
    }

    private async Task<QuizIndex> RebuildIndexAsync(CancellationToken token)
    {
        var index = new QuizIndex { UpdatedAt = DateTime.UtcNow };
        foreach (var file in Directory.GetFiles(_subjectsPath, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, token);
            var subject = JsonSerializer.Deserialize<QuizSubject>(json, _jsonOptions);
            if (subject == null)
                continue;
            var questionCount = subject.QuestionSets.Sum(set => set.Questions.Count);
            index.Subjects.Add(new QuizSubjectSummary
            {
                Id = subject.Id,
                Title = subject.Title,
                Description = subject.Description,
                QuestionCount = questionCount,
                DefaultModeId = subject.DefaultModeId,
                Tags = subject.QuestionSets.SelectMany(set => set.Questions.SelectMany(q => q.Tags)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            });
        }

        await WriteIndexAsync(index, token);
        return index;
    }

    private string GetIndexPath() => Path.Combine(_rootPath, "index.json");

    private string GetSubjectPath(string subjectId) => Path.Combine(_subjectsPath, $"{subjectId}.json");

    private static string ValidateSubjectId(string subjectId)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("Subject id required.", nameof(subjectId));

        var normalized = subjectId.Trim().ToLowerInvariant();
        foreach (var ch in normalized)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
                throw new ArgumentException("Subject id must be alphanumeric with dashes/underscores.", nameof(subjectId));
        }

        return normalized;
    }
}
