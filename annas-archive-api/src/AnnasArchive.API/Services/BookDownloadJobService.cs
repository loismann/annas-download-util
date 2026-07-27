using System.Collections.Concurrent;

namespace AnnasArchive.API.Services;

public enum BookDownloadJobStatus
{
    Queued,
    Downloading,
    Complete,
    Error
}

/// <summary>
/// In-memory record of one background "send to library" download. Not persisted —
/// an app restart mid-download loses job status, same tradeoff LibraryIndexCache
/// and friends already make for this single-household app.
/// </summary>
public class BookDownloadJob
{
    public required string JobId { get; init; }
    public required string Title { get; init; }
    public BookDownloadJobStatus Status { get; set; } = BookDownloadJobStatus.Queued;
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public string? FileName { get; set; }
    public string? Message { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Tracks progress of book downloads that run detached from the HTTP request that
/// started them (see HandleSendToLibrary/HandleLibGenSendToLibrary) — large files
/// can take several minutes, longer than it's reasonable to hold a client
/// connection open for, so the download runs in the background and the frontend
/// polls this service's state via a jobId instead.
/// </summary>
public interface IBookDownloadJobService
{
    BookDownloadJob Start(string title);
    void UpdateProgress(string jobId, long bytesDownloaded, long? totalBytes);
    void Complete(string jobId, string fileName, string message);
    void Fail(string jobId, string message);
    BookDownloadJob? Get(string jobId);
}

public class BookDownloadJobService : IBookDownloadJobService
{
    // How long a finished (complete/error) job's status stays queryable before
    // being pruned — long enough that a client polling every couple seconds
    // never misses the final state, short enough this dictionary can't grow
    // unbounded over the app's lifetime.
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, BookDownloadJob> _jobs = new();

    public BookDownloadJob Start(string title)
    {
        PruneStaleJobs();

        var job = new BookDownloadJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            Title = title
        };
        _jobs[job.JobId] = job;
        return job;
    }

    public void UpdateProgress(string jobId, long bytesDownloaded, long? totalBytes)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.Status = BookDownloadJobStatus.Downloading;
        job.BytesDownloaded = bytesDownloaded;
        job.TotalBytes = totalBytes;
        job.UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Complete(string jobId, string fileName, string message)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.Status = BookDownloadJobStatus.Complete;
        job.FileName = fileName;
        job.Message = message;
        job.UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Fail(string jobId, string message)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.Status = BookDownloadJobStatus.Error;
        job.Message = message;
        job.UpdatedAtUtc = DateTime.UtcNow;
    }

    public BookDownloadJob? Get(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    private void PruneStaleJobs()
    {
        var cutoff = DateTime.UtcNow - RetentionPeriod;
        foreach (var (key, job) in _jobs)
        {
            if (job.UpdatedAtUtc < cutoff)
                _jobs.TryRemove(key, out _);
        }
    }
}
