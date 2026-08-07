using System.Security.Claims;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for user-related operations.
/// </summary>
public static class UserHelpers
{
    /// <summary>
    /// Gets the user ID from the HTTP context claims.
    /// </summary>
    public static string? GetUserIdFromContext(HttpContext context)
    {
        return context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    /// <summary>
    /// Maps owner id to display name, for the panels that report per-person
    /// figures (AI spend). Keyed by <see cref="HouseholdIdentity.ResolveId"/>
    /// because that is what <see cref="GetUserIdFromContext"/> returns and what
    /// the usage store files are named after — keying it by the access code, as
    /// it once was, is what let the two drift apart.
    /// </summary>
    public static Dictionary<string, string> GetUserDisplayNames(IConfiguration cfg)
    {
        return HouseholdIdentity.Members(cfg)
            .ToDictionary(HouseholdIdentity.ResolveId, member => member.Name);
    }

    /// <summary>
    /// Maps full account name to the initial shown in the presence indicator,
    /// for everyone who should appear there.
    /// </summary>
    /// <remarks>
    /// Prefers explicitly configured <see cref="AccessCode.Initial"/> values.
    /// Falls back to the original hard-coded "(Mom)" → M / "(Dad)" → D sniffing
    /// only when nothing is configured, so this is a drop-in change: behaviour
    /// is identical until initials are added to appsettings, after which adding
    /// or renaming a household member needs no code change at all.
    /// </remarks>
    public static Dictionary<string, string> GetPresenceInitials(IConfiguration cfg)
    {
        var codes = cfg.GetSection("Auth:AccessCodes").Get<List<AccessCode>>() ?? new List<AccessCode>();
        var initials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var configured = codes.Where(c => !string.IsNullOrWhiteSpace(c.Initial)).ToList();
        if (configured.Count > 0)
        {
            foreach (var code in configured)
                initials[code.Name] = code.Initial!.Trim()[..1].ToUpperInvariant();

            return initials;
        }

        foreach (var code in codes)
        {
            if (code.Name.Contains("(Mom)", StringComparison.OrdinalIgnoreCase))
                initials[code.Name] = "M";
            else if (code.Name.Contains("(Dad)", StringComparison.OrdinalIgnoreCase))
                initials[code.Name] = "D";
        }

        return initials;
    }
}
