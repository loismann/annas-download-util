using AnnasArchive.API.Constants;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Constants;

/// <summary>
/// <see cref="HouseholdOwners.ResolveName"/> fails by returning null, and every
/// caller treats null as "no user was involved" rather than "this user is
/// broken" — so a member whose display name stops resolving silently unowns
/// everything they add. These pin the startup check that catches it before the
/// first item goes missing, which a corrupted name got past until 2026-08-06.
/// </summary>
public class HouseholdOwnersValidationTests
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

    private static IConfiguration WholeHousehold() =>
        Config("Paul (Admin)", "Boo! (Mom)", "The Biggest Dad (Dad)");

    [Fact]
    public void AProperlyConfiguredHouseholdHasNoProblems()
    {
        HouseholdOwners.Validate(WholeHousehold()).Should().BeEmpty();
    }

    [Fact]
    public void CatchesTheNameThatResolvesToNobody()
    {
        // The observed failure: the display name was overwritten with a pasted
        // markdown image tag, so it contained none of Paul/Mom/Dad and everything
        // that member added went unowned, one Log.Warning at a time.
        var config = Config(
            "Paul (Admin)",
            "Boo! (![1785685343439](image/appsettings/1785685343439.png))",
            "The Biggest Dad (Dad)");

        var problems = HouseholdOwners.Validate(config);

        // Two problems, not one: a member who resolves to nobody necessarily
        // leaves their owner name unclaimed, and both halves are worth saying —
        // one names the broken config, the other names what stopped working.
        problems.Should().HaveCount(2);
        problems.Should().Contain(problem =>
            problem.Contains("resolves to no household member") && problem.Contains("Boo!"));
        problems.Should().Contain(problem => problem.Contains("Mom is in the household roster"));
    }

    [Fact]
    public void CatchesTwoMembersResolvingToTheSameOwner()
    {
        // Resolution is a substring match, first match wins in roster order, so
        // "Paula (Mom)" contains "paul" and files her books under Paul. The
        // collision is the only visible symptom.
        var config = Config("Paul (Admin)", "Paula (Mom)", "The Biggest Dad (Dad)");

        var problems = HouseholdOwners.Validate(config);

        problems.Should().Contain(problem =>
            problem.Contains("both resolve to Paul"));
    }

    [Fact]
    public void ACollisionAlsoReportsTheOwnerLeftWithNobody()
    {
        var config = Config("Paul (Admin)", "Paula (Mom)", "The Biggest Dad (Dad)");

        HouseholdOwners.Validate(config).Should().Contain(problem =>
            problem.Contains("Mom is in the household roster"));
    }

    [Fact]
    public void CatchesARosterNameNobodyIsConfiguredAs()
    {
        var config = Config("Paul (Admin)", "Boo! (Mom)");

        HouseholdOwners.Validate(config).Should().ContainSingle()
            .Which.Should().Contain("Dad is in the household roster");
    }

    [Fact]
    public void SaysNothingWhenNoMembersAreConfiguredAtAll()
    {
        // Every test host and a fresh checkout look like this. Reporting three
        // missing owners on every unconfigured run would train everyone to ignore
        // the message, which is the failure mode this check exists to avoid.
        HouseholdOwners.Validate(new ConfigurationBuilder().Build()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Paul (Admin)", "Paul")]
    [InlineData("paul", "Paul")]
    [InlineData("  Boo! (Mom)  ", "Mom")]
    [InlineData("The Biggest Dad (Dad)", "Dad")]
    public void ResolveNameStillMapsTheRealConfiguredNames(string raw, string expected)
    {
        HouseholdOwners.ResolveName(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Somebody Else")]
    public void ResolveNameReturnsNullForAnythingItCannotPlace(string? raw)
    {
        HouseholdOwners.ResolveName(raw).Should().BeNull();
    }
}
