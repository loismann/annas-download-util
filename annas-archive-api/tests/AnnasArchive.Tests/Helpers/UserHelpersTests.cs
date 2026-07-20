using System.Security.Claims;
using AnnasArchive.API.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AnnasArchive.Tests.Helpers;

public class UserHelpersTests
{
    #region GetUserIdFromContext Tests

    [Fact]
    public void GetUserIdFromContext_ReturnsNull_WhenUserIsNull()
    {
        var httpContext = new DefaultHttpContext();

        var result = UserHelpers.GetUserIdFromContext(httpContext);

        Assert.Null(result);
    }

    [Fact]
    public void GetUserIdFromContext_ReturnsNull_WhenNameIdentifierClaimIsMissing()
    {
        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.Name, "Test User") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = UserHelpers.GetUserIdFromContext(httpContext);

        Assert.Null(result);
    }

    [Fact]
    public void GetUserIdFromContext_ReturnsUserId_WhenNameIdentifierClaimExists()
    {
        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user123") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = UserHelpers.GetUserIdFromContext(httpContext);

        Assert.Equal("user123", result);
    }

    [Fact]
    public void GetUserIdFromContext_ReturnsUserId_WhenMultipleClaimsExist()
    {
        var httpContext = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "Test User"),
            new Claim(ClaimTypes.NameIdentifier, "user456"),
            new Claim(ClaimTypes.Email, "test@example.com")
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = UserHelpers.GetUserIdFromContext(httpContext);

        Assert.Equal("user456", result);
    }

    #endregion

    #region GetUserDisplayNames Tests

    [Fact]
    public void GetUserDisplayNames_ReturnsEmptyDictionary_WhenAccessCodesNotConfigured()
    {
        var cfg = new Mock<IConfiguration>();
        var configSection = new Mock<IConfigurationSection>();
        cfg.Setup(c => c.GetSection("Auth:AccessCodes")).Returns(configSection.Object);

        var result = UserHelpers.GetUserDisplayNames(cfg.Object);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    #endregion
}
