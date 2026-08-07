using System.Text.Json.Nodes;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>What one sweep changed, so the caller can log or assert on it.</summary>
public sealed record OwnershipBackfillResult(int Adopted, int Pruned)
{
    public static readonly OwnershipBackfillResult None = new(0, 0);
}

/// <summary>
/// Reconciles the ownership record against what the three libraries actually hold,
/// in both directions.
///
/// **Adopt:** a live item nobody owns gets <c>Ownership:DefaultMember</c>. Tagging
/// only ever happened inside this app's own add handlers, so anything added in
/// Radarr/Sonarr's own UI, by a maintenance script, or before the request flow
/// existed has no owner and — until the library grids stopped hiding untagged items
/// — was invisible. The ebook library already solved this exact problem with
/// <c>LibraryWatcher:AutoTagNewBooks</c>; this is the same idea for the other three.
///
/// **Prune:** a record whose item is gone is dropped. The TV and movie delete
/// handlers never cleaned up after themselves, which left 66 movie and 7 TV records
/// pointing at ids Radarr and Sonarr no longer have.
///
/// Runs once at startup and daily after that. Every upstream is treated as
/// optional: if Radarr is unreachable, movies are skipped entirely rather than
/// having their whole record pruned as "not found".
/// </summary>
public sealed class OwnershipBackfillService(
    IServiceProvider services,
    IConfiguration configuration) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private bool Enabled => configuration.GetValue("Ownership:BackfillEnabled", true);
    private string? DefaultMember => Constants.HouseholdOwners.ResolveName(
        configuration["Ownership:DefaultMember"] ?? "Paul");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            Log.Information("[Ownership] Backfill is disabled (Ownership:BackfillEnabled=false)");
            return;
        }

        // Let the arr services and Audiobookshelf finish coming up first — a sweep
        // that runs while they are still starting sees an empty library and would
        // prune every record in it.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunAsync(stoppingToken);
                if (result.Adopted > 0 || result.Pruned > 0)
                    Log.Information("[Ownership] Backfill adopted {Adopted} unowned item(s), pruned {Pruned} stale record(s)",
                        result.Adopted, result.Pruned);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log.Warning("[Ownership] Backfill sweep failed: {Message}", ex.Message);
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<OwnershipBackfillResult> RunAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var metadata = scope.ServiceProvider.GetRequiredService<IMediaMetadataService>();

        var adopted = 0;
        var pruned = 0;

        foreach (var (type, ids) in await LoadLiveIdsAsync(scope.ServiceProvider, ct))
        {
            var (a, p) = Reconcile(metadata, type, ids);
            adopted += a;
            pruned += p;
        }

        return new OwnershipBackfillResult(adopted, pruned);
    }

    /// <summary>
    /// The pure half, so the adopt/prune rules are testable without an arr instance.
    /// </summary>
    public (int Adopted, int Pruned) Reconcile(
        IMediaMetadataService metadata, string type, IReadOnlySet<string> liveIds)
    {
        var member = DefaultMember;
        var prefix = type + ":";
        var adopted = 0;
        var pruned = 0;

        // Read the whole record once. Every IMediaMetadataService call loads and
        // re-saves the entire document, so asking it per id would deserialize a
        // ~200 KB blob a thousand times to answer questions one snapshot answers.
        var existing = metadata.GetAll()
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key[prefix.Length..], entry => entry.Value, StringComparer.Ordinal);

        foreach (var (id, record) in existing)
        {
            if (liveIds.Contains(id)) continue;

            metadata.Delete(type, id);
            pruned++;
            Log.Information("[Ownership] Pruned {Type}:{Id} — no longer in the library (owners={Owners})",
                type, id, string.Join(",", record.Owners));
        }

        if (member is null) return (0, pruned);

        foreach (var id in liveIds)
        {
            if (existing.TryGetValue(id, out var record) && record.Owners.Count > 0) continue;
            if (MediaOwnership.Assign(metadata, type, id, member, "ownership backfill"))
                adopted++;
        }

        return (adopted, pruned);
    }

    /// <summary>One id set per media type. A type whose upstream cannot be reached is
    /// omitted rather than reported empty, so an outage never triggers a prune.</summary>
    private static async Task<List<(string Type, IReadOnlySet<string> Ids)>> LoadLiveIdsAsync(
        IServiceProvider scope, CancellationToken ct)
    {
        var sets = new List<(string, IReadOnlySet<string>)>();

        await AddAsync("movie", () => scope.GetRequiredService<IRadarrService>().GetAllMoviesAsync(ct));
        await AddAsync("tv", () => scope.GetRequiredService<ISonarrService>().GetAllSeriesAsync(ct));
        await AddAsync("audiobook", () => scope.GetRequiredService<IAudiobookshelfService>().GetLibraryItemsAsync(ct));

        return sets;

        async Task AddAsync(string type, Func<Task<JsonArray>> load)
        {
            try
            {
                var ids = (await load())
                    .OfType<JsonObject>()
                    .Select(item => item["id"]?.ToString())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToHashSet(StringComparer.Ordinal);

                // An upstream that answers with nothing is indistinguishable from one
                // that lost its library. Refusing to prune on empty is the safe read.
                if (ids.Count == 0)
                {
                    Log.Information("[Ownership] Skipping {Type} — the library reported no items", type);
                    return;
                }

                sets.Add((type, ids));
            }
            catch (Exception ex)
            {
                Log.Information("[Ownership] Skipping {Type} — library unavailable: {Message}", type, ex.Message);
            }
        }
    }
}
