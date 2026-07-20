using System.Security.Claims;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AnnasArchive.Tests.Helpers;

public class TokenLimitHelpersTests
{
    #region CheckTokenLimit Tests

    [Fact]
    public void CheckTokenLimit_ReturnsUnauthorized_WhenUserIdIsNull()
    {
        var cfg = new Mock<IConfiguration>();
        var tokenUsage = new Mock<ITokenUsageService>();
        var httpContext = new DefaultHttpContext();

        var result = TokenLimitHelpers.CheckTokenLimit(cfg.Object, tokenUsage.Object, httpContext);

        Assert.NotNull(result);
    }

    [Fact]
    public void CheckTokenLimit_ReturnsNull_WhenUnderLimit()
    {
        var cfg = new Mock<IConfiguration>();
        var configSection = new Mock<IConfigurationSection>();
        configSection.Setup(s => s.Value).Returns("20.0");
        cfg.Setup(c => c.GetSection("OpenAI:PerUserMonthlyCostAllowanceUsd")).Returns(configSection.Object);

        var tokenUsage = new Mock<ITokenUsageService>();
        tokenUsage.Setup(t => t.GetTotals(It.IsAny<string>())).Returns((1000, 500, 0));
        tokenUsage.Setup(t => t.CalculateCostUsd(1000, 500)).Returns(5.0);

        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user123") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = TokenLimitHelpers.CheckTokenLimit(cfg.Object, tokenUsage.Object, httpContext);

        Assert.Null(result);
    }

    [Fact]
    public void CheckTokenLimit_ReturnsProblem_WhenOverLimit()
    {
        var cfg = new Mock<IConfiguration>();
        var configSection = new Mock<IConfigurationSection>();
        configSection.Setup(s => s.Value).Returns("20.0");
        cfg.Setup(c => c.GetSection("OpenAI:PerUserMonthlyCostAllowanceUsd")).Returns(configSection.Object);

        var tokenUsage = new Mock<ITokenUsageService>();
        tokenUsage.Setup(t => t.GetTotals(It.IsAny<string>())).Returns((100000, 50000, 0));
        tokenUsage.Setup(t => t.CalculateCostUsd(100000, 50000)).Returns(25.0);

        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user123") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = TokenLimitHelpers.CheckTokenLimit(cfg.Object, tokenUsage.Object, httpContext);

        Assert.NotNull(result);
    }

    [Fact]
    public void CheckTokenLimit_ReturnsProblem_WhenExactlyAtLimit()
    {
        var cfg = new Mock<IConfiguration>();
        var configSection = new Mock<IConfigurationSection>();
        configSection.Setup(s => s.Value).Returns("20.0");
        cfg.Setup(c => c.GetSection("OpenAI:PerUserMonthlyCostAllowanceUsd")).Returns(configSection.Object);

        var tokenUsage = new Mock<ITokenUsageService>();
        tokenUsage.Setup(t => t.GetTotals(It.IsAny<string>())).Returns((80000, 40000, 0));
        tokenUsage.Setup(t => t.CalculateCostUsd(80000, 40000)).Returns(20.0);

        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user123") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = TokenLimitHelpers.CheckTokenLimit(cfg.Object, tokenUsage.Object, httpContext);

        Assert.NotNull(result);
    }

    #endregion

    #region IsTokenLimitExceeded Tests

    [Fact]
    public void IsTokenLimitExceeded_ReturnsFalse_WhenUserIdIsNull()
    {
        var cfg = new Mock<IConfiguration>();
        var tokenUsage = new Mock<ITokenUsageService>();
        var httpContext = new DefaultHttpContext();

        var result = TokenLimitHelpers.IsTokenLimitExceeded(cfg.Object, tokenUsage.Object, httpContext);

        Assert.False(result);
    }

    [Fact]
    public void IsTokenLimitExceeded_ReturnsFalse_WhenUnderLimit()
    {
        var cfg = new Mock<IConfiguration>();
        var configSection = new Mock<IConfigurationSection>();
        configSection.Setup(s => s.Value).Returns("20.0");
        cfg.Setup(c => c.GetSection("OpenAI:PerUserMonthlyCostAllowanceUsd")).Returns(configSection.Object);

        var tokenUsage = new Mock<ITokenUsageService>();
        tokenUsage.Setup(t => t.GetTotals(It.IsAny<string>())).Returns((1000, 500, 0));
        tokenUsage.Setup(t => t.CalculateCostUsd(1000, 500)).Returns(5.0);

        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user123") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = TokenLimitHelpers.IsTokenLimitExceeded(cfg.Object, tokenUsage.Object, httpContext);

        Assert.False(result);
    }

    [Fact]
    public void IsTokenLimitExceeded_ReturnsTrue_WhenOverLimit()
    {
        var cfg = new Mock<IConfiguration>();
        var configSection = new Mock<IConfigurationSection>();
        configSection.Setup(s => s.Value).Returns("20.0");
        cfg.Setup(c => c.GetSection("OpenAI:PerUserMonthlyCostAllowanceUsd")).Returns(configSection.Object);

        var tokenUsage = new Mock<ITokenUsageService>();
        tokenUsage.Setup(t => t.GetTotals(It.IsAny<string>())).Returns((100000, 50000, 0));
        tokenUsage.Setup(t => t.CalculateCostUsd(100000, 50000)).Returns(25.0);

        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user123") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = TokenLimitHelpers.IsTokenLimitExceeded(cfg.Object, tokenUsage.Object, httpContext);

        Assert.True(result);
    }

    [Fact]
    public void IsTokenLimitExceeded_ReturnsTrue_WhenExactlyAtLimit()
    {
        var cfg = new Mock<IConfiguration>();
        var configSection = new Mock<IConfigurationSection>();
        configSection.Setup(s => s.Value).Returns("20.0");
        cfg.Setup(c => c.GetSection("OpenAI:PerUserMonthlyCostAllowanceUsd")).Returns(configSection.Object);

        var tokenUsage = new Mock<ITokenUsageService>();
        tokenUsage.Setup(t => t.GetTotals(It.IsAny<string>())).Returns((80000, 40000, 0));
        tokenUsage.Setup(t => t.CalculateCostUsd(80000, 40000)).Returns(20.0);

        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user123") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = TokenLimitHelpers.IsTokenLimitExceeded(cfg.Object, tokenUsage.Object, httpContext);

        Assert.True(result);
    }

    #endregion
}
