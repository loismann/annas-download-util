using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnnasArchive.API.HealthChecks;

/// <summary>
/// Health check for Google Books API connectivity.
/// </summary>
public class GoogleBooksHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;

    public GoogleBooksHealthCheck(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("GoogleBooks");

            // Simple query to verify API is reachable
            var response = await client.GetAsync(
                "books/v1/volumes?q=test&maxResults=1",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("Google Books API is reachable");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return HealthCheckResult.Degraded("Google Books API rate limited");
            }

            return HealthCheckResult.Degraded(
                $"Google Books API returned status {(int)response.StatusCode}");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return HealthCheckResult.Degraded("Google Books API request timed out");
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Degraded(
                $"Google Books API unreachable: {ex.Message}",
                ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Google Books health check failed: {ex.Message}",
                ex);
        }
    }
}
