using System.Text.Json;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>Body for POST .../run.</summary>
public record RunAudiobookEnrichmentRequest(bool DryRun, int? Limit);

/// <summary>
/// Admin-only manual trigger/status endpoints for AudiobookEnrichmentService
/// — bypasses its internal weekly timer for on-demand dry-run/subset/full
/// runs, and surfaces progress without needing to SSH in and read sidecar
/// files by hand.
/// </summary>
public static class AudiobookEnrichmentEndpoints
{
    private const string SidecarFileName = ".audiobook-enrichment.json";

    public static WebApplication MapAudiobookEnrichmentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/audiobook-enrichment/run", HandleRunScan)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        app.MapGet("/api/admin/audiobook-enrichment/status", HandleGetStatus)
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleRunScan(
        [FromBody] RunAudiobookEnrichmentRequest request,
        AudiobookEnrichmentService enrichmentService)
    {
        var options = new AudiobookScanOptions(request.DryRun, request.Limit);

        // Bounded runs (a Limit is set) are fast enough to await directly.
        // Unbounded full runs could take hours (see the plan's backlog time
        // estimate) — kick those off in the background and let the caller
        // poll /status instead of holding the HTTP request open.
        if (request.Limit is not null)
        {
            var summary = enrichmentService.RunScanAsync(options, CancellationToken.None).GetAwaiter().GetResult();
            return Results.Ok(summary);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await enrichmentService.RunScanAsync(options, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warning("[AudiobookEnrichment] Background-triggered full scan failed: {Message}", ex.Message);
            }
        });

        return Results.Accepted(value: new { message = "Full scan started in the background — poll /api/admin/audiobook-enrichment/status for progress." });
    }

    /// <summary>Cheap progress view — walks the audiobooks tree reading only the small
    /// sidecar JSON files (no external API calls), tallying counts by renameStatus/matchStatus
    /// plus a clean top-level pending/processed/failed summary (the three-state lifecycle:
    /// pending = will be retried, processed = renamed or quarantined as a duplicate, failed =
    /// permanently gave up after MaxMatchAttempts, needs a human).</summary>
    private static IResult HandleGetStatus()
    {
        var root = ResolveAudiobooksRoot();
        if (!Directory.Exists(root))
            return Results.Ok(new { root, exists = false });

        var renameStatusCounts = new Dictionary<string, int>();
        var matchStatusCounts = new Dictionary<string, int>();
        var total = 0;
        var pending = 0;
        var processed = 0;
        var failed = 0;

        foreach (var sidecarPath in EnumerateSidecarsSafe(root))
        {
            total++;
            try
            {
                var json = File.ReadAllText(sidecarPath);
                using var doc = JsonDocument.Parse(json);
                var renameStatus = doc.RootElement.TryGetProperty("renameStatus", out var rs) ? rs.GetString() ?? "unknown" : "unknown";
                var matchStatus = doc.RootElement.TryGetProperty("matchStatus", out var ms) ? ms.GetString() ?? "unknown" : "unknown";

                renameStatusCounts[renameStatus] = renameStatusCounts.GetValueOrDefault(renameStatus) + 1;
                matchStatusCounts[matchStatus] = matchStatusCounts.GetValueOrDefault(matchStatus) + 1;

                if (matchStatus == "failed")
                    failed++;
                else if (renameStatus is "renamed" or "quarantined")
                    processed++;
                else
                    pending++;
            }
            catch
            {
                // Corrupt/partial sidecar — count it but don't let one bad file break the whole status view.
                renameStatusCounts["unreadable"] = renameStatusCounts.GetValueOrDefault("unreadable") + 1;
                pending++; // safest bucket for something we couldn't classify — worth a human glance, not silently dropped
            }
        }

        return Results.Ok(new
        {
            root,
            exists = true,
            totalTracked = total,
            summary = new { pending, processed, failed },
            byRenameStatus = renameStatusCounts,
            byMatchStatus = matchStatusCounts
        });
    }

    /// <summary>Walks every sidecar in the tree — both the normal folder-level
    /// ".audiobook-enrichment.json" and the per-file ".{fileName}.enrichment.json" markers a
    /// collection-split book unit gets (see AudiobookEnrichmentService.GetSidecarPath) — so
    /// status coverage includes files still sitting inside a partially-drained collection
    /// folder, not just whole-folder book units.</summary>
    private static IEnumerable<string> EnumerateSidecarsSafe(string dir)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch
        {
            yield break;
        }

        var subDirs = new List<string>();
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (!name.Equals("@eaDir", StringComparison.OrdinalIgnoreCase))
                    subDirs.Add(entry);
                continue;
            }

            if (name == SidecarFileName || (name.StartsWith('.') && name.EndsWith(".enrichment.json")))
                yield return entry;
        }

        foreach (var sub in subDirs)
        {
            foreach (var found in EnumerateSidecarsSafe(sub))
                yield return found;
        }
    }

    private static string ResolveAudiobooksRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("AUDIOBOOKS_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot;

        const string dockerDefault = "/audiobooks";
        return Directory.Exists(dockerDefault) ? dockerDefault : Path.Combine(AppContext.BaseDirectory, "audiobooks");
    }
}
