using System;
using System.IO;
using System.Text.Json;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Tracks OpenAI token usage with persistence to disk and automatic monthly resets
/// </summary>
public class TokenUsageService : ITokenUsageService
{
    private readonly string _usageFilePath;
    private readonly object _fileLock = new();

    private class UsageData
    {
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public DateTime LastResetDate { get; set; }
    }

    public TokenUsageService(string? storagePath = null)
    {
        _usageFilePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".annas-archive",
            "openai-usage.json"
        );
    }

    public void AddUsage(int promptTokens, int completionTokens)
    {
        lock (_fileLock)
        {
            var data = LoadUsageUnsafe();
            data.PromptTokens += promptTokens;
            data.CompletionTokens += completionTokens;
            SaveUsageUnsafe(data);
        }
    }

    public (long PromptTokens, long CompletionTokens, long TotalTokens) GetTotals()
    {
        lock (_fileLock)
        {
            var data = LoadUsageUnsafe();
            CheckAndAutoResetUnsafe(ref data);
            return (data.PromptTokens, data.CompletionTokens, data.PromptTokens + data.CompletionTokens);
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

    public bool IsOverLimit(long allowance)
    {
        lock (_fileLock)
        {
            var data = LoadUsageUnsafe();
            CheckAndAutoResetUnsafe(ref data);
            var total = data.PromptTokens + data.CompletionTokens;
            return total >= allowance;
        }
    }

    public void Reset()
    {
        var data = new UsageData
        {
            PromptTokens = 0,
            CompletionTokens = 0,
            LastResetDate = DateTime.UtcNow
        };
        SaveUsage(data);
    }

    private UsageData LoadUsage()
    {
        lock (_fileLock)
        {
            return LoadUsageUnsafe();
        }
    }

    private UsageData LoadUsageUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_usageFilePath)!);

            if (!File.Exists(_usageFilePath))
            {
                return new UsageData
                {
                    PromptTokens = 0,
                    CompletionTokens = 0,
                    LastResetDate = DateTime.UtcNow
                };
            }

            var json = File.ReadAllText(_usageFilePath);
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

    private void SaveUsage(UsageData data)
    {
        lock (_fileLock)
        {
            SaveUsageUnsafe(data);
        }
    }

    private void SaveUsageUnsafe(UsageData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_usageFilePath)!);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_usageFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to save token usage: {ex.Message}");
        }
    }

    private void CheckAndAutoReset(ref UsageData data)
    {
        lock (_fileLock)
        {
            CheckAndAutoResetUnsafe(ref data);
        }
    }

    private void CheckAndAutoResetUnsafe(ref UsageData data)
    {
        // Check if we've passed into a new month since last reset
        var now = DateTime.UtcNow;
        var lastReset = data.LastResetDate;

        // Reset if we're in a different month OR if it's been more than 30 days
        var shouldReset = (now.Year > lastReset.Year && now.Month >= lastReset.Month) ||
                         (now.Year == lastReset.Year && now.Month > lastReset.Month) ||
                         (now - lastReset).TotalDays >= 30;

        if (shouldReset)
        {
            Console.WriteLine($"📅 Auto-resetting token usage counter (last reset: {lastReset:yyyy-MM-dd}, now: {now:yyyy-MM-dd})");
            data.PromptTokens = 0;
            data.CompletionTokens = 0;
            data.LastResetDate = now;
            SaveUsageUnsafe(data);
        }
    }
}
