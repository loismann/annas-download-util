namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for tracking OpenAI token usage and managing monthly allowances
/// </summary>
public interface ITokenUsageService
{
    /// <summary>
    /// Adds token usage to the running total
    /// </summary>
    void AddUsage(int promptTokens, int completionTokens);

    /// <summary>
    /// Gets the current token usage totals (prompt, completion, total)
    /// </summary>
    (long PromptTokens, long CompletionTokens, long TotalTokens) GetTotals();

    /// <summary>
    /// Calculates the cost in USD based on GPT-5.2 pricing
    /// </summary>
    double CalculateCostUsd(long promptTokens, long completionTokens);

    /// <summary>
    /// Checks if usage has exceeded the specified allowance
    /// </summary>
    bool IsOverLimit(long allowance);

    /// <summary>
    /// Manually resets the usage counter
    /// </summary>
    void Reset();
}
