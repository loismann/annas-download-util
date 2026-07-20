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
    /// Gets a dictionary mapping user codes to display names from configuration.
    /// </summary>
    public static Dictionary<string, string> GetUserDisplayNames(IConfiguration cfg)
    {
        var codes = cfg.GetSection("Auth:AccessCodes").Get<List<AccessCode>>();
        return codes?.ToDictionary(c => c.Code, c => c.Name) ?? new Dictionary<string, string>();
    }
}
