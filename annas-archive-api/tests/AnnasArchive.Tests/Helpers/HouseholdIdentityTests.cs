using System.Security.Claims;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// The JWT used to carry the access code itself as the owner key. These pin the
/// two properties that made replacing it safe: the new id never contains the
/// code, and a token issued before the change still resolves to the same person.
/// </summary>
public class HouseholdIdentityTests
{
    private const string Hashed = "$2a$11$abcdefghijklmnopqrstuv";

    private static IConfiguration Config(params AccessCode[] members)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < members.Length; i++)
        {
            values[$"Auth:AccessCodes:{i}:Code"] = members[i].Code;
            values[$"Auth:AccessCodes:{i}:Name"] = members[i].Name;
            values[$"Auth:AccessCodes:{i}:IsAdmin"] = members[i].IsAdmin.ToString();
            if (members[i].Id is { } id)
                values[$"Auth:AccessCodes:{i}:Id"] = id;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void DerivedIdNeverContainsTheCode()
    {
        var member = new AccessCode(Hashed, "Paul (Admin)", true);

        var id = HouseholdIdentity.ResolveId(member);

        id.Should().NotContain(Hashed);
        id.Should().StartWith("acct-");
        id.Should().HaveLength("acct-".Length + 16);
    }

    [Fact]
    public void DerivedIdIsStableForTheSameCode()
    {
        var first = HouseholdIdentity.ResolveId(new AccessCode(Hashed, "Paul", true));
        var second = HouseholdIdentity.ResolveId(new AccessCode(Hashed, "Renamed", false));

        second.Should().Be(first, "the display name is not part of who someone is");
    }

    [Fact]
    public void ConfiguredIdWinsOverTheDerivedOne()
    {
        var member = new AccessCode(Hashed, "Paul", true) { Id = " paul " };

        HouseholdIdentity.ResolveId(member).Should().Be("paul");
    }

    [Fact]
    public void ConfiguredIdSurvivesACodeRotation()
    {
        var before = new AccessCode("$2a$11$old", "Paul", true) { Id = "paul" };
        var after = new AccessCode("$2a$11$rotated", "Paul", true) { Id = "paul" };

        HouseholdIdentity.ResolveId(after).Should().Be(HouseholdIdentity.ResolveId(before));
    }

    [Fact]
    public void DerivedIdDoesNotSurviveACodeRotation()
    {
        // Documented limitation, and the whole reason Id exists: without one, a
        // rotation still moves the owner key. It no longer leaks the code, which
        // is the part this change was for.
        var before = new AccessCode("$2a$11$old", "Paul", true);
        var after = new AccessCode("$2a$11$rotated", "Paul", true);

        HouseholdIdentity.ResolveId(after).Should().NotBe(HouseholdIdentity.ResolveId(before));
    }

    [Fact]
    public void PriorKeysCoverBothTheCodeAndTheDerivedId()
    {
        var member = new AccessCode(Hashed, "Paul", true) { Id = "paul" };

        HouseholdIdentity.PriorKeys(member).Should().BeEquivalentTo(
            [HouseholdIdentity.DeriveId(Hashed), Hashed],
            "a member can be migrated code -> derived -> configured, in either order");
    }

    [Fact]
    public void PriorKeysExcludesTheCurrentId()
    {
        var member = new AccessCode(Hashed, "Paul", true);

        HouseholdIdentity.PriorKeys(member).Should().BeEquivalentTo([Hashed]);
    }

    [Fact]
    public void ResolveClaimValueMapsALegacyCodeClaimOntoTheCurrentId()
    {
        var config = Config(new AccessCode(Hashed, "Paul", true) { Id = "paul" });

        HouseholdIdentity.ResolveClaimValue(config, Hashed).Should().Be("paul");
    }

    [Fact]
    public void ResolveClaimValueMapsADerivedIdClaimOntoAConfiguredId()
    {
        var config = Config(new AccessCode(Hashed, "Paul", true) { Id = "paul" });

        HouseholdIdentity.ResolveClaimValue(config, HouseholdIdentity.DeriveId(Hashed))
            .Should().Be("paul");
    }

    [Fact]
    public void ResolveClaimValueLeavesACurrentIdAlone()
    {
        var config = Config(new AccessCode(Hashed, "Paul", true) { Id = "paul" });

        HouseholdIdentity.ResolveClaimValue(config, "paul").Should().Be("paul");
    }

    [Fact]
    public void ResolveClaimValueLeavesAnUnknownValueAlone()
    {
        // A removed member must resolve to nothing of their own, not to whoever
        // happens to be first in the list.
        var config = Config(new AccessCode(Hashed, "Paul", true) { Id = "paul" });

        HouseholdIdentity.ResolveClaimValue(config, "$2a$11$someone-deleted")
            .Should().Be("$2a$11$someone-deleted");
    }

    [Fact]
    public void NormalizeIdentityRewritesALegacyTokensClaim()
    {
        var config = Config(new AccessCode(Hashed, "Paul", true) { Id = "paul" });
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "Paul (Admin)"),
            new Claim(ClaimTypes.NameIdentifier, Hashed),
            new Claim(ClaimTypes.Role, "Admin")
        ]));

        HouseholdIdentity.NormalizeIdentity(principal, config);

        principal.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("paul");
        principal.FindAll(ClaimTypes.NameIdentifier).Should().ContainSingle(
            "the old claim is replaced, not shadowed");
        principal.FindFirst(ClaimTypes.Role)!.Value.Should().Be("Admin",
            "no other claim is disturbed");
    }

    [Fact]
    public void NormalizeIdentityIsANoOpForACurrentToken()
    {
        var config = Config(new AccessCode(Hashed, "Paul", true) { Id = "paul" });
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "paul")]));

        HouseholdIdentity.NormalizeIdentity(principal, config);

        principal.FindAll(ClaimTypes.NameIdentifier).Should().ContainSingle()
            .Which.Value.Should().Be("paul");
    }

    [Fact]
    public void NormalizeIdentityToleratesAnUnauthenticatedPrincipal()
    {
        var config = Config(new AccessCode(Hashed, "Paul", true));

        var act = () => HouseholdIdentity.NormalizeIdentity(new ClaimsPrincipal(), config);

        act.Should().NotThrow();
    }

    [Fact]
    public void MembersIsEmptyRatherThanNullWhenNothingIsConfigured()
    {
        var config = new ConfigurationBuilder().Build();

        HouseholdIdentity.Members(config).Should().BeEmpty();
    }

    [Fact]
    public void OwnerHashIsTheSameFunctionEveryStoreUses()
    {
        // Pinned because the startup migration computes the old and new hashes
        // itself; if this ever diverged from what the stores write, the migration
        // would silently move nothing.
        HouseholdIdentity.OwnerHash("paul").Should().Be(
            "0357513DEB903A056E74A7E475247FC1FFE31D8BE4C1D4A31F58DD47AE484100");
    }
}
