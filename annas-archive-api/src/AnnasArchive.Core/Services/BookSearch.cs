#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Telemetry;
using Serilog;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Where a book search's results come from.
///
/// <para><b>Why this exists.</b> Anna's Archive moved its front end from
/// Cloudflare to DDoS-Guard on 2026-08-13. Its HTML pages now answer a scraper
/// with a redirect to <c>?check=1</c> and then a 403 carrying a JS challenge that
/// headless Chromium does not pass — verified by sitting a real browser on it for
/// thirty seconds, during which it collected the challenge cookies and never
/// advanced. Search was the only feature built on those pages. Their
/// <c>/dyn/*</c> JSON, including the member download API, was never touched.</para>
///
/// <para><b>Why LibGen can stand in.</b> An md5 is not an identifier Anna's
/// assigns; it is a hash of the file's bytes. Any index of the same files
/// therefore arrives at the same md5s with no coordination at all — which is
/// precisely why Anna's chose it, to fold Libgen, Z-Library and the Internet
/// Archive into one identifier space. So an md5 found on LibGen is a key Anna's
/// download API accepts, confirmed against live records before this was
/// written. Search moved; downloads did not have to.</para>
///
/// <para><b>Why there is no fallback to Anna's.</b> There was one, briefly, on
/// the theory that the feature would repair itself if DDoS-Guard ever went away.
/// That was wrong twice over: it cannot succeed today, so every search with no
/// LibGen match paid thirty seconds to be told nothing, and a fallback nobody
/// can rely on is worse than none — it makes a broken path look like a
/// supported one. If Anna's search becomes reachable again, restoring it should
/// be a decision somebody makes, not a timeout somebody waits out.</para>
///
/// <para>A thin class, and deliberately kept: it is the one place that answers
/// "where do results come from", and the next time that answer changes this is
/// the file to open. The endpoint above stays pointed at it either way.</para>
/// </summary>
public sealed class BookSearch
{
    private readonly LibGenService _libgen;

    public BookSearch(LibGenService libgen) => _libgen = libgen;

    /// <summary>
    /// Throws <see cref="System.Net.Http.HttpRequestException"/> when the source
    /// could not be reached at all, which the endpoint turns into a 503. An
    /// empty list means the search ran and matched nothing. Keeping those two
    /// apart is the whole reason this does not swallow anything.
    /// </summary>
    public async Task<IReadOnlyList<BookDto>> SearchAsync(
        string query, int limit, bool exact, int page)
    {
        var sw = Stopwatch.StartNew();

        var results = (await _libgen.SearchAsync(query, limit, exact, page)).ToList();

        Log.Information(
            "[BookSearch] {Count} result(s) for {Query} (page {Page})", results.Count, query, page);
        PerfLog.Record("BookSearch", sw.Elapsed.TotalMilliseconds, true, ("Results", results.Count));

        return results;
    }
}

#nullable restore
