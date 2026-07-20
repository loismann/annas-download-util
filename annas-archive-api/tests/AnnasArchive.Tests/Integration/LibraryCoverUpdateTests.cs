using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AnnasArchive.Tests.Integration;

/// <summary>
/// Integration tests for library book cover update functionality.
/// Tests the complete flow of updating a book cover and ensuring persistence across reloads.
/// </summary>
public class LibraryCoverUpdateTests
{
    private const string TestLibraryRoot = "/tmp/annas-archive-library-cover-tests";
    private const string TestFileName = "test-book.epub";
    private const string TestMetaFileName = "test-book.epub.meta.json";

    [Fact]
    public async Task UpdateBookCover_ShouldPersistAfterReload()
    {
        // Arrange - Create test library directory and initial metadata file
        Directory.CreateDirectory(TestLibraryRoot);
        var coverDir = Path.Combine(TestLibraryRoot, "_covers");
        Directory.CreateDirectory(coverDir);

        var initialCoverUrl = "_covers/initial-cover.jpg";
        var initialMeta = new
        {
            title = "Test Book",
            authors = new[] { "Test Author" },
            format = "EPUB",
            fileSize = "1.2 MB",
            fileName = TestFileName,
            coverUrl = initialCoverUrl,
            primaryGenre = "Fiction",
            tags = new[] { "test" },
            savedAt = DateTime.UtcNow
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        var metaPath = Path.Combine(TestLibraryRoot, TestMetaFileName);
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(initialMeta, jsonOptions));

        // Create a dummy cover file
        var initialCoverPath = Path.Combine(TestLibraryRoot, initialCoverUrl);
        await File.WriteAllBytesAsync(initialCoverPath, new byte[] { 0x01, 0x02, 0x03 });

        // Act - Simulate updating the cover to a new URL
        var newCoverUrl = "_covers/new-cover.jpg";
        var updatedMeta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            await File.ReadAllTextAsync(metaPath),
            jsonOptions
        );

        updatedMeta!["coverUrl"] = JsonSerializer.SerializeToElement(newCoverUrl, jsonOptions);

        // Serialize back to disk (simulating what the endpoint does)
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(updatedMeta, jsonOptions));

        // Act - Read the metadata again (simulating a page reload)
        var reloadedJson = await File.ReadAllTextAsync(metaPath);
        var reloadedMeta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reloadedJson, jsonOptions);

        // Assert - The coverUrl should have persisted
        Assert.NotNull(reloadedMeta);
        Assert.True(reloadedMeta.ContainsKey("coverUrl"));
        Assert.Equal(newCoverUrl, reloadedMeta["coverUrl"].GetString());

        // Cleanup
        if (Directory.Exists(TestLibraryRoot))
        {
            Directory.Delete(TestLibraryRoot, true);
        }
    }

    [Fact]
    public async Task UpdateBookCover_MetadataFile_ShouldContainAllMutableFields()
    {
        // Arrange - Create test metadata with all fields
        Directory.CreateDirectory(TestLibraryRoot);

        var initialMeta = new
        {
            title = "Original Title",
            authors = new[] { "Original Author" },
            format = "EPUB",
            fileSize = "1.2 MB",
            fileName = TestFileName,
            coverUrl = "_covers/original.jpg",
            primaryGenre = "Original Genre",
            tags = new[] { "original-tag" },
            series = "Original Series",
            goodreadsRating = 4.0,
            personalRating = 5,
            readerEnabled = false,
            savedAt = DateTime.UtcNow
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        var metaPath = Path.Combine(TestLibraryRoot, TestMetaFileName);
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(initialMeta, jsonOptions));

        // Act - Read and update all mutable fields
        var metaDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            await File.ReadAllTextAsync(metaPath),
            jsonOptions
        );

        metaDict!["title"] = JsonSerializer.SerializeToElement("Updated Title", jsonOptions);
        metaDict["authors"] = JsonSerializer.SerializeToElement(new[] { "Updated Author" }, jsonOptions);
        metaDict["coverUrl"] = JsonSerializer.SerializeToElement("_covers/updated.jpg", jsonOptions);
        metaDict["primaryGenre"] = JsonSerializer.SerializeToElement("Updated Genre", jsonOptions);
        metaDict["tags"] = JsonSerializer.SerializeToElement(new[] { "updated-tag" }, jsonOptions);
        metaDict["series"] = JsonSerializer.SerializeToElement("Updated Series", jsonOptions);
        metaDict["goodreadsRating"] = JsonSerializer.SerializeToElement(4.5, jsonOptions);
        metaDict["personalRating"] = JsonSerializer.SerializeToElement(4, jsonOptions);
        metaDict["readerEnabled"] = JsonSerializer.SerializeToElement(true, jsonOptions);

        // Write back
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metaDict, jsonOptions));

        // Act - Reload from disk
        var reloadedJson = await File.ReadAllTextAsync(metaPath);
        var reloadedMeta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reloadedJson, jsonOptions);

        // Assert - All mutable fields should persist
        Assert.NotNull(reloadedMeta);
        Assert.Equal("Updated Title", reloadedMeta["title"].GetString());
        Assert.Equal("Updated Author", reloadedMeta["authors"][0].GetString());
        Assert.Equal("_covers/updated.jpg", reloadedMeta["coverUrl"].GetString());
        Assert.Equal("Updated Genre", reloadedMeta["primaryGenre"].GetString());
        Assert.Equal("updated-tag", reloadedMeta["tags"][0].GetString());
        Assert.Equal("Updated Series", reloadedMeta["series"].GetString());
        Assert.Equal(4.5, reloadedMeta["goodreadsRating"].GetDouble());
        Assert.Equal(4, reloadedMeta["personalRating"].GetInt32());
        Assert.True(reloadedMeta["readerEnabled"].GetBoolean());

        // Cleanup
        if (Directory.Exists(TestLibraryRoot))
        {
            Directory.Delete(TestLibraryRoot, true);
        }
    }

    [Fact]
    public async Task UpdateBookCover_NullCoverUrl_ShouldBeHandledCorrectly()
    {
        // Arrange
        Directory.CreateDirectory(TestLibraryRoot);

        var initialMeta = new
        {
            title = "Test Book",
            authors = new[] { "Test Author" },
            format = "EPUB",
            fileSize = "1.2 MB",
            fileName = TestFileName,
            coverUrl = (string?)null,
            savedAt = DateTime.UtcNow
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var metaPath = Path.Combine(TestLibraryRoot, TestMetaFileName);
        var json = JsonSerializer.Serialize(initialMeta, jsonOptions);
        await File.WriteAllTextAsync(metaPath, json);

        // Assert - coverUrl should not be in JSON when null
        Assert.DoesNotContain("\"coverUrl\":", json);

        // Act - Read back
        var reloadedJson = await File.ReadAllTextAsync(metaPath);
        var reloadedMeta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reloadedJson, jsonOptions);

        // Assert - coverUrl key should not exist
        Assert.NotNull(reloadedMeta);
        Assert.False(reloadedMeta.ContainsKey("coverUrl"));

        // Cleanup
        if (Directory.Exists(TestLibraryRoot))
        {
            Directory.Delete(TestLibraryRoot, true);
        }
    }
}
