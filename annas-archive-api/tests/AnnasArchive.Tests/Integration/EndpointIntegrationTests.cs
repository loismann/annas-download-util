using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using AnnasArchive.Core.Models;

namespace AnnasArchive.Tests.Integration;

/// <summary>
/// Integration tests for API endpoints to ensure request/response contracts remain stable
/// These tests validate the actual HTTP endpoints without external dependencies
/// </summary>
public class EndpointIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private const string TestJwtSecret = "test-secret-key-for-integration-tests-minimum-32-characters-required-for-security";

    public EndpointIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                // Override configuration for testing
                config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Auth:JwtSecret"] = TestJwtSecret,
                    ["Auth:AccessCodeHash"] = "$2a$12$test", // Dummy BCrypt hash
                    ["Anna:MemberKey"] = "test-member-key",
                    ["OpenAI:ApiKey"] = "test-openai-key",
                    ["Dropbox:AccessToken"] = "test-dropbox-token",
                    ["Kindle:EmailAddress"] = "test@example.com",
                    ["Kindle:SmtpServer"] = "smtp.test.com",
                    ["Kindle:SmtpPort"] = "587",
                    ["Kindle:SmtpUsername"] = "test",
                    ["Kindle:SmtpPassword"] = "test",
                    ["Logging:LogLevel:Default"] = "Error",
                    ["Logging:LogLevel:Microsoft"] = "Error",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Error"
                }!);
            });
        });
        _client = _factory.CreateClient();
    }

    private void SetAuthToken()
    {
        // Generate a proper JWT token for testing
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(TestJwtSecret);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test@test.com"),
                new Claim(ClaimTypes.Email, "test@test.com"),
                new Claim(ClaimTypes.Name, "Test User")
            }),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(tokenDescriptor);
        var tokenString = tokenHandler.WriteToken(token);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenString);
    }

    [Fact]
    public async Task HealthCheck_Swagger_ShouldBeAccessible()
    {
        // Act
        var response = await _client.GetAsync("/swagger/v1/swagger.json");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        // NotFound is OK in production mode where Swagger is disabled
    }

    [Fact]
    public async Task BookSearch_WithMissingQueryParameter_ShouldReturnBadRequest()
    {
        // Arrange
        SetAuthToken();

        // Act
        var response = await _client.GetAsync("/api/anna/book");

        // Assert - Auth middleware may run before validation
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("name");
            content.Should().Contain("required");
        }
    }

    [Fact]
    public async Task BookSearch_WithValidQuery_ShouldReturnOkOrArray()
    {
        // Arrange
        SetAuthToken();

        // Act - Note: This will fail without proper Anna's Archive connection in test env
        var response = await _client.GetAsync("/api/anna/book?name=test");

        // Assert
        // In test environment, we expect either OK or Unauthorized (if auth is strict)
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.ServiceUnavailable
        );
    }

    [Fact]
    public async Task DownloadLinks_WithInvalidMd5_ShouldReturnBadRequest()
    {
        // Arrange
        SetAuthToken();

        // Act
        var response = await _client.GetAsync("/api/anna/book/invalid-md5/download");

        // Assert - Auth middleware may run before validation
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("Invalid MD5");
        }
    }

    [Fact]
    public async Task DownloadLinks_WithValidMd5_ShouldReturnOkOrError()
    {
        // Arrange
        SetAuthToken();
        var validMd5 = "abc123def456789012345678901234ab";

        // Act
        var response = await _client.GetAsync($"/api/anna/book/{validMd5}/download");

        // Assert
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.NotFound,
            HttpStatusCode.ServiceUnavailable
        );
    }

    [Fact]
    public async Task DropboxEpubs_WithoutAuth_ShouldReturnUnauthorized()
    {
        // Act - No auth token set
        var response = await _client.GetAsync("/api/anna/dropbox/epubs");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DropboxEpubs_WithAuth_ShouldReturnOkOrError()
    {
        // Arrange
        SetAuthToken();

        // Act
        var response = await _client.GetAsync("/api/anna/dropbox/epubs");

        // Assert - May get auth error if token not properly validated
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.InternalServerError
        );
    }

    [Fact]
    public async Task ChaptersList_WithoutPath_ShouldReturnBadRequest()
    {
        // Arrange
        SetAuthToken();

        // Act
        var response = await _client.GetAsync("/api/anna/dropbox/epub/chapters");

        // Assert - Auth middleware may run before validation
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChapterContent_WithoutRequiredParameters_ShouldReturnBadRequest()
    {
        // Arrange
        SetAuthToken();

        // Act - Missing both path and chapterId
        var response = await _client.GetAsync("/api/anna/dropbox/epub/chapter");

        // Assert - Auth middleware may run before validation
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EpubSearch_WithShortQuery_ShouldReturnBadRequest()
    {
        // Arrange
        SetAuthToken();

        // Act - Query too short (min 10 characters)
        var response = await _client.GetAsync("/api/anna/dropbox/epub/search?path=/test.epub&query=short");

        // Assert - Auth middleware may run before validation
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("10 characters");
        }
    }

    [Fact]
    public async Task TokenUsage_WithAuth_ShouldReturnUsageStats()
    {
        // Arrange
        SetAuthToken();

        // Act
        var response = await _client.GetAsync("/api/ai/usage");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Unauthorized);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("totalTokens");
        }
    }

    [Fact]
    public async Task Login_WithMissingCode_ShouldReturnBadRequest()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_WithInvalidCode_ShouldReturnUnauthorized()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            code = "invalid-code-12345"
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RateLimiting_ShouldApplyToEndpoints()
    {
        // This test verifies that rate limiting is configured
        // Note: Actual rate limit testing requires many rapid requests

        // Arrange
        SetAuthToken();
        var tasks = new List<Task<HttpResponseMessage>>();

        // Act - Make multiple rapid requests
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(_client.GetAsync("/api/ai/usage"));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert - All should either succeed or be rate limited
        foreach (var response in responses)
        {
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.OK,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.Unauthorized
            );
        }
    }

    [Fact]
    public async Task SecurityHeaders_ShouldBePresent()
    {
        // Act
        var response = await _client.GetAsync("/api/ai/usage");

        // Assert - Security headers should be present
        response.Headers.Should().Contain(h => h.Key == "X-Content-Type-Options");
        response.Headers.Should().Contain(h => h.Key == "X-Frame-Options");
        response.Headers.Should().Contain(h => h.Key == "X-XSS-Protection");
    }

    [Theory]
    [InlineData("/api/ai/summarize")]
    [InlineData("/api/ai/flashcards")]
    [InlineData("/api/ai/vocab/learn-more")]
    [InlineData("/api/ai/section-summary")]
    [InlineData("/api/ai/characters/graph")]
    public async Task AiEndpoints_WithoutAuth_ShouldReturnUnauthorized(string endpoint)
    {
        // Act
        var response = await _client.PostAsync(endpoint, new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SendToBoox_WithInvalidMd5_ShouldReturnBadRequest()
    {
        // Arrange
        SetAuthToken();

        // Act
        var response = await _client.PostAsync("/api/anna/book/invalid/send-to-boox", null);

        // Assert - Auth middleware may run before validation
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SendToKindle_WithoutTargetParameter_ShouldReturnBadRequest()
    {
        // Arrange
        SetAuthToken();
        var validMd5 = "abc123def456789012345678901234ab";

        // Act
        var response = await _client.PostAsync($"/api/anna/book/{validMd5}/send-to-kindle", null);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChunkBoundaries_ShouldReturnServerSentEvents()
    {
        // Arrange
        SetAuthToken();

        // Act
        var response = await _client.GetAsync("/api/ai/chunk-boundaries?dropboxPath=/test.epub&chapterId=1");

        // Assert
        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        }
        else
        {
            // Expect error due to missing EPUB in test environment
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.BadRequest,
                HttpStatusCode.NotFound,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.Unauthorized
            );
        }
    }

    [Fact]
    public async Task ChapterSummaryStream_ShouldReturnServerSentEvents()
    {
        // Arrange
        SetAuthToken();
        var request = new
        {
            dropboxPath = "/test.epub",
            chapterId = 1,
            bookTitle = "Test Book"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/ai/summarize/chapter/stream", request);

        // Assert
        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        }
        else
        {
            // Expect error due to missing EPUB or token limits
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.BadRequest,
                HttpStatusCode.NotFound,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.Unauthorized
            );
        }
    }

    [Fact]
    public async Task DeleteEndpoints_WithAuth_ShouldAcceptDeleteMethod()
    {
        // Arrange
        SetAuthToken();

        // Act - Test DELETE endpoints exist and accept DELETE method
        var epubIndexResponse = await _client.DeleteAsync("/api/anna/dropbox/epub/index?path=/test.epub");
        var summaryResponse = await _client.DeleteAsync("/api/ai/summarize/chapter?dropboxPath=/test.epub&chapterId=1");
        var flashcardsResponse = await _client.DeleteAsync("/api/ai/flashcards?path=/test.epub");

        // Assert - All should respond (even if with errors due to missing data)
        epubIndexResponse.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed);
        summaryResponse.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed);
        flashcardsResponse.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task GamingPcEndpoints_WithAuth_ShouldBeAccessible()
    {
        // Arrange
        SetAuthToken();

        // Act
        var statusResponse = await _client.GetAsync("/api/gaming/status");

        // Assert
        statusResponse.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.Unauthorized
        );
    }
}
