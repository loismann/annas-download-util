using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.IdentityModel.Tokens;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping authentication-related endpoints.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// Maps all authentication endpoints to the application.
    /// </summary>
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        // Login endpoint
        app.MapPost("/api/auth/login", HandleLogin)
            .RequireRateLimiting("login");

        // User activity endpoint
        app.MapGet("/api/auth/user-activity", HandleGetUserActivity)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleLogin(CodeLoginRequest request, IConfiguration cfg)
    {
        if (string.IsNullOrEmpty(request.Code))
            return Results.BadRequest(new { error = "Access code required" });

        // Get access codes from config
        var codesSection = cfg.GetSection("Auth:AccessCodes");
        var codes = codesSection.Get<List<AccessCode>>();

        if (codes == null || codes.Count == 0)
            return Results.Unauthorized();

        // Find valid code (supports both hashed and plaintext for migration)
        var validCode = codes.FirstOrDefault(c =>
        {
            // If the stored code starts with "$2" it's a BCrypt hash
            if (c.Code.StartsWith("$2"))
            {
                try
                {
                    return BCrypt.Net.BCrypt.Verify(request.Code, c.Code);
                }
                catch
                {
                    return false; // Invalid hash format
                }
            }

            // Fall back to plaintext comparison (DEPRECATED - migrate to BCrypt)
            return c.Code.Equals(request.Code, StringComparison.OrdinalIgnoreCase);
        });

        if (validCode == null)
            return Results.Unauthorized();

        // Generate JWT token
        var jwtSecret = cfg["Auth:JwtSecret"]!;
        var jwtKey = Encoding.UTF8.GetBytes(jwtSecret);
        var tokenExpirationDays = cfg.GetValue<int>("Auth:TokenExpirationDays", 30);

        var tokenHandler = new JwtSecurityTokenHandler();
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, validCode.Name),
            new Claim(ClaimTypes.NameIdentifier, validCode.Code),
            new Claim(ClaimTypes.Role, validCode.IsAdmin ? "Admin" : "User")
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddDays(tokenExpirationDays),
            Issuer = "AnnasArchiveAPI",
            Audience = "AnnasArchiveApp",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(jwtKey),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        var tokenString = tokenHandler.WriteToken(token);

        return Results.Ok(new
        {
            token = tokenString,
            name = validCode.Name,
            isAdmin = validCode.IsAdmin,
            expiresAt = tokenDescriptor.Expires
        });
    }

    private static IResult HandleGetUserActivity(
        HttpContext context,
        IConfiguration cfg,
        IUserActivityService activityService)
    {
        var currentUser = context.User?.FindFirst(ClaimTypes.Name)?.Value ?? "";
        var now = DateTime.UtcNow;

        // Get access codes from config to find actual user names
        var codesSection = cfg.GetSection("Auth:AccessCodes");
        var codes = codesSection.Get<List<AccessCode>>() ?? new List<AccessCode>();

        // Build user mappings dynamically: find users whose names contain "(Mom)" or "(Dad)"
        // Map: actual full name -> display initial
        var userInitials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in codes)
        {
            if (code.Name.Contains("(Mom)", StringComparison.OrdinalIgnoreCase))
            {
                userInitials[code.Name] = "M";
            }
            else if (code.Name.Contains("(Dad)", StringComparison.OrdinalIgnoreCase))
            {
                userInitials[code.Name] = "D";
            }
        }

        var activityList = new List<object>();

        foreach (var (userName, initial) in userInitials)
        {
            // Skip current user
            if (userName.Equals(currentUser, StringComparison.OrdinalIgnoreCase))
                continue;

            double? minutesAgo = null;
            var lastActivity = activityService.GetLastActivity(userName);
            if (lastActivity.HasValue)
            {
                minutesAgo = (now - lastActivity.Value).TotalMinutes;
            }

            // Always return both users with outline, fill based on activity
            activityList.Add(new
            {
                initial,
                userName,
                minutesAgo = minutesAgo.HasValue ? Math.Round(minutesAgo.Value, 1) : (double?)null,
                isFullTone = minutesAgo.HasValue && minutesAgo <= 30,     // Full color: active within 30 min
                isHalfTone = minutesAgo.HasValue && minutesAgo > 30 && minutesAgo <= 60  // Half-toned: 30-60 min
            });
        }

        return Results.Ok(activityList);
    }
}
