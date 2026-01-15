using AnnasArchive.API.Extensions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AnnasArchive.Tests.Extensions;

public class ConfigurationExtensionsTests
{
    private IConfiguration CreateConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    #region GetRequiredValue Tests

    [Fact]
    public void GetRequiredValue_ReturnsValue_WhenKeyExists()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            { "TestKey", "TestValue" }
        });

        var result = config.GetRequiredValue<string>("TestKey");

        Assert.Equal("TestValue", result);
    }

    [Fact]
    public void GetRequiredValue_ReturnsIntValue_WhenKeyExists()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            { "Port", "8080" }
        });

        var result = config.GetRequiredValue<int>("Port");

        Assert.Equal(8080, result);
    }

    [Fact]
    public void GetRequiredValue_ThrowsInvalidOperationException_WhenKeyMissing()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            config.GetRequiredValue<string>("MissingKey"));

        Assert.Contains("MissingKey", exception.Message);
        Assert.Contains("required", exception.Message);
    }

    #endregion

    #region GetValueOrEmpty Tests

    [Fact]
    public void GetValueOrEmpty_ReturnsValue_WhenKeyExists()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            { "TestKey", "TestValue" }
        });

        var result = config.GetValueOrEmpty("TestKey");

        Assert.Equal("TestValue", result);
    }

    [Fact]
    public void GetValueOrEmpty_ReturnsEmptyString_WhenKeyMissing()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var result = config.GetValueOrEmpty("MissingKey");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetValueOrEmpty_ReturnsEmptyString_WhenValueIsNull()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            { "TestKey", null }
        });

        var result = config.GetValueOrEmpty("TestKey");

        Assert.Equal(string.Empty, result);
    }

    #endregion

    #region GetValueOrDefault Tests

    [Fact]
    public void GetValueOrDefault_ReturnsValue_WhenKeyExists()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            { "TestKey", "TestValue" }
        });

        var result = config.GetValueOrDefault("TestKey", "DefaultValue");

        Assert.Equal("TestValue", result);
    }

    [Fact]
    public void GetValueOrDefault_ReturnsDefault_WhenKeyMissing()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var result = config.GetValueOrDefault("MissingKey", "DefaultValue");

        Assert.Equal("DefaultValue", result);
    }

    [Fact]
    public void GetValueOrDefault_ReturnsDefault_WhenValueIsNull()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            { "TestKey", null }
        });

        var result = config.GetValueOrDefault("TestKey", "DefaultValue");

        Assert.Equal("DefaultValue", result);
    }

    #endregion

    #region TryGetValue Tests

    [Fact]
    public void TryGetValue_ReturnsTrueAndValue_WhenKeyExists()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            { "TestKey", "TestValue" }
        });

        var result = config.TryGetValue<string>("TestKey", out var value);

        Assert.True(result);
        Assert.Equal("TestValue", value);
    }

    [Fact]
    public void TryGetValue_ReturnsFalse_WhenKeyMissing()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var result = config.TryGetValue<string>("MissingKey", out var value);

        Assert.False(result);
        Assert.Null(value);
    }

    [Fact]
    public void TryGetValue_ReturnsTrueAndIntValue_WhenKeyExists()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            { "Port", "3000" }
        });

        var result = config.TryGetValue<int>("Port", out var value);

        Assert.True(result);
        Assert.Equal(3000, value);
    }

    #endregion
}
