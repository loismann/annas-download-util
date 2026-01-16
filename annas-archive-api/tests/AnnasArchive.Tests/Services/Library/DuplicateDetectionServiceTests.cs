using AnnasArchive.API.Services.Library;
using Xunit;

namespace AnnasArchive.Tests.Services.Library;

public class DuplicateDetectionServiceTests : IDisposable
{
    private readonly DuplicateDetectionService _service = new();
    private readonly string _testRoot;
    private readonly List<string> _createdFiles = new();
    private readonly List<string> _createdDirs = new();

    public DuplicateDetectionServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"DuplicateDetectionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _createdDirs.Add(_testRoot);
    }

    public void Dispose()
    {
        foreach (var file in _createdFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
        foreach (var dir in _createdDirs.OrderByDescending(d => d.Length))
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    private string CreateTestFile(string relativePath, string content = "test content")
    {
        var fullPath = Path.Combine(_testRoot, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _createdDirs.Add(dir);
        }
        File.WriteAllText(fullPath, content);
        _createdFiles.Add(fullPath);
        return fullPath;
    }

    private string CreateMetaFile(string bookPath, string title, string[]? authors = null)
    {
        var metaPath = $"{bookPath}.meta.json";
        var authorsJson = authors != null
            ? $"[{string.Join(",", authors.Select(a => $"\"{a}\""))}]"
            : "[]";
        var json = $"{{\"title\":\"{title}\",\"authors\":{authorsJson}}}";
        File.WriteAllText(metaPath, json);
        _createdFiles.Add(metaPath);
        return metaPath;
    }

    #region FindDuplicates Tests

    [Fact]
    public void FindDuplicates_WithEmptyList_ReturnsEmptySet()
    {
        var result = _service.FindDuplicates(_testRoot, new List<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void FindDuplicates_WithSingleFile_ReturnsEmptySet()
    {
        var file = CreateTestFile("book.epub");
        var result = _service.FindDuplicates(_testRoot, new List<string> { file });
        Assert.Empty(result);
    }

    [Fact]
    public void FindDuplicates_WithDifferentTitles_ReturnsEmptySet()
    {
        var file1 = CreateTestFile("Book One.epub");
        var file2 = CreateTestFile("Book Two.epub");

        var result = _service.FindDuplicates(_testRoot, new List<string> { file1, file2 });

        Assert.Empty(result);
    }

    [Fact]
    public void FindDuplicates_SameTitle_SameFormat_KeepsNewest()
    {
        var older = CreateTestFile("The Great Book.epub", "older content");
        Thread.Sleep(50); // Ensure different timestamps
        var newer = CreateTestFile("The Great Book (1).epub", "newer content");

        var result = _service.FindDuplicates(_testRoot, new List<string> { older, newer });

        // Should mark the older one as duplicate
        Assert.Single(result);
        Assert.Contains(older, result);
    }

    [Fact]
    public void FindDuplicates_SameTitle_DifferentFormats_WithAuthor_PreferEpub()
    {
        var epub = CreateTestFile("Author Name - Great Book.epub");
        var pdf = CreateTestFile("Author Name - Great Book.pdf");

        // Create meta files with matching title and author
        CreateMetaFile(epub, "Great Book", new[] { "Author Name" });
        CreateMetaFile(pdf, "Great Book", new[] { "Author Name" });

        var result = _service.FindDuplicates(_testRoot, new List<string> { epub, pdf });

        // PDF should be marked as duplicate since epub has higher priority
        Assert.Single(result);
        Assert.Contains(pdf, result);
    }

    [Fact]
    public void FindDuplicates_CrossFormat_RequiresMatchingAuthor()
    {
        var epub = CreateTestFile("Great Book.epub");
        var pdf = CreateTestFile("Great Book.pdf");

        // Same title but no author info - shouldn't be cross-format duplicates
        // (Pass 2 requires title + author match)

        var result = _service.FindDuplicates(_testRoot, new List<string> { epub, pdf });

        // Without matching authors, cross-format detection shouldn't trigger
        Assert.Empty(result);
    }

    [Fact]
    public void FindDuplicates_NormalizesTitles_IgnoresBrackets()
    {
        var file1 = CreateTestFile("Great Book.epub");
        Thread.Sleep(50);
        var file2 = CreateTestFile("Great Book [Special Edition].epub");

        var result = _service.FindDuplicates(_testRoot, new List<string> { file1, file2 });

        // Should detect as duplicates since brackets are stripped
        Assert.Single(result);
    }

    [Fact]
    public void FindDuplicates_NormalizesTitles_IgnoresParentheses()
    {
        var file1 = CreateTestFile("Great Book.epub");
        Thread.Sleep(50);
        var file2 = CreateTestFile("Great Book (Unabridged).epub");

        var result = _service.FindDuplicates(_testRoot, new List<string> { file1, file2 });

        Assert.Single(result);
    }

    [Fact]
    public void FindDuplicates_NormalizesTitles_CaseInsensitive()
    {
        var file1 = CreateTestFile("the great book.epub");
        Thread.Sleep(50);
        var file2 = CreateTestFile("THE GREAT BOOK.epub");

        var result = _service.FindDuplicates(_testRoot, new List<string> { file1, file2 });

        Assert.Single(result);
    }

    [Fact]
    public void FindDuplicates_UsesMetaJsonTitle_WhenAvailable()
    {
        var file1 = CreateTestFile("random-filename-123.epub");
        var file2 = CreateTestFile("another-random-456.epub");

        // Meta files with same title
        CreateMetaFile(file1, "The Actual Title");
        Thread.Sleep(50);
        CreateMetaFile(file2, "The Actual Title");

        var result = _service.FindDuplicates(_testRoot, new List<string> { file1, file2 });

        Assert.Single(result);
    }

    [Fact]
    public void FindDuplicates_FormatPriority_EpubOverAzw3()
    {
        var epub = CreateTestFile("Author - Book.epub");
        var azw3 = CreateTestFile("Author - Book.azw3");

        CreateMetaFile(epub, "Book", new[] { "Author" });
        CreateMetaFile(azw3, "Book", new[] { "Author" });

        var result = _service.FindDuplicates(_testRoot, new List<string> { epub, azw3 });

        Assert.Contains(azw3, result);
        Assert.DoesNotContain(epub, result);
    }

    [Fact]
    public void FindDuplicates_FormatPriority_EpubOverPdf()
    {
        var epub = CreateTestFile("Author - Book.epub");
        var pdf = CreateTestFile("Author - Book.pdf");

        CreateMetaFile(epub, "Book", new[] { "Author" });
        CreateMetaFile(pdf, "Book", new[] { "Author" });

        var result = _service.FindDuplicates(_testRoot, new List<string> { epub, pdf });

        Assert.Contains(pdf, result);
        Assert.DoesNotContain(epub, result);
    }

    [Fact]
    public void FindDuplicates_MultipleGroups_HandlesEachSeparately()
    {
        var book1_v1 = CreateTestFile("Book One.epub");
        Thread.Sleep(50);
        var book1_v2 = CreateTestFile("Book One (2nd Ed).epub");

        var book2_v1 = CreateTestFile("Book Two.epub");
        Thread.Sleep(50);
        var book2_v2 = CreateTestFile("Book Two (Revised).epub");

        var result = _service.FindDuplicates(_testRoot, new List<string>
        {
            book1_v1, book1_v2, book2_v1, book2_v2
        });

        // Should have 2 duplicates (one from each group)
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region DeleteLibraryArtifacts Tests

    [Fact]
    public void DeleteLibraryArtifacts_DeletesBookFile()
    {
        var file = CreateTestFile("book.epub");
        Assert.True(File.Exists(file));

        _service.DeleteLibraryArtifacts(_testRoot, file);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeleteLibraryArtifacts_DeletesMetaJson()
    {
        var file = CreateTestFile("book.epub");
        var meta = CreateMetaFile(file, "Book Title");
        Assert.True(File.Exists(meta));

        _service.DeleteLibraryArtifacts(_testRoot, file);

        Assert.False(File.Exists(meta));
    }

    [Fact]
    public void DeleteLibraryArtifacts_DeletesCoverFiles()
    {
        var file = CreateTestFile("book.epub");
        var coversDir = Path.Combine(_testRoot, "_covers");
        Directory.CreateDirectory(coversDir);
        _createdDirs.Add(coversDir);

        var coverFile = Path.Combine(coversDir, "book.epub.cover.jpg");
        File.WriteAllText(coverFile, "fake cover");
        _createdFiles.Add(coverFile);

        Assert.True(File.Exists(coverFile));

        _service.DeleteLibraryArtifacts(_testRoot, file);

        Assert.False(File.Exists(coverFile));
    }

    [Fact]
    public void DeleteLibraryArtifacts_HandlesNonExistentFile()
    {
        var nonExistent = Path.Combine(_testRoot, "doesnt-exist.epub");

        // Should not throw
        var exception = Record.Exception(() =>
            _service.DeleteLibraryArtifacts(_testRoot, nonExistent));

        Assert.Null(exception);
    }

    [Fact]
    public void DeleteLibraryArtifacts_HandlesNoCoversDirectory()
    {
        var file = CreateTestFile("book.epub");
        // Don't create _covers directory

        var exception = Record.Exception(() =>
            _service.DeleteLibraryArtifacts(_testRoot, file));

        Assert.Null(exception);
    }

    #endregion
}
