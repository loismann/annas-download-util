using System.Text.Json.Nodes;
using AnnasArchive.API.Constants;
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
/// plain overview. <c>Genre</c> is up to two of Radarr's own genres, joined for
/// display — not the app's separate `customGenres` household tagging.</summary>
public record CycleMovieView(
    int MovieId, string Title, string? PosterUrl, int? TmdbId, string? Overview, string? Summary,
    int? Year, string? Genre, bool HasFile, bool Monitored, string? MomVote, string? DadVote);

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

/// <param name="TestCycle">The dry run's own cycle — same shape as <c>Cycle</c>,
/// sourced from completely separate storage. Read-only here; it's driven from the
/// real /date-night page via admin impersonation, not from this panel.</param>
public record DateNightPoolResponse(
    DateNightPoolSummary Summary,
    AvailabilityScanStatus Scan,
    List<DateNightPoolItem> Items,
    List<AnnouncementRecipient> Announcement,
    CycleAdminView? Cycle,
    CycleAdminView? TestCycle);

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
    bool ShouldShowScheduleReminderToday,
    bool Skipped);

public record ResetAnnouncementRequest(string Person);
public record CastVoteRequest(int MovieId, string Vote);
public record SetSkipRequest(string Scope);
public record ProposeScheduleRequest(List<ProposedSlot> Slots);
public record ApproveScheduleRequest(ProposedSlot Slot);

/// <summary>
/// Date Night pool + availability endpoints. See DOCS/features/DATE_NIGHT.md.
///
/// Only the pool and its availability pre-pass live here so far — the weekly draw,
/// voting, and scheduling are later phases, deliberately not built until the real
/// availability numbers say the pool is big enough to sustain five fresh picks a week.
/// </summary>
public static class DateNightEndpoints
{
    public static WebApplication MapDateNightEndpoints(this WebApplication app)
    {
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        //
        // Two groups off the same prefix rather than one group plus per-route
        // overrides: an AdminOnly route must not be able to end up inheriting plain
        // authorization, and a route joining the wrong group is a visible mistake
        // where a missing override is an invisible one.
        var adminGroup = app.MapGroup("/api/date-night")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");
        var group = app.MapGroup("/api/date-night")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // AdminOnly, not merely authenticated: until the feature is deliberately
        // switched on, Mom and Dad must not be able to learn it exists — including
        // by hitting these URLs directly. Pool administration stays admin-only
        // permanently; the eventual flyer/voting endpoints are the ones that will
        // be exposed to them, and only then.
        adminGroup.MapGet("/pool", HandleGetPool);

        adminGroup.MapPost("/availability/scan", HandleStartScan);

        // Announcement routes are for Mom and Dad, so plain authorization — these
        // are the only Date Night endpoints they may reach. They expose nothing
        // but a few poster URLs and a boolean.
        group.MapGet("/announcement", HandleGetAnnouncement);

        group.MapPost("/announcement/dismiss", HandleDismissAnnouncement);

        // Admin recovery path for a showing burned by testing on Mom/Dad's own
        // account — resets their announcement state as if they'd never seen it.
        adminGroup.MapPost("/announcement/admin/reset", HandleAdminResetAnnouncement);

        // Weekly cycle routes for Mom and Dad — built now for phase 4 (the flyer) to
        // consume; nothing in this app calls them yet.
        group.MapGet("/cycle", HandleGetCycle);

        group.MapPost("/cycle/vote", HandleCastVote);

        group.MapPost("/skip", HandleSetSkip);

        group.MapPost("/cycle/flyer-shown", HandleFlyerShown);

        group.MapPost("/cycle/schedule/propose", HandleProposeSchedule);

        group.MapPost("/cycle/schedule/approve", HandleApproveSchedule);

        group.MapPost("/cycle/schedule/cancel", HandleCancelSchedule);

        // Marks that this person has seen the schedule's current state (a proposal
        // waiting on them, or a cancellation) — stops the "your turn"/"cancelled"
        // popup from reappearing on their next load until the state changes again.
        group.MapPost("/cycle/schedule/acknowledge", HandleAcknowledgeSchedule);

        group.MapPost("/cycle/download/retry", HandleRetryDownload);

        // Polled every 30-60s from AppComponent (any page, not just /date-night) —
        // this app has no push notifications, so this is how the countdown surfaces.
        group.MapGet("/showtime-check", HandleShowtimeCheck);

        group.MapPost("/cycle/showtime/start", HandleStartShowtime);

        group.MapPost("/cycle/mark-watched", HandleMarkWatched);

        // Admin-only cycle controls — how phase 3 (and now 4-7) is exercised end to
        // end before the real flyer/scheduling UI is live for Mom and Dad. See
        // DOCS/features/DATE_NIGHT.md.
        adminGroup.MapPost("/cycle/admin/force-issue", HandleAdminForceIssue);

        adminGroup.MapPost("/cycle/admin/resolve-now", HandleAdminResolveNow);

        adminGroup.MapPost("/cycle/admin/discard", HandleAdminDiscard);

        adminGroup.MapPost("/cycle/admin/restore/{movieId:int}", HandleAdminRestore);

        adminGroup.MapPost("/cycle/admin/skip/clear", HandleAdminClearSkip);

        // The leak-prevention gate — see DateNightCycleService.IsLive.
        adminGroup.MapPost("/cycle/admin/go-live", HandleAdminGoLive);

        adminGroup.MapPost("/cycle/admin/go-dark", HandleAdminGoDark);

        // The dry run has one reset control only. There is deliberately no
        // resolve-now bypass: both complete Mom/Dad ballots are required, exactly
        // as they are in the real person-facing flow.
        adminGroup.MapPost("/cycle/admin/test/reset", HandleAdminResetDryRun);

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
                .Select(DateNightViews.PosterUrl)
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
            Log.Warning(ex, "[DateNight] Could not load announcement posters");
        }

        return Results.Ok(new DateNightAnnouncement(true, posters, live));
    }

    private static IResult HandleDismissAnnouncement(
        HttpContext context, DateNightAvailabilityService availability)
    {
        var person = LibraryHelpers.ResolveUserDisplayName(context);
        if (person is null)
            return ApiResponse.BadRequest("Could not identify the current user.");

        availability.MarkAnnouncementSeen(person);
        return Results.Ok(new { dismissed = true });
    }

    private static IResult HandleAdminResetAnnouncement(
        DateNightAvailabilityService availability, [FromBody] ResetAnnouncementRequest request)
    {
        availability.ResetAnnouncement(request.Person);
        return Results.Ok(new { reset = true });
    }

    private static async Task<IResult> HandleGetPool(
        DateNightAvailabilityService availability, DateNightCycleService cycles, DateNightSummaryService summaries, IRadarrService radarr)
    {
        try
        {
            var movies = await availability.GetPoolMoviesAsync();
            var results = availability.GetAvailability();

            // Recoverable movies can include ones already graduated out of the pool
            // (watched — their date-night-pool tag is gone), so titleById has to cover
            // every Radarr movie, not just the currently pool-tagged ones, or a
            // recovered movie shows up as a bare "#412" with nothing to identify it by.
            Dictionary<int, string> allTitlesById;
            try
            {
                var allMovies = await radarr.GetAllMoviesAsync();
                allTitlesById = allMovies.OfType<JsonObject>()
                    .Where(m => (int?)m["id"] is int)
                    .ToDictionary(m => (int)m["id"]!, m => m["title"]?.ToString() ?? "");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DateNight] Could not fetch all movies for recoverable-title lookup");
                allTitlesById = new();
            }

            CycleAdminView? cycleView = null;
            CycleAdminView? testCycleView = null;
            try
            {
                cycleView = await BuildCycleAdminViewAsync(cycles, summaries, movies, allTitlesById, isTest: false);
            }
            catch (Exception ex)
            {
                // The pool table is the important part of this page; a cycle-side
                // hiccup shouldn't take it down too.
                Log.Warning(ex, "[DateNight] Could not build the cycle admin view");
            }
            try
            {
                testCycleView = await BuildCycleAdminViewAsync(cycles, summaries, movies, allTitlesById, isTest: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DateNight] Could not build the dry-run admin view");
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
                    DateNightViews.PosterUrl(m),
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
                summary, availability.GetScanStatus(), items, availability.GetAnnouncementStatus(), cycleView, testCycleView));
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "[DateNight] Pool fetch failed");
            return Results.Json(new { error = "Radarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Shared between the admin panel and the Mom/Dad cycle view — resolves
    /// this week's drawn movie ids against already-fetched pool metadata (no second
    /// Radarr call) and attaches the already-prepared AI pitch. This rendering path
    /// is deliberately cache-only; cycle issuance maintains a five-movie reserve.</summary>
    private static async Task<CycleAdminView> BuildCycleAdminViewAsync(
        DateNightCycleService cycles, DateNightSummaryService summaries, List<JsonObject> poolMovies,
        Dictionary<int, string> allTitlesById, bool isTest)
    {
        // The real cycle keeps its existing lazy issue/resolve-on-view behavior; the
        // test cycle is peeked only — see PeekTestCycle for why it must not auto-draw.
        var cycle = isTest ? cycles.PeekTestCycle() : await cycles.GetCurrentCycleAsync();
        var skip = cycles.GetSkip();
        var lists = cycles.GetLists(isTest);
        var coolingCutoff = DateTime.UtcNow - DateNightPolicy.CoolingOff;

        // Watched movies have already lost their date-night-pool tag (that's what
        // MarkWatchedAsync does), so they won't be in poolMovies — allTitlesById
        // covers every Radarr movie, tagged or not, so a recovered movie still shows
        // a real title instead of a bare "#412".
        var recoverable = DateNightViews.RecoverableMovies(lists, allTitlesById, coolingCutoff);
        var noCycleStatus = DateNightViews.NoCycleStatus(skip, isTest, DateTime.UtcNow);

        return new CycleAdminView(
            cycle?.CycleId,
            cycle?.Status ?? noCycleStatus,
            cycle?.DeadlineUtc,
            cycle?.ResolvedUtc,
            cycle is null ? [] : DateNightViews.ResolveCycleMovies(cycle, poolMovies, summaries.GetCachedSummary),
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
    private static readonly IResult FeatureDarkResult =
        Results.Json(new { error = "Date Night is not live yet." }, statusCode: StatusCodes.Status404NotFound);

    private static IResult? AudienceActionGate(DateNightCycleService cycles, string? person, bool isTest)
    {
        if (!DateNightPolicy.IsAudience(person)) return NotAudienceResult;
        return !isTest && !cycles.IsLive() ? FeatureDarkResult : null;
    }

    private const string ImpersonationHeader = "X-Date-Night-As";

    /// <summary>Reads the two inputs off the request; the rule itself is
    /// <see cref="DateNightPolicy.ResolveViewer"/>.</summary>
    private static (string? Person, bool IsTest) ResolveDateNightContext(HttpContext context) =>
        DateNightPolicy.ResolveViewer(
            LibraryHelpers.ResolveUserDisplayName(context),
            context.Request.Headers.TryGetValue(ImpersonationHeader, out var header)
                ? header.ToString()
                : null);

    private static async Task<IResult> HandleGetCycle(
        HttpContext context, DateNightAvailabilityService availability, DateNightCycleService cycles, DateNightSummaryService summaries)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (!DateNightPolicy.IsAudience(person))
            return Results.Ok(new CycleView(null, "None", null, [], new(), null, null, null, false, false, false));
        if (!isTest && !cycles.IsLive())
            return Results.Ok(new CycleView(null, "None", null, [], new(), null, null, null, false, false, false));

        // The test cycle is lazily drawn on first touch and never skip-gated — a dry
        // run always has something to show, unlike the real cycle which can be null.
        var cycle = isTest ? await cycles.GetCurrentTestCycleAsync() : await cycles.GetCurrentCycleAsync();
        if (cycle is null)
        {
            var skip = cycles.GetSkip();
            return Results.Ok(new CycleView(
                null, "None", null, [], new(), null, null, null, false, false, skip.SkipUntilUtc > DateTime.UtcNow));
        }

        var poolMovies = await availability.GetPoolMoviesAsync();
        var moviesView = DateNightViews.ResolveCycleMovies(cycle, poolMovies, summaries.GetCachedSummary);
        var myVotes = cycle.Votes.TryGetValue(person!, out var votes) ? votes : new Dictionary<int, string>();
        // Finished Watching removes the winner's Date Night pool tag, so it is no
        // longer present in poolMovies. Prefer the title persisted at conclusion;
        // otherwise ResolveCycleMovies would supply only its "Movie #id" placeholder.
        var resolvedTitle = cycle.Schedule?.ConclusionTitle;
        resolvedTitle ??= cycle.ResolvedMovieId is int rid
            ? moviesView.FirstOrDefault(m => m.MovieId == rid)?.Title
            : null;

        return Results.Ok(new CycleView(
            cycle.CycleId, cycle.Status, cycle.DeadlineUtc, moviesView, myVotes,
            cycle.ResolvedMovieId, resolvedTitle, cycle.Schedule,
            DateNightPolicy.IsFlyerOwedToday(person!, cycle, DateTime.UtcNow),
            DateNightPolicy.IsScheduleReminderOwedToday(person!, cycle, DateTime.UtcNow), false));
    }

    private static readonly IResult NotAudienceResult =
        Results.Json(new { error = "Only Mom and Dad can do this." }, statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IResult> HandleCastVote(HttpContext context, DateNightCycleService cycles, [FromBody] CastVoteRequest request)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (AudienceActionGate(cycles, person, isTest) is { } blocked) return blocked;

        try
        {
            await cycles.CastVoteAsync(person!, request.MovieId, request.Vote, isTest);
            return Results.Ok(new { voted = true });
        }
        // ArgumentException is deliberately not caught: InvalidOperationException is
        // a sibling type, not a base, so it falls through to
        // UseGlobalExceptionHandler — same 400, plus errorCode and details.
        catch (InvalidOperationException ex)
        {
            return ApiResponse.Conflict(ex.Message);
        }
    }

    private static IResult HandleSetSkip(HttpContext context, DateNightCycleService cycles, [FromBody] SetSkipRequest request)
    {
        // No test-cycle equivalent — skip is a real, household-wide concept.
        var (person, isTest) = ResolveDateNightContext(context);
        if (!DateNightPolicy.IsAudience(person)) return NotAudienceResult;
        if (isTest) return ApiResponse.Conflict("Skip is disabled in the isolated dry run.");
        if (!cycles.IsLive()) return FeatureDarkResult;

        if (request.Scope is not ("week" or "month"))
            return ApiResponse.BadRequest("scope must be \"week\" or \"month\".");

        cycles.SetSkip(person!, request.Scope);
        return Results.Ok(new { skipped = true });
    }

    private static async Task<IResult> HandleFlyerShown(HttpContext context, DateNightCycleService cycles)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (AudienceActionGate(cycles, person, isTest) is { } blocked) return blocked;

        await cycles.RecordFlyerShownAsync(person!, isTest);
        return Results.Ok(new { recorded = true });
    }

    private static async Task<IResult> HandleProposeSchedule(
        HttpContext context, DateNightCycleService cycles, [FromBody] ProposeScheduleRequest request)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (AudienceActionGate(cycles, person, isTest) is { } blocked) return blocked;

        try
        {
            await cycles.ProposeScheduleAsync(person!, request.Slots, isTest);
            return Results.Ok(new { proposed = true });
        }
        // ArgumentException is deliberately not caught: InvalidOperationException is
        // a sibling type, not a base, so it falls through to
        // UseGlobalExceptionHandler — same 400, plus errorCode and details.
        catch (InvalidOperationException ex)
        {
            return ApiResponse.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> HandleApproveSchedule(
        HttpContext context, DateNightCycleService cycles, [FromBody] ApproveScheduleRequest request)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (AudienceActionGate(cycles, person, isTest) is { } blocked) return blocked;

        try
        {
            await cycles.ApproveScheduleAsync(person!, request.Slot, isTest);
            return Results.Ok(new { locked = true });
        }
        catch (InvalidOperationException ex)
        {
            return ApiResponse.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> HandleCancelSchedule(HttpContext context, DateNightCycleService cycles)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (AudienceActionGate(cycles, person, isTest) is { } blocked) return blocked;

        try
        {
            await cycles.CancelScheduleAsync(person!, isTest);
            return Results.Ok(new { cancelled = true });
        }
        catch (InvalidOperationException ex)
        {
            return ApiResponse.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> HandleAcknowledgeSchedule(HttpContext context, DateNightCycleService cycles)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (AudienceActionGate(cycles, person, isTest) is { } blocked) return blocked;

        try
        {
            await cycles.AcknowledgeScheduleAsync(person!, isTest);
            return Results.Ok(new { acknowledged = true });
        }
        catch (InvalidOperationException ex)
        {
            return ApiResponse.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> HandleRetryDownload(HttpContext context, DateNightCycleService cycles)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (AudienceActionGate(cycles, person, isTest) is { } blocked) return blocked;

        try
        {
            var cycle = await cycles.RetryDownloadAsync(isTest);
            return Results.Ok(new
            {
                status = cycle.Schedule?.DownloadStatus,
                message = cycle.Schedule?.DownloadMessage
            });
        }
        catch (InvalidOperationException ex)
        {
            return ApiResponse.Conflict(ex.Message);
        }
    }

    /// <summary>Every household member's session polls this, so it must stay
    /// person-aware even though the real version doesn't need a person at all — only
    /// this way can a dry-run countdown ever surface for Paul without also leaking to
    /// a real Mom/Dad poll (which never carries the impersonation header).</summary>
    private static async Task<IResult> HandleShowtimeCheck(HttpContext context, DateNightCycleService cycles)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (!DateNightPolicy.IsAudience(person) || (!isTest && !cycles.IsLive()))
            return Results.Ok(new ShowtimeStatus(false, null, null));
        return Results.Ok(await cycles.GetShowtimeStatusAsync(isTest));
    }

    private static async Task<IResult> HandleStartShowtime(HttpContext context, DateNightCycleService cycles)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (AudienceActionGate(cycles, person, isTest) is { } blocked) return blocked;

        try
        {
            await cycles.StartShowtimeAsync(isTest);
            return Results.Ok(new { started = true });
        }
        catch (InvalidOperationException ex)
        {
            return ApiResponse.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> HandleMarkWatched(HttpContext context, DateNightCycleService cycles)
    {
        var (person, isTest) = ResolveDateNightContext(context);
        if (AudienceActionGate(cycles, person, isTest) is { } blocked) return blocked;

        var cycle = isTest ? await cycles.GetCurrentTestCycleAsync() : await cycles.GetCurrentCycleAsync();
        if (cycle?.Schedule?.Status != "Locked" || cycle.ResolvedMovieId is not int movieId)
            return ApiResponse.Conflict("Nothing locked in to mark watched.");
        if (cycle.Schedule.PlaybackStartedUtc is null)
            return ApiResponse.Conflict("Start the movie before marking it watched.");

        await cycles.MarkWatchedAsync(movieId, isTest);
        return Results.Ok(new { watched = true });
    }

    private static async Task<IResult> HandleAdminForceIssue(DateNightCycleService cycles)
    {
        var cycle = await cycles.ForceIssueAsync();
        return cycle is null
            ? ApiResponse.Conflict("No eligible movies to draw from.")
            : Results.Ok(new { issued = true, cycle.CycleId });
    }

    private static async Task<IResult> HandleAdminResolveNow(DateNightCycleService cycles)
    {
        var cycle = await cycles.ResolveNowAsync();
        return cycle is null
            ? ApiResponse.Conflict("No cycle to resolve.")
            : Results.Ok(new { cycle.Status, cycle.ResolvedMovieId });
    }

    private static IResult HandleAdminDiscard(DateNightCycleService cycles)
    {
        cycles.DiscardCycle();
        return Results.Ok(new { discarded = true });
    }

    private static async Task<IResult> HandleAdminRestore(DateNightCycleService cycles, int movieId)
    {
        await cycles.RestoreMovieAsync(movieId);
        return Results.Ok(new { restored = true });
    }

    private static IResult HandleAdminClearSkip(DateNightCycleService cycles)
    {
        cycles.ClearSkip();
        return Results.Ok(new { cleared = true });
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

    private static async Task<IResult> HandleAdminResetDryRun(DateNightCycleService cycles)
    {
        try
        {
            var cycle = await cycles.ResetDryRunAsync();
            return Results.Ok(new { reset = true, cycleId = cycle.CycleId, movieCount = cycle.MovieIds.Count });
        }
        catch (InvalidOperationException ex)
        {
            return ApiResponse.Conflict(ex.Message);
        }
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
            return ApiResponse.Conflict("A scan is already running.");

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

    /// <summary>Up to two of Radarr's own genres for the movie, joined for display —
    /// e.g. "Action · Comedy". Radarr returns this natively (this app's own household
    /// genre tagging deliberately lives under the separate "customGenres" key to avoid
    /// colliding with it — see MediaLibraryEndpoints).</summary>
}
