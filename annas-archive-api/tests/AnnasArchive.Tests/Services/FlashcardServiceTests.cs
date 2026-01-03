using AnnasArchive.Core.Services;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class FlashcardServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FlashcardService _service;
    private readonly TestEpubCachePathProvider _pathProvider;

    public FlashcardServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"flashcard-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempRoot);
        _pathProvider = new TestEpubCachePathProvider(_tempRoot);
        _service = new FlashcardService(_pathProvider);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetFlashcardPath_ShouldCreateCacheDir()
    {
        // Arrange
        var dropboxPath = "/test/book.epub";

        // Act
        var (cacheDir, filePath) = _service.GetFlashcardPath(dropboxPath);

        // Assert
        Assert.True(Directory.Exists(cacheDir));
        Assert.EndsWith("flashcards.json", filePath);
        Assert.StartsWith(cacheDir, filePath);
    }

    [Fact]
    public void GetFlashcardPath_SamePath_ShouldReturnSameDirectory()
    {
        // Arrange
        var dropboxPath = "/test/book.epub";

        // Act
        var result1 = _service.GetFlashcardPath(dropboxPath);
        var result2 = _service.GetFlashcardPath(dropboxPath);

        // Assert
        Assert.Equal(result1.cacheDir, result2.cacheDir);
        Assert.Equal(result1.filePath, result2.filePath);
    }

    [Fact]
    public void GetFlashcardPath_DifferentPaths_ShouldReturnDifferentDirectories()
    {
        // Arrange
        var path1 = "/test/book1.epub";
        var path2 = "/test/book2.epub";

        // Act
        var result1 = _service.GetFlashcardPath(path1);
        var result2 = _service.GetFlashcardPath(path2);

        // Assert
        Assert.NotEqual(result1.cacheDir, result2.cacheDir);
        Assert.NotEqual(result1.filePath, result2.filePath);
    }

    [Fact]
    public void LoadFlashcards_NoFile_ShouldReturnEmptyList()
    {
        // Arrange
        var dropboxPath = "/test/nonexistent.epub";

        // Act
        var result = _service.LoadFlashcards(dropboxPath);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void SaveFlashcards_ShouldCreateFile()
    {
        // Arrange
        var dropboxPath = "/test/book.epub";
        var cards = new List<FlashcardItem>
        {
            new FlashcardItem("test", "definition", "etymology", new List<string> { "usage" }, "notes")
        };

        // Act
        _service.SaveFlashcards(dropboxPath, cards);

        // Assert
        var (_, filePath) = _service.GetFlashcardPath(dropboxPath);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void SaveAndLoad_ShouldPersist()
    {
        // Arrange
        var dropboxPath = "/test/book.epub";
        var cards = new List<FlashcardItem>
        {
            new FlashcardItem("term1", "def1", "etym1", new List<string> { "usage1" }, "notes1"),
            new FlashcardItem("term2", "def2", "etym2", new List<string> { "usage2a", "usage2b" }, null)
        };

        // Act
        _service.SaveFlashcards(dropboxPath, cards);
        var loaded = _service.LoadFlashcards(dropboxPath);

        // Assert
        Assert.Equal(2, loaded.Count);
        Assert.Equal("term1", loaded[0].Term);
        Assert.Equal("def1", loaded[0].Definition);
        Assert.Equal("etym1", loaded[0].Etymology);
        Assert.Single(loaded[0].UsageExamples);
        Assert.Equal("notes1", loaded[0].Notes);

        Assert.Equal("term2", loaded[1].Term);
        Assert.Equal(2, loaded[1].UsageExamples.Count);
        Assert.Null(loaded[1].Notes);
    }

    [Fact]
    public void SaveFlashcards_EmptyList_ShouldSaveEmptyArray()
    {
        // Arrange
        var dropboxPath = "/test/book.epub";
        var cards = new List<FlashcardItem>();

        // Act
        _service.SaveFlashcards(dropboxPath, cards);
        var loaded = _service.LoadFlashcards(dropboxPath);

        // Assert
        Assert.Empty(loaded);
    }

    [Fact]
    public void SaveFlashcards_Overwrite_ShouldReplaceExisting()
    {
        // Arrange
        var dropboxPath = "/test/book.epub";
        var cards1 = new List<FlashcardItem>
        {
            new FlashcardItem("old", "old", "old", new List<string>(), null)
        };
        var cards2 = new List<FlashcardItem>
        {
            new FlashcardItem("new", "new", "new", new List<string>(), null)
        };

        // Act
        _service.SaveFlashcards(dropboxPath, cards1);
        _service.SaveFlashcards(dropboxPath, cards2);
        var loaded = _service.LoadFlashcards(dropboxPath);

        // Assert
        Assert.Single(loaded);
        Assert.Equal("new", loaded[0].Term);
    }

    [Fact]
    public void LoadFlashcards_CorruptedFile_ShouldReturnEmptyList()
    {
        // Arrange
        var dropboxPath = "/test/corrupted.epub";
        var (_, filePath) = _service.GetFlashcardPath(dropboxPath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "{ invalid json >");

        // Act
        var result = _service.LoadFlashcards(dropboxPath);

        // Assert
        Assert.Empty(result);
    }

    // Test helper class
    private class TestEpubCachePathProvider : IEpubCachePathProvider
    {
        private readonly string _root;

        public TestEpubCachePathProvider(string root)
        {
            _root = root;
        }

        public string GetCacheRoot() => _root;

        public string ComputeHash(string value)
        {
            // Simple hash for testing
            return $"hash-{value.GetHashCode():X}";
        }
    }
}
