using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnnasArchive.API.HealthChecks;

/// <summary>
/// Health check for OpenAI API connectivity.
/// Verifies that the API key is valid and the service is reachable.
/// </summary>
public class OpenAiHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAiHealthCheck(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("OpenAI");

            // Use the models endpoint - lightweight and verifies API key
            var response = await client.GetAsync(
                "https://api.openai.com/v1/models",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("OpenAI API is reachable and authenticated");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return HealthCheckResult.Unhealthy("OpenAI API key is invalid or expired");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return HealthCheckResult.Degraded("OpenAI API rate limited");
            }

            return HealthCheckResult.Degraded(
                $"OpenAI API returned status {(int)response.StatusCode}");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Don't catch cancellation
        }
        catch (TaskCanceledException)
        {
            return HealthCheckResult.Degraded("OpenAI API request timed out");
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Degraded(
                $"OpenAI API unreachable: {ex.Message}",
                ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"OpenAI health check failed: {ex.Message}",
                ex);
        }
    }
}
