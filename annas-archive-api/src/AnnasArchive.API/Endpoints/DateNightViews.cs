using System.Text.Json.Nodes;
using AnnasArchive.API.Services;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Turning Radarr's JSON and the week's stored state into the shapes the Date Night
/// screens render.
///
/// This is where the display decisions live: what a movie is called when Radarr has
/// forgotten it, which of two poster URLs a browser can actually load, and why a movie
/// is sitting in the recoverable list. All of it used to be private statics inside
/// <see cref="DateNightEndpoints"/>, reachable only through an HTTP request.
///
/// Nothing here touches Radarr, the database or the cycle service. The one dependency
/// that is not data — the cached AI summary — arrives as a <c>Func</c>, so these can be
/// exercised without standing up <see cref="DateNightSummaryService"/>.
/// </summary>
public static class DateNightViews
{
    /// <summary>
    /// The first two genres, joined. Two because the card has one line for it, and a
    /// movie tagged with six genres would otherwise push the layout apart.
    /// </summary>
    public static string? GenreLine(JsonObject movie)
    {
        var genres = (movie["genres"] as JsonArray)?
            .Select(g => g?.ToString())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Take(2)
            .ToList();
        return genres is { Count: > 0 } ? string.Join(" · ", genres) : null;
    }

    /// <summary>
    /// Radarr serves posters from its own host behind its API key, so the local
    /// <c>url</c> renders as a broken image in a browser. The remote (TMDB) one is
    /// preferred for that reason; the local one remains as a fallback because some
    /// entries carry no remote URL at all.
    /// </summary>
    public static string? PosterUrl(JsonObject movie)
    {
        var poster = (movie["images"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(i => string.Equals(
                i["coverType"]?.ToString(), "poster", StringComparison.OrdinalIgnoreCase));

        return poster?["remoteUrl"]?.ToString() ?? poster?["url"]?.ToString();
    }

    /// <summary>
    /// This week's drawn movies, in draw order, joined to whatever Radarr still knows
    /// about them and to how Mom and Dad voted.
    ///
    /// A drawn movie can vanish from the pool between the draw and the render — someone
    /// deletes it in Radarr, or it loses its tag. The row is still produced, titled
    /// <c>#412</c>, because dropping it would silently shorten the ballot and leave a
    /// week that can never reach "everyone voted".
    /// </summary>
    public static List<CycleMovieView> ResolveCycleMovies(
        WeeklyCycle cycle,
        List<JsonObject> poolMovies,
        Func<int, string?> cachedSummary)
    {
        var byId = poolMovies
            .Where(m => (int?)m["id"] is int)
            .ToDictionary(m => (int)m["id"]!);

        cycle.Votes.TryGetValue("Mom", out var momVotes);
        cycle.Votes.TryGetValue("Dad", out var dadVotes);

        var result = new List<CycleMovieView>();
        foreach (var id in cycle.MovieIds)
        {
            byId.TryGetValue(id, out var movie);

            string? momVote = null, dadVote = null;
            momVotes?.TryGetValue(id, out momVote);
            dadVotes?.TryGetValue(id, out dadVote);

            result.Add(new CycleMovieView(
                id,
                movie?["title"]?.ToString() ?? $"#{id}",
                movie is null ? null : PosterUrl(movie),
                movie is null ? null : (int?)movie["tmdbId"],
                movie?["overview"]?.ToString(),
                cachedSummary(id),
                movie is null ? null : (int?)movie["year"],
                movie is null ? null : GenreLine(movie),
                movie is not null && ((bool?)movie["hasFile"] ?? false),
                movie is not null && ((bool?)movie["monitored"] ?? false),
                momVote,
                dadVote));
        }
        return result;
    }

    /// <summary>
    /// Every movie currently held out of the draw, newest first, with the reason.
    ///
    /// Titles come from <paramref name="allTitlesById"/> rather than the pool: a watched
    /// movie has already lost its date-night-pool tag, so it is not in the pool at all,
    /// and looking there would show a bare <c>#412</c> for exactly the entries someone
    /// is most likely to want to recover.
    /// </summary>
    public static List<RecoverableMovie> RecoverableMovies(
        IReadOnlyDictionary<int, MovieListEntry> lists,
        IReadOnlyDictionary<int, string> allTitlesById,
        DateTime coolingCutoffUtc)
    {
        return lists
            .Where(kv => IsHeldOut(kv.Value, coolingCutoffUtc))
            .Select(kv =>
            {
                var (id, entry) = (kv.Key, kv.Value);
                var title = allTitlesById.TryGetValue(id, out var t) ? t : $"#{id}";

                if (entry.NeverShowAgain)
                    return new RecoverableMovie(id, title, "Never show again",
                        entry.NeverShowAgainUtc ?? DateTime.UtcNow);
                if (entry.Watched)
                    return new RecoverableMovie(id, title, "Watched — retired from pool",
                        entry.WatchedUtc ?? DateTime.UtcNow);
                return new RecoverableMovie(id, title, "Disagreed — cooling off",
                    entry.LastDisagreedUtc!.Value);
            })
            .OrderByDescending(r => r.Since)
            .ToList();
    }

    /// <summary>
    /// Whether a movie is out of the draw right now. Never-show and watched are
    /// permanent; a disagreement only counts while it is still inside the cooling-off
    /// window, which is what lets a movie come back on its own.
    /// </summary>
    public static bool IsHeldOut(MovieListEntry entry, DateTime coolingCutoffUtc) =>
        entry.NeverShowAgain
        || entry.Watched
        || (entry.LastDisagreedUtc is DateTime d && d > coolingCutoffUtc);

    /// <summary>
    /// The status shown when there is no cycle. Skip only ever applies to the real
    /// cycle — the dry run has no calendar week to skip, and showing "Skipped" there
    /// would suggest the test run was blocked when it simply has not been drawn.
    /// </summary>
    public static string NoCycleStatus(SkipState skip, bool isTest, DateTime utcNow) =>
        !isTest && skip.SkipUntilUtc > utcNow ? "Skipped" : "None";
}
