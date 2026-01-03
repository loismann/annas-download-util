using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Tracks download usage with 18-hour rolling window and persistence to disk
/// </summary>
public class DownloadTrackingService : IDownloadTrackingService
{
    private readonly string _trackingFilePath;
    private readonly object _fileLock = new();
    private readonly int _downloadLimit;
    private readonly TimeSpan _rollingWindow;

    private class DownloadRecord
    {
        public DateTime Timestamp { get; set; }
        public string Md5 { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
    }

    private class TrackingData
    {
        public List<DownloadRecord> Downloads { get; set; } = new();
    }

    public DownloadTrackingService(int downloadLimit = 50, double rollingWindowHours = 18, string? storagePath = null)
    {
        _downloadLimit = downloadLimit;
        _rollingWindow = TimeSpan.FromHours(rollingWindowHours);
        _trackingFilePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".annas-archive",
            "download-tracking.json"
        );

        // Initialize the file if it doesn't exist
        InitializeTracking();
    }

    public void RecordDownload(string md5, string userEmail)
    {
        lock (_fileLock)
        {
            var data = LoadTrackingDataUnsafe();

            // Add new download record
            data.Downloads.Add(new DownloadRecord
            {
                Timestamp = DateTime.UtcNow,
                Md5 = md5,
                UserEmail = userEmail
            });

            // Clean up old downloads outside the rolling window
            CleanupOldDownloadsUnsafe(data);

            SaveTrackingDataUnsafe(data);

            Console.WriteLine($"[DownloadTracking] Recorded download for {userEmail}, MD5: {md5}");
        }
    }

    public (int DownloadsLeft, int DownloadsPerDay) GetDownloadStatus()
    {
        lock (_fileLock)
        {
            var data = LoadTrackingDataUnsafe();

            // Clean up old downloads outside the rolling window
            CleanupOldDownloadsUnsafe(data);

            // Count downloads within the rolling window
            var downloadsInWindow = CountDownloadsInWindowUnsafe(data);
            var downloadsLeft = Math.Max(0, _downloadLimit - downloadsInWindow);

            return (downloadsLeft, _downloadLimit);
        }
    }

    public int GetDownloadLimit()
    {
        return _downloadLimit;
    }

    private void InitializeTracking()
    {
        lock (_fileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_trackingFilePath)!);

                if (!File.Exists(_trackingFilePath))
                {
                    var initialData = new TrackingData
                    {
                        Downloads = new List<DownloadRecord>()
                    };
                    SaveTrackingDataUnsafe(initialData);
                    Console.WriteLine($"[DownloadTracking] Initialized tracking file at {_trackingFilePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Failed to initialize download tracking: {ex.Message}");
            }
        }
    }

    private TrackingData LoadTrackingDataUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_trackingFilePath)!);

            if (!File.Exists(_trackingFilePath))
            {
                return new TrackingData
                {
                    Downloads = new List<DownloadRecord>()
                };
            }

            var json = File.ReadAllText(_trackingFilePath);
            return JsonSerializer.Deserialize<TrackingData>(json) ?? new TrackingData
            {
                Downloads = new List<DownloadRecord>()
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to load download tracking data: {ex.Message}");
            return new TrackingData
            {
                Downloads = new List<DownloadRecord>()
            };
        }
    }

    private void SaveTrackingDataUnsafe(TrackingData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_trackingFilePath)!);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_trackingFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to save download tracking data: {ex.Message}");
        }
    }

    private int CountDownloadsInWindowUnsafe(TrackingData data)
    {
        var cutoffTime = DateTime.UtcNow - _rollingWindow;
        return data.Downloads.Count(d => d.Timestamp >= cutoffTime);
    }

    private void CleanupOldDownloadsUnsafe(TrackingData data)
    {
        var cutoffTime = DateTime.UtcNow - _rollingWindow;
        var originalCount = data.Downloads.Count;

        data.Downloads = data.Downloads
            .Where(d => d.Timestamp >= cutoffTime)
            .ToList();

        var removedCount = originalCount - data.Downloads.Count;
        if (removedCount > 0)
        {
            SaveTrackingDataUnsafe(data);
            Console.WriteLine($"[DownloadTracking] Cleaned up {removedCount} old download record(s)");
        }
    }
}
