using AnnasArchive.Core.Models;
using System.Text.Json;

namespace AnnasArchive.Tests.Models;

/// <summary>
/// Tests for BookDto to ensure model structure and JSON serialization remain consistent
/// </summary>
public class BookDtoTests
{
    [Fact]
    public void BookDto_Constructor_ShouldCreateValidInstance()
    {
        // Arrange & Act
        var book = new BookDto(
            Title: "Test Book",
            Md5: "abc123def456789012345678901234ab",
            Authors: new List<string> { "Author 1", "Author 2" },
            Language: "English",
            Format: "EPUB",
            Source: "/lgli",
            FileSize: "2.5MB",
            BookType: "Book (fiction)",
            Publisher: "Test Publisher",
            PublicationYear: 2023,
            BaseScore: 4.5,
            FinalScore: 4.8
        );

        // Assert
        book.Title.Should().Be("Test Book");
        book.Md5.Should().Be("abc123def456789012345678901234ab");
        book.Authors.Should().HaveCount(2);
        book.Authors.Should().Contain("Author 1");
        book.Language.Should().Be("English");
        book.Format.Should().Be("EPUB");
        book.FileSize.Should().Be("2.5MB");
        book.Publisher.Should().Be("Test Publisher");
        book.PublicationYear.Should().Be(2023);
        book.BaseScore.Should().Be(4.5);
        book.FinalScore.Should().Be(4.8);
    }

    [Fact]
    public void BookDto_WithIsbn_ShouldSerializeIsbn()
    {
        // Arrange
        var book = new BookDto(
            Title: "Test Book",
            Md5: "abc123",
            Authors: new List<string> { "Author" },
            Language: "English",
            Format: "EPUB",
            Source: "/lgli",
            FileSize: "1MB",
            BookType: "Book",
            Publisher: "Publisher",
            PublicationYear: 2023,
            BaseScore: null,
            FinalScore: null
        )
        {
            Isbn = "9781234567890"
        };

        // Act
        var json = JsonSerializer.Serialize(book, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Assert
        json.Should().Contain("isbn");
        json.Should().Contain("9781234567890");
    }

    [Fact]
    public void BookDto_WithoutIsbn_ShouldNotSerializeIsbn()
    {
        // Arrange
        var book = new BookDto(
            Title: "Test Book",
            Md5: "abc123",
            Authors: new List<string> { "Author" },
            Language: "English",
            Format: "EPUB",
            Source: "/lgli",
            FileSize: "1MB",
            BookType: "Book",
            Publisher: "Publisher",
            PublicationYear: 2023,
            BaseScore: null,
            FinalScore: null
        );
        // ISBN intentionally not set

        // Act
        var json = JsonSerializer.Serialize(book, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        // Assert
        json.Should().NotContain("isbn");
    }

    [Fact]
    public void BookDto_CoverCandidates_ShouldInitializeAsEmptyList()
    {
        // Arrange & Act
        var book = new BookDto(
            Title: "Test",
            Md5: "abc",
            Authors: new List<string>(),
            Language: "English",
            Format: "PDF",
            Source: "",
            FileSize: "1MB",
            BookType: "Book",
            Publisher: "",
            PublicationYear: null,
            BaseScore: null,
            FinalScore: null
        );

        // Assert
        book.CoverCandidates.Should().NotBeNull();
        book.CoverCandidates.Should().BeEmpty();
    }

    [Fact]
    public void BookDto_CoverCandidates_ShouldAllowAdding()
    {
        // Arrange
        var book = new BookDto(
            Title: "Test",
            Md5: "abc",
            Authors: new List<string>(),
            Language: "English",
            Format: "PDF",
            Source: "",
            FileSize: "1MB",
            BookType: "Book",
            Publisher: "",
            PublicationYear: null,
            BaseScore: null,
            FinalScore: null
        );

        // Act
        book.CoverCandidates.Add("https://covers.example.com/1.jpg");
        book.CoverCandidates.Add("https://covers.example.com/2.jpg");

        // Assert
        book.CoverCandidates.Should().HaveCount(2);
        book.CoverCandidates.Should().Contain("https://covers.example.com/1.jpg");
    }

    [Fact]
    public void BookDto_JsonSerialization_ShouldUseCamelCase()
    {
        // Arrange
        var book = new BookDto(
            Title: "Test Book",
            Md5: "abc123",
            Authors: new List<string> { "Author" },
            Language: "English",
            Format: "EPUB",
            Source: "/lgli",
            FileSize: "1MB",
            BookType: "Book",
            Publisher: "Publisher",
            PublicationYear: 2023,
            BaseScore: 4.5,
            FinalScore: 4.8
        );

        // Act
        var json = JsonSerializer.Serialize(book, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Assert
        json.Should().Contain("\"title\":\"Test Book\"");
        json.Should().Contain("\"md5\":\"abc123\"");
        json.Should().Contain("\"publicationYear\":2023");
        json.Should().Contain("\"baseScore\":4.5");
        json.Should().Contain("\"finalScore\":4.8");

        // Should NOT contain Pascal case
        json.Should().NotContain("Title");
        json.Should().NotContain("Md5");
        json.Should().NotContain("PublicationYear");
    }

    [Fact]
    public void BookDto_WithNullValues_ShouldHandleGracefully()
    {
        // Arrange & Act
        var book = new BookDto(
            Title: "Test",
            Md5: "abc",
            Authors: new List<string>(),
            Language: "",
            Format: "",
            Source: "",
            FileSize: "",
            BookType: "",
            Publisher: "",
            PublicationYear: null,
            BaseScore: null,
            FinalScore: null
        );

        // Assert
        book.PublicationYear.Should().BeNull();
        book.BaseScore.Should().BeNull();
        book.FinalScore.Should().BeNull();
        book.Language.Should().BeEmpty();
    }

    [Fact]
    public void BookDto_Equality_ShouldWorkByValue()
    {
        // Arrange
        var book1 = new BookDto(
            Title: "Test",
            Md5: "abc",
            Authors: new List<string> { "Author" },
            Language: "English",
            Format: "EPUB",
            Source: "/lgli",
            FileSize: "1MB",
            BookType: "Book",
            Publisher: "Pub",
            PublicationYear: 2023,
            BaseScore: null,
            FinalScore: null
        );

        var book2 = new BookDto(
            Title: "Test",
            Md5: "abc",
            Authors: new List<string> { "Author" },
            Language: "English",
            Format: "EPUB",
            Source: "/lgli",
            FileSize: "1MB",
            BookType: "Book",
            Publisher: "Pub",
            PublicationYear: 2023,
            BaseScore: null,
            FinalScore: null
        );

        // Act & Assert - Use BeEquivalentTo for deep equality (includes collections)
        book1.Should().BeEquivalentTo(book2);
    }
}
