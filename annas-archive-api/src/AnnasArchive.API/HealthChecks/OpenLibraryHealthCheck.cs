using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnnasArchive.API.HealthChecks;

/// <summary>
/// Health check for Open Library API connectivity.
/// </summary>
public class OpenLibraryHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OpenLibraryHealthCheck(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("OpenLibrary");

            // Simple search to verify API is reachable
            var response = await client.GetAsync(
                "search.json?q=test&limit=1",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("Open Library API is reachable");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return HealthCheckResult.Degraded("Open Library API rate limited");
            }

            return HealthCheckResult.Degraded(
                $"Open Library API returned status {(int)response.StatusCode}");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return HealthCheckResult.Degraded("Open Library API request timed out");
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Degraded(
                $"Open Library API unreachable: {ex.Message}",
                ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Open Library health check failed: {ex.Message}",
                ex);
        }
    }
}
