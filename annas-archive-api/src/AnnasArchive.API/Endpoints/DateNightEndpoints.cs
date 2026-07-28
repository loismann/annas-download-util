using System.Text.Json.Nodes;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>One pool movie as the UI needs it: enough Radarr metadata to render a
/// tile, plus whether we've established that it can actually be obtained.
/// <c>Available</c> is null when the movie has never been checked — distinct from
/// false, which means it was checked and nothing grabbable came back.</summary>
public record DateNightPoolItem(
    int MovieId,
    string Title,
    int? Year,
    string? Overview,
    string? PosterUrl,
    bool HasFile,
    bool Monitored,
    bool? Available,
    int GrabbableReleases,
    int RejectedReleases,
    DateTime? CheckedUtc);

/// <summary>Headline numbers for the pool — the answer to "is there enough here to
/// run this feature?", which is the question that gates everything downstream.</summary>
public record DateNightPoolSummary(
    int Total,
    int Available,
    int Unavailable,
    int Unchecked,
    int AlreadyDownloaded);

/// <summary>The one-time "coming soon" splash for Mom and Dad — whether to show it,
/// and poster URLs to decorate it with. <c>Live</c> tells the frontend whether the
/// real feature (flyer/lobby/scheduling) has been switched on yet — while false, the
/// page never renders anything but this poster, regardless of what's built behind it.</summary>
public record DateNightAnnouncement(bool ShouldShow, List<string> Posters, bool Live);

/// <summary>One of this week's drawn movies, as both the admin panel and the real
/// flyer need it. <c>Summary</c> is the AI pitch line — null until generated (lazily,
/// on first read) or if generation failed, in which case the caller falls back to the
/// plain overview.</summary>
public record CycleMovieView(int MovieId, string Title, string? PosterUrl, int? TmdbId, string? Overview, string? Summary, string? MomVote, string? DadVote);

/// <summary>A movie sitting in never-show or a disagreement cooling-off — the "collapsed
/// section" the spec calls for, so a mis-tap has a visible way back.</summary>
public record RecoverableMovie(int MovieId, string Title, string Reason, DateTime Since);

/// <summary>Everything the admin "Weekly cycle" panel needs to render and drive a full
/// cycle end to end — including the schedule handshake and cleanup, so the whole
/// pipeline is testable without a Mom/Dad login or the real flyer.</summary>
public record CycleAdminView(
    string? CycleId,
    string Status,
    DateTime? DeadlineUtc,
    DateTime? ResolvedUtc,
    List<CycleMovieView> Movies,
    int? ResolvedMovieId,
    ScheduleState? Schedule,
    SkipState Skip,
    bool Live,
    int NeverShowCount,
    int WatchedCount,
    int CoolingOffCount,
    List<RecoverableMovie> Recoverable);

public record DateNightPoolResponse(
    DateNightPoolSummary Summary,
    AvailabilityScanStatus Scan,
    List<DateNightPoolItem> Items,
    List<AnnouncementRecipient> Announcement,
    CycleAdminView? Cycle);

/// <summary>What Mom or Dad see when they check this week's draw.</summary>
public record CycleView(
    string? CycleId,
    string Status,
    DateTime? DeadlineUtc,
    List<CycleMovieView> Movies,
    Dictionary<int, string> MyVotes,
    int? ResolvedMovieId,
    string? ResolvedTitle,
    ScheduleState? Schedule,
    bool ShouldShowFlyerToday,
    bool Skipped);

public record CastVoteRequest(int MovieId, string Vote);
public record SetSkipRequest(string Scope);
public record AdminCastVoteRequest(string Person, int MovieId, string Vote);
public record ProposeScheduleRequest(List<ProposedSlot> Slots);
public record ApproveScheduleRequest(ProposedSlot Slot);
public record AdminProposeScheduleRequest(string Person, List<ProposedSlot> Slots);
public record AdminApproveScheduleRequest(string Person, ProposedSlot Slot);
public record AdminCancelScheduleRequest(string Person);

/// <summary>
/// Date Night pool + availability endpoints. See DOCS/DATE_NIGHT_FEATURE.md.
///
/// Only the pool and its availability pre-pass live here so far — the weekly draw,
/// voting, and scheduling are later phases, deliberately not built until the real
/// availability numbers say the pool is big enough to sustain three fresh picks a week.
/// </summary>
public static class DateNightEndpoints
{
    public static WebApplication MapDateNightEndpoints(this WebApplication app)
    {
        // AdminOnly, not merely authenticated: until the feature is deliberately
        // switched on, Mom and Dad must not be able to learn it exists — including
        // by hitting these URLs directly. Pool administration stays admin-only
        // permanently; the eventual flyer/voting endpoints are the ones that will
        // be exposed to them, and only then.
        app.MapGet("/api/date-night/pool", HandleGetPool)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/availability/scan", HandleStartScan)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        // Announcement routes are for Mom and Dad, so plain authorization — these
        // are the only Date Night endpoints they may reach. They expose nothing
        // but a few poster URLs and a boolean.
        app.MapGet("/api/date-night/announcement", HandleGetAnnouncement)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/announcement/dismiss", HandleDismissAnnouncement)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Weekly cycle routes for Mom and Dad — built now for phase 4 (the flyer) to
        // consume; nothing in this app calls them yet.
        app.MapGet("/api/date-night/cycle", HandleGetCycle)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/vote", HandleCastVote)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/skip", HandleSetSkip)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/flyer-shown", HandleFlyerShown)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/schedule/propose", HandleProposeSchedule)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/schedule/approve", HandleApproveSchedule)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/schedule/cancel", HandleCancelSchedule)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Polled every 30-60s from AppComponent (any page, not just /date-night) —
        // this app has no push notifications, so this is how the countdown surfaces.
        app.MapGet("/api/date-night/showtime-check", HandleShowtimeCheck)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/mark-watched", HandleMarkWatched)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Admin-only cycle controls — how phase 3 (and now 4-7) is exercised end to
        // end before the real flyer/scheduling UI is live for Mom and Dad. See
        // DOCS/DATE_NIGHT_FEATURE.md.
        app.MapPost("/api/date-night/cycle/admin/force-issue", HandleAdminForceIssue)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/resolve-now", HandleAdminResolveNow)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/discard", HandleAdminDiscard)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/vote", HandleAdminCastVote)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/restore/{movieId:int}", HandleAdminRestore)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/skip/clear", HandleAdminClearSkip)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/schedule/propose", HandleAdminProposeSchedule)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/schedule/approve", HandleAdminApproveSchedule)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/schedule/cancel", HandleAdminCancelSchedule)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/mark-watched", HandleAdminMarkWatched)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        // The leak-prevention gate — see DateNightCycleService.IsLive.
        app.MapPost("/api/date-night/cycle/admin/go-live", HandleAdminGoLive)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapPost("/api/date-night/cycle/admin/go-dark", HandleAdminGoDark)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        return app;
    }

    /// <summary>Whether this person should see the one-time "coming soon" splash,
    /// plus a handful of pool posters to decorate it with.
    ///
    /// Tracked per person, so Mom dismissing it has no effect on Dad — they each
    /// get exactly one showing. Paul is excluded by default (he built the thing)
    /// but can pass ?preview=true to see it without being marked as having seen it,
    /// which is also how it gets checked before it goes live.</summary>
    private static async Task<IResult> HandleGetAnnouncement(
        HttpContext context,
        DateNightAvailabilityService availability,
        DateNightCycleService cycles,
        [FromQuery] bool preview = false)
    {
        var live = cycles.IsLive();
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (person is null)
            return Results.Ok(new DateNightAnnouncement(false, [], live));

        var isAudience = !string.Equals(person, "Paul", StringComparison.OrdinalIgnoreCase);
        var owed = isAudience && !availability.HasSeenAnnouncement(person);
        if (!preview && !owed)
            return Results.Ok(new DateNightAnnouncement(false, [], live));

        // Record the first genuine sighting, so "never saw it" can be told apart
        // from "saw it and closed the tab". A preview is Paul checking his own
        // work and must not count as either.
        if (owed && !preview)
            availability.RecordAnnouncementShown(person);

        var posters = new List<string>();
        try
        {
            var pool = await availability.GetPoolMoviesAsync();
            // A varied selection rather than the first N alphabetically, so the
            // reel doesn't come out as two dozen near-identical 1940s horror
            // one-sheets. Enough to fill a long scrolling marquee without the
            // same poster coming round again too soon.
            posters = pool
                .Select(PosterUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .OrderBy(_ => Random.Shared.Next())
                .Take(24)
                .Select(u => u!)
                .ToList();
        }
        catch (Exception ex)
        {
            // Posters are decoration. A Radarr hiccup shouldn't stop the
            // announcement itself from appearing.
            Log.Warning("[DateNight] Could not load announcement posters: {Message}", ex.Message);
        }

        return Results.Ok(new DateNightAnnouncement(true, posters, live));
    }

    private static IResult HandleDismissAnnouncement(
        HttpContext context, DateNightAvailabilityService availability)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (person is null)
            return Results.BadRequest(new { error = "Could not identify the current user." });

        availability.MarkAnnouncementSeen(person);
        return Results.Ok(new { dismissed = true });
    }

    private static async Task<IResult> HandleGetPool(
        DateNightAvailabilityService availability, DateNightCycleService cycles, DateNightSummaryService summaries)
    {
        try
        {
            var movies = await availability.GetPoolMoviesAsync();
            var results = availability.GetAvailability();

            CycleAdminView? cycleView = null;
            try
            {
                cycleView = await BuildCycleAdminViewAsync(cycles, summaries, movies);
            }
            catch (Exception ex)
            {
                // The pool table is the important part of this page; a cycle-side
                // hiccup shouldn't take it down too.
                Log.Warning("[DateNight] Could not build the cycle admin view: {Message}", ex.Message);
            }

            var items = movies.Select(m =>
            {
                var id = (int)(m["id"] ?? 0);
                results.TryGetValue(id, out var a);
                return new DateNightPoolItem(
                    id,
                    m["title"]?.ToString() ?? "",
                    (int?)m["year"],
                    m["overview"]?.ToString(),
                    PosterUrl(m),
                    (bool?)m["hasFile"] ?? false,
                    (bool?)m["monitored"] ?? false,
                    a?.IsAvailable,
                    a?.Grabbable ?? 0,
                    a?.RejectedOnly ?? 0,
                    a?.CheckedUtc);
            })
            .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

            var summary = new DateNightPoolSummary(
                items.Count,
                items.Count(i => i.Available == true),
                items.Count(i => i.Available == false),
                items.Count(i => i.Available is null),
                items.Count(i => i.HasFile));

            return Results.Ok(new DateNightPoolResponse(
                summary, availability.GetScanStatus(), items, availability.GetAnnouncementStatus(), cycleView));
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[DateNight] Pool fetch failed: {Message}", ex.Message);
            return Results.Json(new { error = "Radarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Shared between the admin panel and the Mom/Dad cycle view — resolves
    /// this week's drawn movie ids against already-fetched pool metadata (no second
    /// Radarr call) and attaches each movie's AI pitch line, generating it on first
    /// read if the cache (<see cref="DateNightSummaryService"/>) doesn't have one yet.</summary>
    private static async Task<List<CycleMovieView>> ResolveCycleMoviesAsync(
        WeeklyCycle cycle, List<JsonObject> poolMovies, DateNightSummaryService summaries, string? attributedTo = null)
    {
        var byId = poolMovies
            .Where(m => (int?)m["id"] is int)
            .ToDictionary(m => (int)m["id"]!);

        var result = new List<CycleMovieView>();
        foreach (var id in cycle.MovieIds)
        {
            byId.TryGetValue(id, out var movie);
            cycle.Votes.TryGetValue("Mom", out var momVotes);
            cycle.Votes.TryGetValue("Dad", out var dadVotes);
            string? momVote = null, dadVote = null;
            momVotes?.TryGetValue(id, out momVote);
            dadVotes?.TryGetValue(id, out dadVote);

            var title = movie?["title"]?.ToString() ?? $"#{id}";
            var overview = movie?["overview"]?.ToString();
            var year = movie is null ? null : (int?)movie["year"];
            var tmdbId = movie is null ? null : (int?)movie["tmdbId"];
            var summary = await summaries.GetOrGenerateSummaryAsync(id, title, year, overview, attributedTo);

            result.Add(new CycleMovieView(
                id, title, movie is null ? null : PosterUrl(movie), tmdbId, overview, summary, momVote, dadVote));
        }
        return result;
    }

    private static async Task<CycleAdminView> BuildCycleAdminViewAsync(
        DateNightCycleService cycles, DateNightSummaryService summaries, List<JsonObject> poolMovies)
    {
        var cycle = await cycles.GetCurrentCycleAsync();
        var skip = cycles.GetSkip();
        var lists = cycles.GetLists();
        var coolingCutoff = DateTime.UtcNow - DateNightCycleService.CoolingOff;

        var titleById = poolMovies
            .Where(m => (int?)m["id"] is int)
            .ToDictionary(m => (int)m["id"]!, m => m["title"]?.ToString() ?? "");

        var recoverable = lists
            .Where(kv => kv.Value.NeverShowAgain || (kv.Value.LastDisagreedUtc is DateTime d && d > coolingCutoff))
            .Select(kv =>
            {
                var (id, entry) = (kv.Key, kv.Value);
                var title = titleById.TryGetValue(id, out var t) ? t : $"#{id}";
                return entry.NeverShowAgain
                    ? new RecoverableMovie(id, title, "Never show again", entry.NeverShowAgainUtc ?? DateTime.UtcNow)
                    : new RecoverableMovie(id, title, "Disagreed — cooling off", entry.LastDisagreedUtc!.Value);
            })
            .OrderByDescending(r => r.Since)
            .ToList();

        return new CycleAdminView(
            cycle?.CycleId,
            cycle?.Status ?? (skip.SkipUntilUtc > DateTime.UtcNow ? "Skipped" : "None"),
            cycle?.DeadlineUtc,
            cycle?.ResolvedUtc,
            cycle is null ? [] : await ResolveCycleMoviesAsync(cycle, poolMovies, summaries, "Paul"),
            cycle?.ResolvedMovieId,
            cycle?.Schedule,
            skip,
            cycles.IsLive(),
            lists.Count(kv => kv.Value.NeverShowAgain),
            lists.Count(kv => kv.Value.Watched),
            lists.Count(kv => kv.Value.LastDisagreedUtc is DateTime d && d > coolingCutoff),
            recoverable);
    }

    /// <summary>Whether this person should even be allowed to touch the weekly cycle —
    /// same audience rule as the announcement, so Paul can't accidentally vote and Mom/Dad
    /// can't skip on his behalf.</summary>
    private static bool IsAudience(string? person) =>
        person is not null && !string.Equals(person, "Paul", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> HandleGetCycle(
        HttpContext context, DateNightAvailabilityService availability, DateNightCycleService cycles, DateNightSummaryService summaries)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (!IsAudience(person))
            return Results.Ok(new CycleView(null, "None", null, [], new(), null, null, null, false, false));

        var cycle = await cycles.GetCurrentCycleAsync();
        if (cycle is null)
        {
            var skip = cycles.GetSkip();
            return Results.Ok(new CycleView(
                null, "None", null, [], new(), null, null, null, false, skip.SkipUntilUtc > DateTime.UtcNow));
        }

        var poolMovies = await availability.GetPoolMoviesAsync();
        var moviesView = await ResolveCycleMoviesAsync(cycle, poolMovies, summaries, person);
        var myVotes = cycle.Votes.TryGetValue(person!, out var votes) ? votes : new Dictionary<int, string>();
        var resolvedTitle = cycle.ResolvedMovieId is int rid
            ? moviesView.FirstOrDefault(m => m.MovieId == rid)?.Title
            : null;

        return Results.Ok(new CycleView(
            cycle.CycleId, cycle.Status, cycle.DeadlineUtc, moviesView, myVotes,
            cycle.ResolvedMovieId, resolvedTitle, cycle.Schedule,
            cycles.IsFlyerOwedToday(person!, cycle), false));
    }

    private static readonly IResult NotAudienceResult =
        Results.Json(new { error = "Only Mom and Dad can do this." }, statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IResult> HandleCastVote(HttpContext context, DateNightCycleService cycles, [FromBody] CastVoteRequest request)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (!IsAudience(person))
            return NotAudienceResult;

        try
        {
            await cycles.CastVoteAsync(person!, request.MovieId, request.Vote);
            return Results.Ok(new { voted = true });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult HandleSetSkip(HttpContext context, DateNightCycleService cycles, [FromBody] SetSkipRequest request)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (!IsAudience(person))
            return NotAudienceResult;

        if (request.Scope is not ("week" or "month"))
            return Results.BadRequest(new { error = "scope must be \"week\" or \"month\"." });

        cycles.SetSkip(person!, request.Scope);
        return Results.Ok(new { skipped = true });
    }

    private static async Task<IResult> HandleFlyerShown(HttpContext context, DateNightCycleService cycles)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (!IsAudience(person))
            return NotAudienceResult;

        await cycles.RecordFlyerShownAsync(person!);
        return Results.Ok(new { recorded = true });
    }

    private static async Task<IResult> HandleProposeSchedule(
        HttpContext context, DateNightCycleService cycles, [FromBody] ProposeScheduleRequest request)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (!IsAudience(person))
            return NotAudienceResult;

        try
        {
            await cycles.ProposeScheduleAsync(person!, request.Slots);
            return Results.Ok(new { proposed = true });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> HandleApproveSchedule(
        HttpContext context, DateNightCycleService cycles, [FromBody] ApproveScheduleRequest request)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (!IsAudience(person))
            return NotAudienceResult;

        try
        {
            await cycles.ApproveScheduleAsync(person!, request.Slot);
            return Results.Ok(new { locked = true });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> HandleCancelSchedule(HttpContext context, DateNightCycleService cycles)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (!IsAudience(person))
            return NotAudienceResult;

        try
        {
            await cycles.CancelScheduleAsync(person!);
            return Results.Ok(new { cancelled = true });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult HandleShowtimeCheck(DateNightCycleService cycles) =>
        Results.Ok(cycles.GetShowtimeStatus());

    private static async Task<IResult> HandleMarkWatched(HttpContext context, DateNightCycleService cycles)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (!IsAudience(person))
            return NotAudienceResult;

        var cycle = await cycles.GetCurrentCycleAsync();
        if (cycle?.Schedule?.Status != "Locked" || cycle.ResolvedMovieId is not int movieId)
            return Results.Conflict(new { error = "Nothing locked in to mark watched." });

        await cycles.MarkWatchedAsync(movieId);
        return Results.Ok(new { watched = true });
    }

    private static async Task<IResult> HandleAdminForceIssue(DateNightCycleService cycles)
    {
        var cycle = await cycles.ForceIssueAsync();
        return cycle is null
            ? Results.Conflict(new { error = "No eligible movies to draw from." })
            : Results.Ok(new { issued = true, cycle.CycleId });
    }

    private static async Task<IResult> HandleAdminResolveNow(DateNightCycleService cycles)
    {
        var cycle = await cycles.ResolveNowAsync();
        return cycle is null
            ? Results.Conflict(new { error = "No cycle to resolve." })
            : Results.Ok(new { cycle.Status, cycle.ResolvedMovieId });
    }

    private static IResult HandleAdminDiscard(DateNightCycleService cycles)
    {
        cycles.DiscardCycle();
        return Results.Ok(new { discarded = true });
    }

    private static async Task<IResult> HandleAdminCastVote(DateNightCycleService cycles, [FromBody] AdminCastVoteRequest request)
    {
        try
        {
            await cycles.CastVoteAsync(request.Person, request.MovieId, request.Vote);
            return Results.Ok(new { voted = true });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult HandleAdminRestore(DateNightCycleService cycles, int movieId)
    {
        cycles.RestoreMovie(movieId);
        return Results.Ok(new { restored = true });
    }

    private static IResult HandleAdminClearSkip(DateNightCycleService cycles)
    {
        cycles.ClearSkip();
        return Results.Ok(new { cleared = true });
    }

    private static async Task<IResult> HandleAdminProposeSchedule(
        DateNightCycleService cycles, [FromBody] AdminProposeScheduleRequest request)
    {
        try
        {
            await cycles.ProposeScheduleAsync(request.Person, request.Slots);
            return Results.Ok(new { proposed = true });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> HandleAdminApproveSchedule(
        DateNightCycleService cycles, [FromBody] AdminApproveScheduleRequest request)
    {
        try
        {
            await cycles.ApproveScheduleAsync(request.Person, request.Slot);
            return Results.Ok(new { locked = true });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> HandleAdminCancelSchedule(
        DateNightCycleService cycles, [FromBody] AdminCancelScheduleRequest request)
    {
        try
        {
            await cycles.CancelScheduleAsync(request.Person);
            return Results.Ok(new { cancelled = true });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> HandleAdminMarkWatched(DateNightCycleService cycles)
    {
        var cycle = await cycles.GetCurrentCycleAsync();
        if (cycle?.Schedule?.Status != "Locked" || cycle.ResolvedMovieId is not int movieId)
            return Results.Conflict(new { error = "Nothing locked in to mark watched." });

        await cycles.MarkWatchedAsync(movieId);
        return Results.Ok(new { watched = true });
    }

    private static IResult HandleAdminGoLive(DateNightCycleService cycles)
    {
        cycles.SetLive(true);
        return Results.Ok(new { live = true });
    }

    private static IResult HandleAdminGoDark(DateNightCycleService cycles)
    {
        cycles.SetLive(false);
        return Results.Ok(new { live = false });
    }

    /// <summary>Kicks the scan off and returns immediately — a full pass is deliberately
    /// paced and runs for hours, so it can't be the body of an HTTP request. Progress is
    /// read back from GET /api/date-night/pool.</summary>
    private static IResult HandleStartScan(
        DateNightAvailabilityService availability,
        [FromQuery] bool force = false,
        [FromQuery] int? limit = null)
    {
        // IsScanning, not the persisted flag — see DateNightAvailabilityService.
        if (availability.IsScanning)
            return Results.Conflict(new { error = "A scan is already running." });

        // Detached on purpose: nothing is waiting on this, and the request that
        // started it will be long gone before it finishes.
        _ = Task.Run(async () =>
        {
            try
            {
                await availability.RunScanAsync(force, limit);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DateNight] Background availability scan crashed");
            }
        });

        return Results.Accepted(value: new { started = true });
    }

    /// <summary>Radarr serves posters from its own host; the remote (TMDB) URL is the
    /// one that works from a browser without proxying through Radarr's API key.</summary>
    private static string? PosterUrl(JsonObject movie)
    {
        var images = movie["images"] as JsonArray;
        var poster = images?.OfType<JsonObject>()
            .FirstOrDefault(i => string.Equals(i["coverType"]?.ToString(), "poster", StringComparison.OrdinalIgnoreCase));
        return poster?["remoteUrl"]?.ToString() ?? poster?["url"]?.ToString();
    }
}
