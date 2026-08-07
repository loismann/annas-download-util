using AnnasArchive.API.Constants;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnnasArchive.API.HealthChecks;

/// <summary>
/// Reports a configured household member who resolves to no owner, or two who
/// resolve to the same one.
///
/// The startup log already says this once, which is where the last occurrence
/// went unnoticed for an unknown length of time — a line at boot is only visible
/// to whoever is watching at boot. This makes the same answer available at
/// <c>/health</c> at any moment, which is the difference between "we found out
/// when someone noticed their library was empty" and "we can ask".
///
/// Degraded, never Unhealthy: everything works, it is only being filed under
/// nobody. Restarting the container — what Unhealthy invites an orchestrator to
/// do — fixes none of it, since the cause is in config.
/// </summary>
public class HouseholdOwnersHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var problems = HouseholdOwners.Validate(configuration);

        if (problems.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Every configured household member resolves to an owner"));
        }

        var data = problems
            .Select((problem, index) => (Key: $"problem{index + 1}", Value: (object)problem))
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        return Task.FromResult(HealthCheckResult.Degraded(
            $"{problems.Count} household owner problem(s); items may be added unowned",
            data: data));
    }
}
