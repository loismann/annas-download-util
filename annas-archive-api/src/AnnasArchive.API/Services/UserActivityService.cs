using System.Collections.Concurrent;

namespace AnnasArchive.API.Services;

/// <summary>
/// Interface for tracking user activity timestamps.
/// </summary>
public interface IUserActivityService
{
    /// <summary>
    /// Records that a user was active at the current time.
    /// </summary>
    void RecordActivity(string userName);

    /// <summary>
    /// Gets the last activity time for a user, or null if not tracked.
    /// </summary>
    DateTime? GetLastActivity(string userName);

    /// <summary>
    /// Gets all tracked user activities.
    /// </summary>
    IReadOnlyDictionary<string, DateTime> GetAllActivities();
}

/// <summary>
/// In-memory implementation of user activity tracking.
/// </summary>
public class UserActivityService : IUserActivityService
{
    private readonly ConcurrentDictionary<string, DateTime> _activities = new();

    public void RecordActivity(string userName)
    {
        if (!string.IsNullOrWhiteSpace(userName))
        {
            _activities[userName] = DateTime.UtcNow;
        }
    }

    public DateTime? GetLastActivity(string userName)
    {
        return _activities.TryGetValue(userName, out var lastActivity)
            ? lastActivity
            : null;
    }

    public IReadOnlyDictionary<string, DateTime> GetAllActivities()
    {
        return _activities;
    }
}
