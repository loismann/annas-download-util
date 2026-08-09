using System.Text.Json;
using System.Text.Json.Nodes;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Data;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>What an indexer search turned up for one pool movie.</summary>
/// <param name="Grabbable">Releases Radarr would accept as-is under the movie's
/// quality profile.</param>
/// <param name="RejectedOnly">Releases that exist but Radarr would refuse (wrong
/// quality, too big, unwanted format). Kept separate from <paramref name="Grabbable"/>
/// because the two mean very different things: a movie with only rejected releases
/// is obtainable by loosening the profile, whereas one with no releases at all is
/// simply not out there and no amount of configuration will conjure it.</param>
public sealed record PoolAvailability(
    int MovieId,
    string Title,
    int? Year,
    int Grabbable,
    int RejectedOnly,
    DateTime CheckedUtc)
{
    public bool IsAvailable => Grabbable > 0;
}

/// <summary>Stored per person: when the announcement was first served to them,
/// and when (if ever) they acknowledged it.</summary>
public sealed record AnnouncementState(DateTime? ShownUtc, DateTime? DismissedUtc);

/// <summary>One household member's announcement state, for the admin view.</summary>
/// <remarks>Both null = never even loaded a page since it went live. Shown but
/// not dismissed = it appeared and they closed the tab instead of acknowledging
/// it, so it will show again.</remarks>
public sealed record AnnouncementRecipient(string Person, DateTime? ShownUtc, DateTime? DismissedUtc);

public sealed record AvailabilityScanStatus(
    bool Running,
    int Checked,
    int Total,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    string? Error);

/// <summary>
/// Answers "which movies in the Date Night pool could we actually get?" by running
/// Radarr's own interactive search against each one and recording the result.
///
/// This exists because the pool is deliberately made of obscure mid-century B-movies,
/// where a meaningful share simply have no release anywhere. Finding that out at the
/// moment a couple has already picked a movie and agreed on a night would be the worst
/// possible time, so availability is established up front and re-checked periodically;
/// only verified-available movies are eligible for a weekly draw.
///
/// The scan is paced rather than parallel on purpose — each check is a live query fanned
/// out to every configured indexer, and firing a few hundred of those as fast as the
/// server can manage is a good way to get rate-limited or banned by a tracker. Slow and
/// unattended is the right trade here; nothing is waiting on the result in real time.
/// </summary>
public class DateNightAvailabilityService
{
    private const string AvailabilityStateKey = "date-night:availability";
    private const string ScanStatusStateKey = "date-night:availability-scan";
    private const string AnnouncementStateKey = "date-night:announcement-seen";

    /// <summary>Gap between indexer searches. Deliberately generous — a full pass over
    /// a few hundred movies taking a couple of hours is fine, being throttled is not.</summary>
    private static readonly TimeSpan PacingDelay = TimeSpan.FromSeconds(20);

    /// <summary>How long an availability result is trusted before it's re-checked.
    /// Release availability drifts (new rips appear, old trackers go dark), but not
    /// fast enough to justify re-querying every indexer more often than this.</summary>
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Rejection reasons that say "you already have this, or something
    /// better, or it's already downloading" — NOT "this release doesn't exist".
    ///
    /// This distinction is the whole point of the scan. For a movie already on the
    /// server, Radarr rejects every single release as redundant, so a naive
    /// "rejected == false" count reports a perfectly obtainable film as having
    /// nothing available. What the pool needs to know is whether a release exists
    /// that Radarr *would* take if the movie weren't already handled.</summary>
    private static readonly string[] RedundancyRejections =
    [
        "already meets cutoff",
        "Existing file on disk",
        "already imported",
        "already been grabbed"
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppDatabase _db;

    /// <summary>Guards against two overlapping scans — each one hammers the same
    /// indexers, and the second would only duplicate the first's work.</summary>
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public DateNightAvailabilityService(IServiceScopeFactory scopeFactory, AppDatabase db)
    {
        _scopeFactory = scopeFactory;
        _db = db;
    }

    /// <summary>Every movie carrying the pool tag in Radarr, newest metadata first-hand
    /// from Radarr rather than cached here — Radarr is the source of truth for what's
    /// in the pool.</summary>
    public async Task<List<JsonObject>> GetPoolMoviesAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();

        var tagId = await radarr.EnsureTagAsync(DateNight.PoolTag, ct);
        var movies = await radarr.GetAllMoviesAsync(ct);

        return movies.OfType<JsonObject>()
            .Where(m => (m["tags"] as JsonArray)?.Any(t => (int?)t == tagId) == true)
            .ToList();
    }

    public Dictionary<int, PoolAvailability> GetAvailability()
    {
        var json = _db.GetState(AvailabilityStateKey);
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, PoolAvailability>>(json, JsonOptions) ?? new();
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "[DateNight] Availability state unreadable, starting fresh");
            return new();
        }
    }

    /// <summary>Whether a scan is running *in this process* — the only thing that
    /// can actually be true. The persisted flag can't answer this: a scan killed
    /// mid-pass (container restart, redeploy) leaves "Running: true" in the
    /// database forever, which would permanently block every future scan.</summary>
    public bool IsScanning => _scanLock.CurrentCount == 0;

    public AvailabilityScanStatus GetScanStatus()
    {
        var json = _db.GetState(ScanStatusStateKey);
        var stored = new AvailabilityScanStatus(false, 0, 0, null, null, null);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                stored = JsonSerializer.Deserialize<AvailabilityScanStatus>(json, JsonOptions) ?? stored;
            }
            catch (JsonException)
            {
                // Fall through to the empty default.
            }
        }

        // Reconcile the stored flag against reality. A scan interrupted by a
        // restart shows as finished-with-a-note rather than eternally running;
        // its per-movie results were persisted as it went, so resuming just means
        // scanning whatever is still unchecked.
        if (stored.Running && !IsScanning)
            return stored with { Running = false, Error = stored.Error ?? "Interrupted — restart the scan to finish the remaining movies." };

        return stored;
    }

    /// <summary>Checks every pool movie whose result is missing or stale.</summary>
    /// <param name="force">Re-check everything, including results still within
    /// <see cref="ResultLifetime"/>.</param>
    /// <param name="limit">Stop after this many checks — for trying the scan out on a
    /// handful of movies before committing to a multi-hour pass.</param>
    /// <returns>False if a scan was already running.</returns>
    public async Task<bool> RunScanAsync(bool force, int? limit, CancellationToken ct = default)
    {
        if (!await _scanLock.WaitAsync(0, ct)) return false;

        try
        {
            var pool = await GetPoolMoviesAsync(ct);
            var results = GetAvailability();
            var cutoff = DateTime.UtcNow - ResultLifetime;

            var pending = pool
                .Where(m => (int?)m["id"] is int id
                            && (force || !results.TryGetValue(id, out var r) || r.CheckedUtc < cutoff))
                .ToList();

            if (limit is int max)
                pending = pending.Take(max).ToList();

            var started = DateTime.UtcNow;
            SetScanStatus(new AvailabilityScanStatus(true, 0, pending.Count, started, null, null));
            Log.Information("[DateNight] Availability scan started — {Pending} of {Pool} pool movies to check",
                pending.Count, pool.Count);

            using var scope = _scopeFactory.CreateScope();
            var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();

            var done = 0;
            foreach (var movie in pending)
            {
                ct.ThrowIfCancellationRequested();

                var id = (int)movie["id"]!;
                var title = movie["title"]?.ToString() ?? $"#{id}";
                var year = (int?)movie["year"];

                try
                {
                    var releases = await radarr.SearchReleasesAsync(id, ct);
                    var grabbable = releases.OfType<JsonObject>().Count(IsObtainable);
                    results[id] = new PoolAvailability(
                        id, title, year, grabbable, releases.Count - grabbable, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    // A failed search is not evidence of unavailability — leaving no
                    // record at all means the next pass retries it, which is what we
                    // want. Writing a zero here would permanently mark a perfectly
                    // gettable movie as unobtainable.
                    Log.Warning(ex, "[DateNight] Release search failed for '{Title}' ({Id})", title, id);
                }

                done++;
                // Persisted every iteration so a restart mid-scan doesn't throw away
                // hours of indexer queries.
                SaveAvailability(results);
                SetScanStatus(new AvailabilityScanStatus(true, done, pending.Count, started, null, null));

                if (done < pending.Count)
                    await Task.Delay(PacingDelay, ct);
            }

            SetScanStatus(new AvailabilityScanStatus(false, done, pending.Count, started, DateTime.UtcNow, null));
            Log.Information("[DateNight] Availability scan finished — {Done} checked, {Available} of {Total} pool movies now known available",
                done, results.Values.Count(r => r.IsAvailable), pool.Count);
            return true;
        }
        catch (OperationCanceledException)
        {
            var status = GetScanStatus();
            SetScanStatus(status with { Running = false, FinishedUtc = DateTime.UtcNow, Error = "Cancelled" });
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DateNight] Availability scan failed");
            var status = GetScanStatus();
            SetScanStatus(status with { Running = false, FinishedUtc = DateTime.UtcNow, Error = ex.Message });
            return true;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>True when this release represents a real, obtainable copy of the
    /// film — either Radarr would grab it outright, or it only declined because the
    /// movie is already on disk or already downloading.
    ///
    /// Public so <see cref="DateNightCycleService"/> can reuse the exact same
    /// "would we actually accept this" judgment when picking a release to grab at
    /// schedule lock-in, instead of re-deriving the redundancy-rejection logic.</summary>
    public static bool IsObtainable(JsonObject release)
    {
        if ((bool?)release["rejected"] != true)
            return true;

        var rejections = release["rejections"] as JsonArray;
        if (rejections is null || rejections.Count == 0)
            return false;

        // Every reason must be a redundancy reason. If even one is a genuine
        // objection to the release itself (x265, unwanted quality, unparseable),
        // this copy is not something we'd actually accept.
        return rejections.All(r =>
        {
            var text = r?.ToString() ?? string.Empty;
            return RedundancyRejections.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>Per-person announcement state.
    ///
    /// Kept server-side rather than in browser storage on purpose: it's a fact
    /// about the human, not the browser. Mom and Dad each use their own devices
    /// day to day and share one only for movie night itself, so a localStorage
    /// flag would re-show the announcement on every new device and would also let
    /// whoever dismisses it first on the shared device hide it from the other.
    ///
    /// Shown and dismissed are tracked separately so "never saw it" can be told
    /// apart from "saw it and closed the tab without acknowledging" — those look
    /// identical if only dismissals are recorded, and they call for different
    /// follow-up.</summary>
    private Dictionary<string, AnnouncementState> LoadAnnouncementState()
    {
        var json = _db.GetState(AnnouncementStateKey);
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, AnnouncementState>>(json, JsonOptions);
            return parsed is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "[DateNight] Announcement state unreadable, starting fresh");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveAnnouncementState(Dictionary<string, AnnouncementState> state) =>
        _db.SetState(AnnouncementStateKey, JsonSerializer.Serialize(state, JsonOptions));

    /// <summary>Dismissed means acknowledged — the ✕ or the button was clicked.
    /// Merely having been shown it does not stop it reappearing, so someone who
    /// closes the tab still gets another chance.</summary>
    public bool HasSeenAnnouncement(string person) =>
        LoadAnnouncementState().TryGetValue(person, out var s) && s.DismissedUtc is not null;

    /// <summary>Records that the announcement was actually served to this person.
    /// First sighting only — the point is when they first met it, not how many
    /// times it has been re-rendered.</summary>
    public void RecordAnnouncementShown(string person)
    {
        var state = LoadAnnouncementState();
        if (state.TryGetValue(person, out var existing) && existing.ShownUtc is not null)
            return;

        state[person] = (existing ?? new AnnouncementState(null, null)) with { ShownUtc = DateTime.UtcNow };
        SaveAnnouncementState(state);
        Log.Information("[DateNight] Announcement shown to {Person} for the first time", person);
    }

    public void MarkAnnouncementSeen(string person)
    {
        var state = LoadAnnouncementState();
        state.TryGetValue(person, out var existing);
        if (existing?.DismissedUtc is not null) return;

        state[person] = (existing ?? new AnnouncementState(null, null)) with
        {
            // Someone could conceivably dismiss without a recorded showing (a
            // replayed request); backfill so the record is never half-empty.
            ShownUtc = existing?.ShownUtc ?? DateTime.UtcNow,
            DismissedUtc = DateTime.UtcNow
        };
        SaveAnnouncementState(state);
        Log.Information("[DateNight] {Person} dismissed the announcement", person);
    }

    /// <summary>Clears one person's announcement state entirely, as if they'd never
    /// loaded a page since it went live. The recovery path for a showing burned by
    /// someone testing on that person's account — logging into Dad's account to check
    /// something triggers exactly the same code path a real Dad would, consuming the
    /// one genuine showing meant for him.</summary>
    public void ResetAnnouncement(string person)
    {
        var state = LoadAnnouncementState();
        if (state.Remove(person))
        {
            SaveAnnouncementState(state);
            Log.Information("[DateNight] Announcement reset for {Person}", person);
        }
    }

    /// <summary>Every household member's announcement state, for the admin page —
    /// so "have they seen it yet?" is answerable without asking them.</summary>
    public List<AnnouncementRecipient> GetAnnouncementStatus()
    {
        var state = LoadAnnouncementState();
        return HouseholdOwners.Names
            .Where(n => !string.Equals(n, "Paul", StringComparison.OrdinalIgnoreCase))
            .Select(n =>
            {
                state.TryGetValue(n, out var s);
                return new AnnouncementRecipient(n, s?.ShownUtc, s?.DismissedUtc);
            })
            .ToList();
    }

    private void SaveAvailability(Dictionary<int, PoolAvailability> results) =>
        _db.SetState(AvailabilityStateKey, JsonSerializer.Serialize(results, JsonOptions));

    private void SetScanStatus(AvailabilityScanStatus status) =>
        _db.SetState(ScanStatusStateKey, JsonSerializer.Serialize(status, JsonOptions));
}
