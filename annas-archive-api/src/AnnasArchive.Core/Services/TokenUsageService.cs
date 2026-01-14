using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Tracks OpenAI token usage per user with persistence to disk and automatic monthly resets
/// </summary>
public class TokenUsageService : ITokenUsageService
{
    private readonly string _storageDirectory;
    private readonly object _fileLock = new();

    private class UsageData
    {
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public DateTime LastResetDate { get; set; }
    }

    public TokenUsageService(string? storagePath = null)
    {
        _storageDirectory = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".annas-archive",
            "ai-usage"
        );
        Directory.CreateDirectory(_storageDirectory);
    }

    private string GetUserFilePath(string userId)
    {
        // Sanitize userId for file system
        var sanitized = string.Join("_", userId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_storageDirectory, $"{sanitized}.json");
    }

    public void AddUsage(string userId, int promptTokens, int completionTokens)
    {
        lock (_fileLock)
        {
            var data = LoadUsageUnsafe(userId);
            data.PromptTokens += promptTokens;
            data.CompletionTokens += completionTokens;
            SaveUsageUnsafe(userId, data);
        }
    }

    public (long PromptTokens, long CompletionTokens, long TotalTokens) GetTotals(string userId)
    {
        lock (_fileLock)
        {
            var data = LoadUsageUnsafe(userId);
            CheckAndAutoResetUnsafe(userId, ref data);
            return (data.PromptTokens, data.CompletionTokens, data.PromptTokens + data.CompletionTokens);
        }
    }

    public Dictionary<string, (long PromptTokens, long CompletionTokens, long TotalTokens, DateTime LastResetDate)> GetAllUsersUsage()
    {
        lock (_fileLock)
        {
            var result = new Dictionary<string, (long, long, long, DateTime)>();

            if (!Directory.Exists(_storageDirectory))
                return result;

            var files = Directory.GetFiles(_storageDirectory, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var userId = Path.GetFileNameWithoutExtension(file).Replace("_", "-");
                    var data = LoadUsageUnsafe(userId);
                    CheckAndAutoResetUnsafe(userId, ref data);
                    result[userId] = (data.PromptTokens, data.CompletionTokens, data.PromptTokens + data.CompletionTokens, data.LastResetDate);
                }
                catch (ArgumentException ex)
                {
                    Log.Warning("Invalid argument loading usage for {FilePath}: {ParamName}", file, ex.ParamName);
                }
                catch (Exception ex)
                {
                    Log.Warning("Failed to load usage for {FilePath}: {ErrorMessage}", file, ex.Message);
                }
            }

            return result;
        }
    }

    public double CalculateCostUsd(long promptTokens, long completionTokens)
    {
        // GPT-5.2 pricing (as of Dec 2025):
        // Input: $5 per 1M tokens
        // Output: $15 per 1M tokens
        const double inputCostPer1M = 5.0;
        const double outputCostPer1M = 15.0;

        var inputCost = (promptTokens / 1_000_000.0) * inputCostPer1M;
        var outputCost = (completionTokens / 1_000_000.0) * outputCostPer1M;

        return Math.Round(inputCost + outputCost, 2);
    }

    public bool IsOverLimit(string userId, long allowance)
    {
        lock (_fileLock)
        {
            var data = LoadUsageUnsafe(userId);
            CheckAndAutoResetUnsafe(userId, ref data);
            var total = data.PromptTokens + data.CompletionTokens;
            return total >= allowance;
        }
    }

    public void Reset(string? userId = null)
    {
        lock (_fileLock)
        {
            if (userId == null)
            {
                // Reset all users
                if (Directory.Exists(_storageDirectory))
                {
                    var files = Directory.GetFiles(_storageDirectory, "*.json");
                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (ArgumentException ex)
                        {
                            Log.Warning("Invalid argument deleting {FilePath}: {ParamName}", file, ex.ParamName);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning("Failed to delete {FilePath}: {ErrorMessage}", file, ex.Message);
                        }
                    }
                }
                Log.Information("Reset all user token usage counters");
            }
            else
            {
                // Reset specific user
                var data = new UsageData
                {
                    PromptTokens = 0,
                    CompletionTokens = 0,
                    LastResetDate = DateTime.UtcNow
                };
                SaveUsageUnsafe(userId, data);
                Log.Information("Reset token usage counter for user: {UserId}", userId);
            }
        }
    }

    private UsageData LoadUsageUnsafe(string userId)
    {
        try
        {
            var filePath = GetUserFilePath(userId);

            if (!File.Exists(filePath))
            {
                return new UsageData
                {
                    PromptTokens = 0,
                    CompletionTokens = 0,
                    LastResetDate = DateTime.UtcNow
                };
            }

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<UsageData>(json) ?? new UsageData
            {
                PromptTokens = 0,
                CompletionTokens = 0,
                LastResetDate = DateTime.UtcNow
            };
        }
        catch
        {
            return new UsageData
            {
                PromptTokens = 0,
                CompletionTokens = 0,
                LastResetDate = DateTime.UtcNow
            };
        }
    }

    private void SaveUsageUnsafe(string userId, UsageData data)
    {
        try
        {
            var filePath = GetUserFilePath(userId);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(filePath, json);
        }
        catch (ArgumentException ex)
        {
            Log.Warning("Invalid argument saving token usage for {UserId}: {ParamName}", userId, ex.ParamName);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to save token usage for {UserId}: {ErrorMessage}", userId, ex.Message);
        }
    }

    private void CheckAndAutoResetUnsafe(string userId, ref UsageData data)
    {
        // Check if we've passed into a new month since last reset
        var now = DateTime.UtcNow;
        var lastReset = data.LastResetDate;

        // Reset at the start of each calendar month
        var shouldReset = (now.Year > lastReset.Year) ||
                         (now.Year == lastReset.Year && now.Month > lastReset.Month);

        if (shouldReset)
        {
            Log.Information("Auto-resetting token usage for {UserId} (last reset: {LastReset:yyyy-MM-dd}, now: {Now:yyyy-MM-dd})", userId, lastReset, now);
            data.PromptTokens = 0;
            data.CompletionTokens = 0;
            data.LastResetDate = now;
            SaveUsageUnsafe(userId, data);
        }
    }
}
