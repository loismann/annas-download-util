using AnnasArchive.Core.Services;
using Xunit;
using System.Collections.Generic;

namespace AnnasArchive.Tests.Services;

public class OpenAiModelHelperTests
{
    private readonly OpenAiModelHelper _helper;

    public OpenAiModelHelperTests()
    {
        _helper = new OpenAiModelHelper();
    }

    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5.2", true)]
    [InlineData("gpt-5-mini", true)]
    [InlineData("GPT-5", true)] // Case insensitive
    [InlineData("gpt-4", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("o1-mini", false)]
    public void IsGpt5Family_ShouldIdentifyGpt5Models(string model, bool expected)
    {
        // Act
        var result = _helper.IsGpt5Family(model);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("o1-mini", true)]
    [InlineData("o1-preview", true)]
    [InlineData("o3", true)]
    [InlineData("O1-MINI", true)] // Case insensitive
    [InlineData("gpt-5", false)]
    [InlineData("gpt-4", false)]
    [InlineData("o2", false)] // Not o1 family
    public void IsO1Family_ShouldIdentifyO1Models(string model, bool expected)
    {
        // Act
        var result = _helper.IsO1Family(model);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildChatCompletionPayload_Gpt5WithTemperature_ShouldSetReasoningEffortNone()
    {
        // Arrange
        var model = "gpt-5.2";
        var messages = new object[] { new { role = "user", content = "test" } };
        var temperature = 0.7;

        // Act
        var payload = _helper.BuildChatCompletionPayload(model, messages, temperature: temperature);

        // Assert
        var dict = payload as Dictionary<string, object>;
        Assert.NotNull(dict);
        Assert.Equal(model, dict["model"]);
        Assert.Equal(messages, dict["messages"]);
        Assert.Equal("none", dict["reasoning_effort"]);
        Assert.Equal(temperature, dict["temperature"]);
    }

    [Fact]
    public void BuildChatCompletionPayload_Gpt5WithReasoningEffort_ShouldSetReasoningEffort()
    {
        // Arrange
        var model = "gpt-5";
        var messages = new object[] { new { role = "user", content = "test" } };
        var reasoningEffort = "high";

        // Act
        var payload = _helper.BuildChatCompletionPayload(model, messages, reasoningEffort: reasoningEffort);

        // Assert
        var dict = payload as Dictionary<string, object>;
        Assert.NotNull(dict);
        Assert.Equal(reasoningEffort, dict["reasoning_effort"]);
        Assert.False(dict.ContainsKey("temperature"));
    }

    [Fact]
    public void BuildChatCompletionPayload_Gpt5WithNoParams_ShouldDefaultToReasoningEffortNone()
    {
        // Arrange
        var model = "gpt-5.2";
        var messages = new object[] { new { role = "user", content = "test" } };

        // Act
        var payload = _helper.BuildChatCompletionPayload(model, messages);

        // Assert
        var dict = payload as Dictionary<string, object>;
        Assert.NotNull(dict);
        Assert.Equal("none", dict["reasoning_effort"]);
        Assert.False(dict.ContainsKey("temperature"));
    }

    [Fact]
    public void BuildChatCompletionPayload_O1Model_ShouldNotSetTemperatureOrReasoning()
    {
        // Arrange
        var model = "o1-mini";
        var messages = new object[] { new { role = "user", content = "test" } };

        // Act
        var payload = _helper.BuildChatCompletionPayload(model, messages, temperature: 0.7);

        // Assert
        var dict = payload as Dictionary<string, object>;
        Assert.NotNull(dict);
        Assert.Equal(model, dict["model"]);
        Assert.Equal(messages, dict["messages"]);
        Assert.False(dict.ContainsKey("temperature"));
        Assert.False(dict.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void BuildChatCompletionPayload_Gpt4WithTemperature_ShouldSetTemperature()
    {
        // Arrange
        var model = "gpt-4o";
        var messages = new object[] { new { role = "user", content = "test" } };
        var temperature = 0.5;

        // Act
        var payload = _helper.BuildChatCompletionPayload(model, messages, temperature: temperature);

        // Assert
        var dict = payload as Dictionary<string, object>;
        Assert.NotNull(dict);
        Assert.Equal(temperature, dict["temperature"]);
        Assert.False(dict.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void BuildChatCompletionPayload_WithMaxCompletionTokens_ShouldIncludeInPayload()
    {
        // Arrange
        var model = "gpt-4o";
        var messages = new object[] { new { role = "user", content = "test" } };
        var maxTokens = 2000;

        // Act
        var payload = _helper.BuildChatCompletionPayload(model, messages, maxCompletionTokens: maxTokens);

        // Assert
        var dict = payload as Dictionary<string, object>;
        Assert.NotNull(dict);
        Assert.Equal(maxTokens, dict["max_completion_tokens"]);
    }

    [Fact]
    public void BuildChatCompletionPayload_WithoutMaxCompletionTokens_ShouldNotIncludeInPayload()
    {
        // Arrange
        var model = "gpt-4o";
        var messages = new object[] { new { role = "user", content = "test" } };

        // Act
        var payload = _helper.BuildChatCompletionPayload(model, messages);

        // Assert
        var dict = payload as Dictionary<string, object>;
        Assert.NotNull(dict);
        Assert.False(dict.ContainsKey("max_completion_tokens"));
    }

    [Theory]
    [InlineData("gpt-5.2", "GPT-5 family (supports reasoning_effort, temperature with effort=none)")]
    [InlineData("o1-mini", "o1 family (built-in reasoning, no temperature support)")]
    [InlineData("gpt-4o", "GPT-4 or earlier (standard temperature support)")]
    [InlineData("gpt-3.5-turbo", "GPT-4 or earlier (standard temperature support)")]
    public void GetModelDescription_ShouldReturnCorrectDescription(string model, string expectedDescription)
    {
        // Act
        var description = _helper.GetModelDescription(model);

        // Assert
        Assert.Equal(expectedDescription, description);
    }
}
