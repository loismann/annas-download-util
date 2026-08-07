using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// The one place that decides "who is this person" for storage purposes.
///
/// The JWT used to carry <see cref="AccessCode.Code"/> — the household's actual
/// access code, or its BCrypt hash — in <see cref="ClaimTypes.NameIdentifier"/>.
/// That value was then the owner key for Spotify connections, plans, drafts and
/// inventory, for audiobook request attribution, for photo print runs, and for
/// the per-person AI spend file. Two consequences:
///
/// <list type="bullet">
/// <item>The secret shipped to the browser in a token body that anything with
/// script access can base64-decode (<c>AuthService.getUserId</c> does exactly
/// that), and it was written into a filename on disk.</item>
/// <item>Rotating a code changed the owner key, silently orphaning everything
/// that person owned — no error, their data simply stopped existing.</item>
/// </list>
///
/// Now the token carries an opaque id instead. Configure
/// <see cref="AccessCode.Id"/> to make it survive a code rotation; with none
/// set it is derived from the code, which is exactly as stable as the old
/// behaviour and no less, but never reveals the code itself.
///
/// Old keys are not forgotten: <see cref="PriorKeys"/> feeds the startup
/// migration that moves stored rows onto the current id, and
/// <see cref="NormalizeIdentity"/> rewrites the claim on tokens issued before
/// the change so a 30-day session keeps working rather than quietly pointing at
/// data that has moved.
/// </summary>
public static class HouseholdIdentity
{
    /// <summary>Marks a derived (unconfigured) id, so a value in the database or
    /// a log line is recognisable as one at a glance.</summary>
    private const string DerivedPrefix = "acct-";

    /// <summary>16 hex chars = 64 bits. This is a collision guard across a
    /// household, not a security boundary — the full digest is a pointless
    /// 64-character filename.</summary>
    private const int DerivedIdLength = 16;

    public static IReadOnlyList<AccessCode> Members(IConfiguration configuration) =>
        configuration.GetSection("Auth:AccessCodes").Get<List<AccessCode>>() ?? [];

    /// <summary>The owner key this person's data is stored under, today.</summary>
    public static string ResolveId(AccessCode member) =>
        string.IsNullOrWhiteSpace(member.Id) ? DeriveId(member.Code) : member.Id.Trim();

    /// <summary>
    /// The fallback id for a member with no configured <see cref="AccessCode.Id"/>.
    /// One-way: knowing this tells you nothing about the code.
    /// </summary>
    public static string DeriveId(string code) =>
        DerivedPrefix + OwnerHash(code)[..DerivedIdLength].ToLowerInvariant();

    /// <summary>
    /// Every owner key this person's data could still be filed under, newest
    /// first, excluding the current one. Two migrations are possible and both
    /// have to be covered, in either order: the original "id is the code", and
    /// the derived id that replaced it before an explicit
    /// <see cref="AccessCode.Id"/> was configured.
    /// </summary>
    public static IEnumerable<string> PriorKeys(AccessCode member)
    {
        var current = ResolveId(member);
        var candidates = new[] { DeriveId(member.Code), member.Code };

        return candidates
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Where(key => !string.Equals(key, current, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// How an owner key becomes a stored value. Every owner-scoped store hashes
    /// before writing, so a database dump never contains the key itself; they
    /// each had their own copy of this line, which meant the startup migration
    /// could not prove it was computing the same thing they were.
    /// </summary>
    public static string OwnerHash(string ownerKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)));

    /// <summary>
    /// Maps whatever a token presented onto the current id. An unrecognised
    /// value is returned unchanged — it belongs to a member who has been removed
    /// from config, and inventing an id for them would hand them someone else's
    /// data rather than the nothing they should get.
    /// </summary>
    public static string ResolveClaimValue(IConfiguration configuration, string claimValue)
    {
        foreach (var member in Members(configuration))
        {
            var current = ResolveId(member);
            if (string.Equals(claimValue, current, StringComparison.Ordinal))
                return current;

            if (PriorKeys(member).Contains(claimValue, StringComparer.Ordinal))
                return current;
        }

        return claimValue;
    }

    /// <summary>
    /// Rewrites a validated principal's <see cref="ClaimTypes.NameIdentifier"/>
    /// to the current id. Called once per request from the JWT bearer's
    /// OnTokenValidated, so the 35 call sites of
    /// <see cref="UserHelpers.GetUserIdFromContext"/> never learn that legacy
    /// tokens exist.
    /// </summary>
    public static void NormalizeIdentity(ClaimsPrincipal? principal, IConfiguration configuration)
    {
        var claim = principal?.FindFirst(ClaimTypes.NameIdentifier);
        if (claim is null || principal is null)
            return;

        var resolved = ResolveClaimValue(configuration, claim.Value);
        if (string.Equals(resolved, claim.Value, StringComparison.Ordinal))
            return;

        // Only the identity that actually carries the claim may be edited;
        // ClaimsPrincipal itself has no remove.
        if (principal.Identities.FirstOrDefault(identity =>
                identity.Claims.Contains(claim)) is not { } owner)
            return;

        owner.RemoveClaim(claim);
        owner.AddClaim(new Claim(ClaimTypes.NameIdentifier, resolved));
    }
}
