using System.Collections.Concurrent;

namespace AnnasArchive.API.Services;

/// <summary>Snapshot of a user's most recent activity — last-seen time, the
/// broad action category of their most recent classified request, and when
/// their current unbroken activity streak began (resets after an idle gap).</summary>
public record UserActivitySnapshot(DateTime LastSeenUtc, DateTime SessionStartUtc, string? LastAction);

/// <summary>
/// Interface for tracking user activity — presence and a broad "what are
/// they doing" category, not a full audit log.
/// </summary>
public interface IUserActivityService
{
    /// <summary>Records that a user was active at the current time.
    /// <paramref name="action"/>, if given, becomes the user's new "last
    /// action" category; a null action (e.g. a background polling request)
    /// just refreshes the last-seen time without overwriting what they were
    /// last seen doing.</summary>
    void RecordActivity(string userName, string? action = null);

    /// <summary>Gets the current activity snapshot for a user, or null if not tracked.</summary>
    UserActivitySnapshot? GetActivity(string userName);

    /// <summary>Gets all tracked user activities.</summary>
    IReadOnlyDictionary<string, UserActivitySnapshot> GetAllActivities();
}

/// <summary>
/// In-memory implementation of user activity tracking.
/// </summary>
public class UserActivityService : IUserActivityService
{
    // A gap longer than this since the user's last request counts as them
    // having left and come back — their "continuously active for" streak
    // resets rather than counting the idle time against it.
    private static readonly TimeSpan SessionIdleGap = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, UserActivitySnapshot> _activities = new();

    public void RecordActivity(string userName, string? action = null)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return;

        var now = DateTime.UtcNow;
        _activities.AddOrUpdate(
            userName,
            _ => new UserActivitySnapshot(now, now, action),
            (_, existing) =>
            {
                var sessionStart = now - existing.LastSeenUtc > SessionIdleGap ? now : existing.SessionStartUtc;
                return new UserActivitySnapshot(now, sessionStart, action ?? existing.LastAction);
            });
    }

    public UserActivitySnapshot? GetActivity(string userName)
    {
        return _activities.TryGetValue(userName, out var snapshot) ? snapshot : null;
    }

    public IReadOnlyDictionary<string, UserActivitySnapshot> GetAllActivities()
    {
        return _activities;
    }
}
