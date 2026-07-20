using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for checking AI token usage limits.
/// </summary>
public static class TokenLimitHelpers
{
    /// <summary>
    /// Checks if the user has exceeded their monthly AI usage allowance.
    /// Returns an error result if exceeded, null if within limits.
    /// </summary>
    public static IResult? CheckTokenLimit(IConfiguration cfg, ITokenUsageService tokenUsage, HttpContext context)
    {
        var userId = UserHelpers.GetUserIdFromContext(context);
        if (userId == null)
            return Results.Unauthorized();

        var allowanceUsd = cfg.GetValue<double?>("OpenAI:PerUserMonthlyCostAllowanceUsd") ?? 20.0;
        var (promptTokens, completionTokens, _) = tokenUsage.GetTotals(userId);
        var costUsd = tokenUsage.CalculateCostUsd(promptTokens, completionTokens);

        if (costUsd >= allowanceUsd)
        {
            var now = DateTime.UtcNow;
            var nextReset = new DateTime(now.Year, now.Month, 1).AddMonths(1);
            var daysUntilReset = (nextReset - now).Days;

            return Results.Problem(
                detail: $"Monthly AI usage allowance (${allowanceUsd:F2}) has been exceeded. Resets in {daysUntilReset} days.",
                statusCode: 429,
                title: "AI Usage Limit Exceeded"
            );
        }

        return null;
    }

    /// <summary>
    /// Returns true if the user has exceeded their monthly AI usage allowance.
    /// </summary>
    public static bool IsTokenLimitExceeded(IConfiguration cfg, ITokenUsageService tokenUsage, HttpContext context)
    {
        var userId = UserHelpers.GetUserIdFromContext(context);
        if (userId == null)
            return false;

        var allowanceUsd = cfg.GetValue<double?>("OpenAI:PerUserMonthlyCostAllowanceUsd") ?? 20.0;
        var (promptTokens, completionTokens, _) = tokenUsage.GetTotals(userId);
        var costUsd = tokenUsage.CalculateCostUsd(promptTokens, completionTokens);

        return costUsd >= allowanceUsd;
    }
}
