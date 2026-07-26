using System.Collections.Concurrent;
using System.Text.Json;
using Serilog;

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
/// Tracks user activity in memory (so the 60s frontend poll stays a cheap
/// dictionary read) while persisting every update to the app_state table —
/// same KV pattern as MediaMetadataService — so presence survives a redeploy.
/// Previously pure in-memory: every container restart silently wiped
/// everyone's "last seen," which looked like Mom/Dad had never been active
/// even seconds after a deploy.
/// </summary>
public class UserActivityService : IUserActivityService
{
    private const string StateKey = "user-activity";

    // A gap longer than this since the user's last request counts as them
    // having left and come back — their "continuously active for" streak
    // resets rather than counting the idle time against it.
    private static readonly TimeSpan SessionIdleGap = TimeSpan.FromMinutes(10);

    private readonly Data.AppDatabase _db;
    private readonly ConcurrentDictionary<string, UserActivitySnapshot> _activities;

    public UserActivityService(Data.AppDatabase db)
    {
        _db = db;
        _activities = new ConcurrentDictionary<string, UserActivitySnapshot>(LoadFromDb());
    }

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

        // Fires on every authenticated request app-wide, not just the classified
        // ones — a full-blob upsert per call is trivial I/O for SQLite in WAL
        // mode at this app's scale (3 users, single NAS), so it's not worth the
        // extra complexity of debouncing/batching.
        Save();
    }

    public UserActivitySnapshot? GetActivity(string userName)
    {
        return _activities.TryGetValue(userName, out var snapshot) ? snapshot : null;
    }

    public IReadOnlyDictionary<string, UserActivitySnapshot> GetAllActivities()
    {
        return _activities;
    }

    private Dictionary<string, UserActivitySnapshot> LoadFromDb()
    {
        try
        {
            var json = _db.GetState(StateKey);
            if (json == null)
                return new Dictionary<string, UserActivitySnapshot>();

            return JsonSerializer.Deserialize<Dictionary<string, UserActivitySnapshot>>(json)
                ?? new Dictionary<string, UserActivitySnapshot>();
        }
        catch (Exception ex)
        {
            Log.Warning("[UserActivity] Failed to load persisted activity state: {Message}", ex.Message);
            return new Dictionary<string, UserActivitySnapshot>();
        }
    }

    private void Save()
    {
        try
        {
            _db.SetState(StateKey, JsonSerializer.Serialize(_activities));
        }
        catch (Exception ex)
        {
            Log.Warning("[UserActivity] Failed to persist activity state: {Message}", ex.Message);
        }
    }
}
