using AnnasArchive.API.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnnasArchive.Tests.HealthChecks;

/// <summary>
/// The startup log reports a broken household once, at boot, to whoever happens
/// to be watching. This is the same answer on demand.
/// </summary>
public class HouseholdOwnersHealthCheckTests
{
    private static IConfiguration Config(params string[] memberNames)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < memberNames.Length; i++)
        {
            values[$"Auth:AccessCodes:{i}:Code"] = $"$2a$11$code{i}";
            values[$"Auth:AccessCodes:{i}:Name"] = memberNames[i];
            values[$"Auth:AccessCodes:{i}:IsAdmin"] = (i == 0).ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Task<HealthCheckResult> Check(IConfiguration configuration) =>
        new HouseholdOwnersHealthCheck(configuration)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task HealthyWhenEveryMemberResolves()
    {
        var result = await Check(Config("Paul (Admin)", "Boo! (Mom)", "The Biggest Dad (Dad)"));

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task DegradedNeverUnhealthyWhenAMemberResolvesToNobody()
    {
        // Unhealthy invites an orchestrator to restart the container, which fixes
        // nothing here — the cause is in config and survives the restart.
        var result = await Check(Config("Paul (Admin)", "Corrupted", "The Biggest Dad (Dad)"));

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task ReportsEachProblemInTheResponseData()
    {
        var result = await Check(Config("Paul (Admin)", "Corrupted", "The Biggest Dad (Dad)"));

        result.Data.Should().HaveCount(2, "one unresolvable member, and Mom left with nobody");
        result.Data.Values.Should().Contain(value =>
            value.ToString()!.Contains("Corrupted"));
        result.Description.Should().Contain("2 household owner problem(s)");
    }

    [Fact]
    public async Task HealthyWhenNothingIsConfigured()
    {
        var result = await Check(new ConfigurationBuilder().Build());

        result.Status.Should().Be(HealthStatus.Healthy);
    }
}
