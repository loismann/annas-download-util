using AnnasArchive.Core.Services;
using System.Text.Json;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class AiResponseParserTests
{
    private readonly AiResponseParser _parser;

    public AiResponseParserTests()
    {
        _parser = new AiResponseParser();
    }

    [Fact]
    public void ExtractText_ChatCompletionsFormat_ShouldExtractContent()
    {
        // Arrange
        var json = """
        {
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "Hello, world!"
                    }
                }
            ]
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Equal("Hello, world!", result);
    }

    [Fact]
    public void ExtractText_ResponsesApiFormat_ShouldExtractContent()
    {
        // Arrange
        var json = """
        {
            "output": [
                {
                    "type": "reasoning",
                    "content": [
                        {
                            "type": "text",
                            "text": "Internal reasoning..."
                        }
                    ]
                },
                {
                    "type": "message",
                    "content": [
                        {
                            "type": "output_text",
                            "text": "This is the response"
                        }
                    ]
                }
            ]
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Equal("This is the response", result);
    }

    [Fact]
    public void ExtractText_EmptyChoicesArray_ShouldReturnNull()
    {
        // Arrange
        var json = """
        {
            "choices": []
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_EmptyOutputArray_ShouldReturnNull()
    {
        // Arrange
        var json = """
        {
            "output": []
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_MissingMessageProperty_ShouldReturnNull()
    {
        // Arrange
        var json = """
        {
            "choices": [
                {
                    "index": 0
                }
            ]
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_MissingContentProperty_ShouldReturnNull()
    {
        // Arrange
        var json = """
        {
            "choices": [
                {
                    "message": {
                        "role": "assistant"
                    }
                }
            ]
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ContentIsNotString_ShouldReturnNull()
    {
        // Arrange
        var json = """
        {
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": 12345
                    }
                }
            ]
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ResponsesApiNoMessageType_ShouldReturnNull()
    {
        // Arrange
        var json = """
        {
            "output": [
                {
                    "type": "reasoning",
                    "content": [
                        {
                            "type": "text",
                            "text": "Only reasoning, no message"
                        }
                    ]
                }
            ]
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ResponsesApiNoOutputText_ShouldReturnNull()
    {
        // Arrange
        var json = """
        {
            "output": [
                {
                    "type": "message",
                    "content": [
                        {
                            "type": "input_text",
                            "text": "Wrong type"
                        }
                    ]
                }
            ]
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_CompletelyUnknownFormat_ShouldReturnNull()
    {
        // Arrange
        var json = """
        {
            "data": "something",
            "result": "unknown format"
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_EmptyObject_ShouldReturnNull()
    {
        // Arrange
        var json = "{}";
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_MultipleChoices_ShouldExtractFirst()
    {
        // Arrange
        var json = """
        {
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "First response"
                    }
                },
                {
                    "message": {
                        "role": "assistant",
                        "content": "Second response"
                    }
                }
            ]
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Equal("First response", result);
    }

    [Fact]
    public void ExtractText_ResponsesApiMultipleMessages_ShouldExtractFirst()
    {
        // Arrange
        var json = """
        {
            "output": [
                {
                    "type": "message",
                    "content": [
                        {
                            "type": "output_text",
                            "text": "First message"
                        }
                    ]
                },
                {
                    "type": "message",
                    "content": [
                        {
                            "type": "output_text",
                            "text": "Second message"
                        }
                    ]
                }
            ]
        }
        """;
        var root = JsonDocument.Parse(json).RootElement;

        // Act
        var result = _parser.ExtractText(root);

        // Assert
        Assert.Equal("First message", result);
    }
}
