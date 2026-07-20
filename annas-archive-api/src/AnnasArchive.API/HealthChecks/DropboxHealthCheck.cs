using Dropbox.Api;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnnasArchive.API.HealthChecks;

/// <summary>
/// Health check for Dropbox connectivity.
/// Verifies that the Dropbox client can authenticate and access the account.
/// </summary>
public class DropboxHealthCheck : IHealthCheck
{
    private readonly DropboxClient _dropboxClient;

    public DropboxHealthCheck(DropboxClient dropboxClient)
    {
        _dropboxClient = dropboxClient;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Try to get account info - lightweight call to verify connectivity
            var account = await _dropboxClient.Users.GetCurrentAccountAsync();

            return HealthCheckResult.Healthy(
                $"Dropbox connected as {account.Email}",
                new Dictionary<string, object>
                {
                    ["account"] = account.Email,
                    ["accountId"] = account.AccountId
                });
        }
        catch (ApiException<Dropbox.Api.Auth.TokenFromOAuth1Error> ex)
        {
            return HealthCheckResult.Unhealthy(
                "Dropbox authentication failed - token may be expired",
                ex);
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Degraded(
                "Dropbox API unreachable - network issue",
                ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Dropbox health check failed: {ex.Message}",
                ex);
        }
    }
}
