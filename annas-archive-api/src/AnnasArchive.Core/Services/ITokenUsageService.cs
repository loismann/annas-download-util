using System;
using System.Collections.Generic;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for tracking OpenAI token usage per user and managing monthly allowances
/// </summary>
public interface ITokenUsageService
{
    /// <summary>
    /// Adds token usage for a specific user
    /// </summary>
    void AddUsage(string userId, int promptTokens, int completionTokens);

    /// <summary>
    /// Gets the current token usage totals for a specific user
    /// </summary>
    (long PromptTokens, long CompletionTokens, long TotalTokens) GetTotals(string userId);

    /// <summary>
    /// Gets usage data for all users
    /// </summary>
    Dictionary<string, (long PromptTokens, long CompletionTokens, long TotalTokens, DateTime LastResetDate)> GetAllUsersUsage();

    /// <summary>
    /// Calculates the cost in USD based on GPT-5.2 pricing
    /// </summary>
    double CalculateCostUsd(long promptTokens, long completionTokens);

    /// <summary>
    /// Checks if a specific user has exceeded the specified allowance
    /// </summary>
    bool IsOverLimit(string userId, long allowance);

    /// <summary>
    /// Manually resets the usage counter for a specific user (or all users if userId is null)
    /// </summary>
    void Reset(string? userId = null);
}
