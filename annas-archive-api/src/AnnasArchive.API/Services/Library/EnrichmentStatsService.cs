using System.Text.Json;
using Serilog;

namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Tracks API call statistics for the enrichment pipeline.
/// Persists stats to a JSON file for monitoring and debugging.
/// </summary>
public interface IEnrichmentStatsService
{
    void RecordCall(string service, bool success, double? confidence = null);
    void RecordBookProcessed(bool fullyEnriched);
    EnrichmentStats GetStats();
    Task SaveAsync(CancellationToken token = default);
    Task LoadAsync(CancellationToken token = default);
}

public class EnrichmentStatsService : IEnrichmentStatsService
{
    private readonly string _statsFilePath;
    private readonly object _lock = new();
    private EnrichmentStats _stats = new();

    public EnrichmentStatsService()
    {
        var statsDir = Environment.GetEnvironmentVariable("ENRICHMENT_STATS_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "stats");
        Directory.CreateDirectory(statsDir);
        _statsFilePath = Path.Combine(statsDir, "enrichment_stats.json");
    }

    public void RecordCall(string service, bool success, double? confidence = null)
    {
        lock (_lock)
        {
            if (!_stats.ServiceStats.TryGetValue(service, out var serviceStats))
            {
                serviceStats = new ServiceCallStats();
                _stats.ServiceStats[service] = serviceStats;
            }

            serviceStats.TotalCalls++;
            if (success)
            {
                serviceStats.SuccessfulCalls++;
                if (confidence.HasValue)
                {
                    serviceStats.TotalConfidence += confidence.Value;
                    serviceStats.ConfidenceCount++;
                }
            }
            else
            {
                serviceStats.FailedCalls++;
            }

            serviceStats.LastCallAt = DateTime.UtcNow;
            _stats.LastUpdated = DateTime.UtcNow;
        }
    }

    public void RecordBookProcessed(bool fullyEnriched)
    {
        lock (_lock)
        {
            _stats.TotalBooksProcessed++;
            if (fullyEnriched)
                _stats.FullyEnrichedBooks++;
            _stats.LastUpdated = DateTime.UtcNow;
        }
    }

    public EnrichmentStats GetStats()
    {
        lock (_lock)
        {
            return _stats.Clone();
        }
    }

    public async Task SaveAsync(CancellationToken token = default)
    {
        EnrichmentStats statsCopy;
        lock (_lock)
        {
            statsCopy = _stats.Clone();
        }

        try
        {
            var json = JsonSerializer.Serialize(statsCopy, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(_statsFilePath, json, token);
            Log.Debug("[EnrichmentStats] Saved stats to {Path}", _statsFilePath);
        }
        catch (Exception ex)
        {
            Log.Warning("[EnrichmentStats] Failed to save stats: {Error}", ex.Message);
        }
    }

    public async Task LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(_statsFilePath))
        {
            Log.Information("[EnrichmentStats] No existing stats file, starting fresh");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_statsFilePath, token);
            var loaded = JsonSerializer.Deserialize<EnrichmentStats>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (loaded != null)
            {
                lock (_lock)
                {
                    _stats = loaded;
                }
                Log.Information("[EnrichmentStats] Loaded stats: {Books} books processed, {Enriched} fully enriched",
                    _stats.TotalBooksProcessed, _stats.FullyEnrichedBooks);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[EnrichmentStats] Failed to load stats: {Error}", ex.Message);
        }
    }
}

public class EnrichmentStats
{
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public int TotalBooksProcessed { get; set; }
    public int FullyEnrichedBooks { get; set; }
    public Dictionary<string, ServiceCallStats> ServiceStats { get; set; } = new();

    public EnrichmentStats Clone()
    {
        return new EnrichmentStats
        {
            StartedAt = StartedAt,
            LastUpdated = LastUpdated,
            TotalBooksProcessed = TotalBooksProcessed,
            FullyEnrichedBooks = FullyEnrichedBooks,
            ServiceStats = ServiceStats.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Clone()
            )
        };
    }
}

public class ServiceCallStats
{
    public int TotalCalls { get; set; }
    public int SuccessfulCalls { get; set; }
    public int FailedCalls { get; set; }
    public double TotalConfidence { get; set; }
    public int ConfidenceCount { get; set; }
    public DateTime? LastCallAt { get; set; }

    public double SuccessRate => TotalCalls > 0 ? (double)SuccessfulCalls / TotalCalls : 0;
    public double AverageConfidence => ConfidenceCount > 0 ? TotalConfidence / ConfidenceCount : 0;

    public ServiceCallStats Clone()
    {
        return new ServiceCallStats
        {
            TotalCalls = TotalCalls,
            SuccessfulCalls = SuccessfulCalls,
            FailedCalls = FailedCalls,
            TotalConfidence = TotalConfidence,
            ConfidenceCount = ConfidenceCount,
            LastCallAt = LastCallAt
        };
    }
}
