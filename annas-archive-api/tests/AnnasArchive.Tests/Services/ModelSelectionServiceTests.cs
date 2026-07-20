using AnnasArchive.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class ModelSelectionServiceTests
{
    [Fact]
    public void GetModelDeep_FromConfig_ShouldReturnConfigValue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ModelDeep"] = "gpt-5.2-custom"
            })
            .Build();
        var service = new ModelSelectionService(config);

        // Act
        var result = service.GetModelDeep();

        // Assert
        Assert.Equal("gpt-5.2-custom", result);
    }

    [Fact]
    public void GetModelDeep_FromEnvironment_ShouldReturnEnvValue()
    {
        // Arrange
        Environment.SetEnvironmentVariable("OPENAI_MODEL_DEEP", "gpt-5-env");
        try
        {
            var config = new ConfigurationBuilder().Build();
            var service = new ModelSelectionService(config);

            // Act
            var result = service.GetModelDeep();

            // Assert
            Assert.Equal("gpt-5-env", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_MODEL_DEEP", null);
        }
    }

    [Fact]
    public void GetModelDeep_NoConfigOrEnv_ShouldReturnDefault()
    {
        // Arrange
        var config = new ConfigurationBuilder().Build();
        var service = new ModelSelectionService(config);

        // Act
        var result = service.GetModelDeep();

        // Assert
        Assert.Equal("gpt-5.2", result);
    }

    [Fact]
    public void GetModelDeep_ConfigOverridesEnv_ShouldReturnConfig()
    {
        // Arrange
        Environment.SetEnvironmentVariable("OPENAI_MODEL_DEEP", "gpt-5-env");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ModelDeep"] = "gpt-5-config"
                })
                .Build();
            var service = new ModelSelectionService(config);

            // Act
            var result = service.GetModelDeep();

            // Assert
            Assert.Equal("gpt-5-config", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_MODEL_DEEP", null);
        }
    }

    [Fact]
    public void GetModelFast_FromConfig_ShouldReturnConfigValue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ModelFast"] = "gpt-4o-custom"
            })
            .Build();
        var service = new ModelSelectionService(config);

        // Act
        var result = service.GetModelFast();

        // Assert
        Assert.Equal("gpt-4o-custom", result);
    }

    [Fact]
    public void GetModelFast_FromEnvironment_ShouldReturnEnvValue()
    {
        // Arrange
        Environment.SetEnvironmentVariable("OPENAI_MODEL_FAST", "gpt-4-env");
        try
        {
            var config = new ConfigurationBuilder().Build();
            var service = new ModelSelectionService(config);

            // Act
            var result = service.GetModelFast();

            // Assert
            Assert.Equal("gpt-4-env", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_MODEL_FAST", null);
        }
    }

    [Fact]
    public void GetModelFast_NoConfigOrEnv_ShouldReturnDefault()
    {
        // Arrange
        var config = new ConfigurationBuilder().Build();
        var service = new ModelSelectionService(config);

        // Act
        var result = service.GetModelFast();

        // Assert
        Assert.Equal("gpt-4o", result);
    }

    [Fact]
    public void GetModelFast_ConfigOverridesEnv_ShouldReturnConfig()
    {
        // Arrange
        Environment.SetEnvironmentVariable("OPENAI_MODEL_FAST", "gpt-4-env");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ModelFast"] = "gpt-4-config"
                })
                .Build();
            var service = new ModelSelectionService(config);

            // Act
            var result = service.GetModelFast();

            // Assert
            Assert.Equal("gpt-4-config", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_MODEL_FAST", null);
        }
    }
}
