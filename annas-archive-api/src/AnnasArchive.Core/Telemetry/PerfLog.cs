using System;
using System.Linq;
using System.Threading.Tasks;
using Serilog;

namespace AnnasArchive.Core.Telemetry;

/// <summary>
/// Emits a single structured log event per timed operation, with duration as
/// a real numeric field (not just text) — this is what lets Seq build charts
/// (e.g. p95 duration over time, grouped by Operation) instead of just
/// full-text log search. Keep Operation names stable and dot-namespaced
/// (e.g. "Playwright.FetchHtml") so dashboards built against one name keep
/// working as instrumentation is added elsewhere.
/// </summary>
public static class PerfLog
{
    public static void Record(string operation, double durationMs, bool success, params (string Key, object? Value)[] extra)
    {
        var logger = Log.ForContext("Operation", operation)
                         .ForContext("DurationMs", durationMs)
                         .ForContext("Success", success);

        foreach (var (key, value) in extra)
        {
            logger = logger.ForContext(key, value);
        }

        logger.Information("[Perf] {Operation} {Outcome} in {DurationMs:0}ms", operation, success ? "succeeded" : "failed", durationMs);
    }

    /// <summary>
    /// Times an async operation end-to-end and records it, regardless of
    /// whether it throws. Rethrows the original exception unchanged.
    /// </summary>
    public static async Task<T> TimeAsync<T>(string operation, Func<Task<T>> action, params (string Key, object? Value)[] extra)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await action();
            Record(operation, sw.Elapsed.TotalMilliseconds, true, extra);
            return result;
        }
        catch (Exception ex)
        {
            var withError = extra.Append(("Error", (object?)ex.Message)).ToArray();
            Record(operation, sw.Elapsed.TotalMilliseconds, false, withError);
            throw;
        }
    }
}
