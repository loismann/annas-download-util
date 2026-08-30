using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnnasArchive.Core.Helpers;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Why a download did not produce a file.
///
/// The reason travels with the failure because the caller has to turn it into an
/// HTTP status, and the three cases are not the same answer: a rate limit is the
/// user's own quota and will clear on its own, while a missing URL or a refused
/// transfer is Anna's Archive failing us. Returning one status for both would put
/// "wait a minute and retry" and "the mirror is down" in the same bucket — which
/// is what a bare <c>success = false</c> did.
/// </summary>
public enum AnnaDownloadFailure
{
    /// <summary>No failure — a file was produced.</summary>
    None = 0,

    /// <summary>Anna's Archive refused on rate-limit grounds. Maps to 429.</summary>
    RateLimited,

    /// <summary>
    /// Anna's Archive has no record of this md5. Maps to 404 — the book cannot be
    /// fetched, but nothing is broken and retrying will not help.
    ///
    /// <para>Kept apart from <see cref="Unavailable"/> because the two want opposite
    /// responses: "Anna's is down" is worth retrying and worth alerting on, while
    /// "Anna's never had this file" is the routine consequence of searching LibGen
    /// and downloading from Anna's. It is also the condition a LibGen download
    /// fallback has to trigger on, and it cannot trigger on a bucket that also
    /// contains a dead mirror.</para>
    /// </summary>
    NotOnAnnasArchive,

    /// <summary>Upstream gave no download URL, or the transfer failed. Maps to 502.</summary>
    Unavailable
}

/// <summary>Which catalogue actually produced the file.</summary>
public enum BookSource
{
    /// <summary>Anna's Archive, against the member allowance.</summary>
    AnnasArchive,

    /// <summary>
    /// LibGen, after Anna's had no record of the md5. Free: LibGen has no
    /// membership and no daily quota, so a download from here must not be
    /// recorded against the Anna's counter.
    /// </summary>
    LibGen
}

/// <summary>
/// The outcome of trying to fetch a book, from whichever source produced it.
///
/// <para>A record rather than the six-part tuple this grew out of, because the
/// source is not optional detail: it decides whether the download is charged to
/// the Anna's allowance, and a caller that forgets to read a tuple element
/// silently mis-bills.</para>
/// </summary>
public sealed record BookDownload(
    HttpResponseMessage? Response,
    string? FileName,
    AccountFastDownloadInfoDto? AccountInfo,
    string? ErrorMessage,
    AnnaDownloadFailure Failure,
    BookSource Source)
{
    /// <summary>True when a file was produced.</summary>
    public bool Succeeded => ErrorMessage is null && Response is not null && FileName is not null;

    /// <summary>
    /// Whether this download consumed an Anna's fast-download slot. Only Anna's
    /// downloads do; the LibGen fallback is the whole reason this is asked.
    /// </summary>
    public bool CountsAgainstAnnasQuota => Succeeded && Source == BookSource.AnnasArchive;
}

/// <summary>
/// Helper methods for Anna's Archive download operations.
/// </summary>
public static class AnnaDownloadHelpers
{
    /// <summary>
    /// The HTTP status a <see cref="AnnaDownloadFailure"/> should be reported as.
    /// One mapping, used by all three download endpoints, so they cannot disagree
    /// about what a rate limit looks like from the outside.
    /// </summary>
    public static int StatusCodeFor(AnnaDownloadFailure failure) => failure switch
    {
        AnnaDownloadFailure.RateLimited => StatusCodes.Status429TooManyRequests,
        AnnaDownloadFailure.NotOnAnnasArchive => StatusCodes.Status404NotFound,
        AnnaDownloadFailure.Unavailable => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status200OK
    };

    /// <summary>
    /// The current quota counters, in the shape every download response carries.
    /// </summary>
    public static AccountFastDownloadInfoDto CurrentCounters(IDownloadTrackingService downloadTracking)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        return new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
    }

    /// <summary>
    /// The outcome of <see cref="DownloadForSendAsync"/>: either the book, or the
    /// response to send instead — never both, never neither.
    ///
    /// This used to be a 4-tuple of nullables. The invariant held at runtime (the
    /// helper null-checks before returning success) but nothing expressed it to the
    /// compiler, so both call sites dereferenced <c>resp</c> and <c>fileName</c>
    /// after testing only the error, and each earned two nullable warnings. Those
    /// four were the entire warning count of the build, which is worth keeping at
    /// zero — a warning nobody expects is one somebody reads.
    ///
    /// <see cref="TryGetBook"/> carries the invariant in the type system via
    /// <see cref="NotNullWhenAttribute"/>, so the same call sites now compile clean
    /// without a null-forgiving operator anywhere.
    /// </summary>
    public sealed class DownloadForSendResult
    {
        private readonly HttpResponseMessage? _response;
        private readonly string? _fileName;
        private readonly AccountFastDownloadInfoDto? _accountInfo;
        private readonly IResult? _error;

        private DownloadForSendResult(
            HttpResponseMessage? response,
            string? fileName,
            AccountFastDownloadInfoDto? accountInfo,
            IResult? error,
            BookSource source)
        {
            _response = response;
            _fileName = fileName;
            _accountInfo = accountInfo;
            _error = error;
            Source = source;
        }

        /// <summary>
        /// Which catalogue served the file. The send endpoints read this to decide
        /// whether to charge the Anna's allowance — a LibGen download must not.
        /// </summary>
        public BookSource Source { get; }

        /// <summary>True when this download used one of the reader's Anna's slots.</summary>
        public bool CountsAgainstAnnasQuota => _error is null && Source == BookSource.AnnasArchive;

        public static DownloadForSendResult Downloaded(
            HttpResponseMessage response,
            string fileName,
            AccountFastDownloadInfoDto? accountInfo,
            BookSource source) =>
            new(response, fileName, accountInfo, null, source);

        public static DownloadForSendResult Failed(IResult error) =>
            new(null, null, null, error, BookSource.AnnasArchive);

        /// <summary>
        /// True with the book when the download succeeded; false with the finished
        /// response to return when it did not. <paramref name="accountInfo"/> stays
        /// nullable on purpose — Anna's Archive does not always report the counters,
        /// so "succeeded but we don't know the quota" is a real state.
        /// </summary>
        public bool TryGetBook(
            [NotNullWhen(true)] out HttpResponseMessage? response,
            [NotNullWhen(true)] out string? fileName,
            out AccountFastDownloadInfoDto? accountInfo,
            [NotNullWhen(false)] out IResult? error)
        {
            response = _response;
            fileName = _fileName;
            accountInfo = _accountInfo;
            error = _error;
            return _error is null;
        }
    }

    /// <summary>
    /// Fetches the book, or the response to send instead.
    ///
    /// The two "send to device" endpoints had this same 27-line prologue —
    /// member key, download, two failure branches — copied between them.
    ///
    /// Both failure bodies still carry the quota counters despite being non-2xx:
    /// a failed attempt can have consumed a slot, and the counter has to stay
    /// truthful. The browser reads it off the error.
    /// </summary>
    public static async Task<DownloadForSendResult> DownloadForSendAsync(
        string md5,
        string? title,
        AnnasArchiveDownloads anna,
        LibGenService libgen,
        IConfiguration cfg,
        IDownloadTrackingService downloadTracking)
    {
        var memberKey = cfg["Anna:MemberKey"]
            ?? throw new InvalidOperationException("Missing Anna:MemberKey.");

        var download = await DownloadBookAsync(md5, title, anna, libgen, memberKey);

        if (download.ErrorMessage != null)
        {
            return DownloadForSendResult.Failed(Results.Json(
                new { success = false, message = download.ErrorMessage, accountFastInfo = CurrentCounters(downloadTracking) },
                statusCode: StatusCodeFor(download.Failure)));
        }

        if (!download.Succeeded)
        {
            return DownloadForSendResult.Failed(Results.Json(
                new { success = false, message = "Failed to download book.", accountFastInfo = CurrentCounters(downloadTracking) },
                statusCode: StatusCodes.Status502BadGateway));
        }

        return DownloadForSendResult.Downloaded(
            download.Response!, download.FileName!, download.AccountInfo, download.Source);
    }

    /// <summary>
    /// Fetches a book, trying Anna's Archive first and falling back to LibGen when
    /// Anna's has no record of the md5.
    ///
    /// <para><b>Why the fallback exists.</b> Search moved to LibGen when Anna's went
    /// behind DDoS-Guard, but downloads stayed on Anna's member API. That works
    /// because an md5 is a hash of the file's bytes, so both catalogues arrive at
    /// the same id for the same file — but only for files <i>both</i> hold. LibGen
    /// indexes books Anna's does not, and for those the send buttons simply failed.
    /// Searching one catalogue and downloading from another leaves exactly this gap,
    /// and this closes it.</para>
    ///
    /// <para>Only <see cref="AnnaDownloadFailure.NotOnAnnasArchive"/> falls through.
    /// A rate limit means the reader's own allowance is spent and LibGen would
    /// quietly launder that into a free download; an unreachable mirror says nothing
    /// about whether Anna's has the book, so retrying Anna's later is the honest
    /// answer. Falling back on either would hide a condition worth reporting.</para>
    ///
    /// <para>The fallback costs no membership quota: Anna's charges a slot for a
    /// download it serves, and it served nothing here.</para>
    /// </summary>
    public static async Task<BookDownload> DownloadBookAsync(
        string md5,
        string? title,
        AnnasArchiveDownloads anna,
        LibGenService libgen,
        string memberKey)
    {
        var (resp, fileName, acctInfo, errorMessage, failure) =
            await DownloadBookFromAnnasArchiveAsync(md5, title, anna, memberKey);

        if (failure != AnnaDownloadFailure.NotOnAnnasArchive)
            return new BookDownload(resp, fileName, acctInfo, errorMessage, failure, BookSource.AnnasArchive);

        Log.Information(
            "[download] Anna's has no record of {Md5}; trying LibGen", md5);

        var fromLibGen = await DownloadBookFromLibGenAsync(md5, title, libgen);
        if (fromLibGen.Succeeded)
        {
            Log.Information("[download] LibGen served {Md5} that Anna's did not have", md5);
            return fromLibGen;
        }

        // LibGen could not produce it either. Report Anna's answer, not LibGen's:
        // "not in either catalogue" is what the reader needs to know, and Anna's
        // message already says the book was found somewhere Anna's has not indexed.
        Log.Information("[download] LibGen could not serve {Md5} either", md5);
        return new BookDownload(null, null, acctInfo, errorMessage, failure, BookSource.AnnasArchive);
    }

    /// <summary>
    /// Fetches a book directly from LibGen. No credentials and no quota — LibGen
    /// serves the file to anyone who can find the link.
    /// </summary>
    public static async Task<BookDownload> DownloadBookFromLibGenAsync(
        string md5,
        string? title,
        LibGenService libgen)
    {
        string? downloadUrl;
        try
        {
            downloadUrl = await libgen.GetDownloadUrlAsync(md5);
        }
        catch (Exception ex)
        {
            // The url lookup scrapes a page, so it can fail in more ways than an
            // API call. This is a fallback: a failure here must report the original
            // problem, never replace it with a scraping error.
            Log.Warning(ex, "[download] LibGen download-url lookup failed for {Md5}", md5);
            return NoLibGenFile;
        }

        if (string.IsNullOrEmpty(downloadUrl))
            return NoLibGenFile;

        var resp = await libgen.GetDownloadResponseAsync(md5);
        if (resp is null || !resp.IsSuccessStatusCode)
        {
            resp?.Dispose();
            return NoLibGenFile;
        }

        var (_, _, fileName) = BookFileNaming.For(title, md5, downloadUrl, resp);

        // AccountInfo stays null: there is no LibGen allowance to report, and
        // borrowing Anna's counters here would show the reader a number that has
        // nothing to do with what just happened.
        return new BookDownload(resp, fileName, null, null, AnnaDownloadFailure.None, BookSource.LibGen);
    }

    private static BookDownload NoLibGenFile =>
        new(null, null, null, "LibGen could not serve this book.",
            AnnaDownloadFailure.NotOnAnnasArchive, BookSource.LibGen);

    /// <summary>
    /// Downloads a book from Anna's Archive using member credentials.
    /// </summary>
    /// <param name="md5">The MD5 hash of the book</param>
    /// <param name="title">Optional title for the file name</param>
    /// <param name="anna">The Anna's Archive download client</param>
    /// <param name="memberKey">The member key for authentication</param>
    /// <returns>
    /// - response: The HttpResponseMessage with the file content (caller must dispose)
    /// - fileName: Sanitized file name with appropriate extension
    /// - accountInfo: Account download info if available (null - tracking happens at endpoint level)
    /// - errorMessage: Error message if something went wrong (null on success)
    /// - failure: Why it failed, so the caller can pick an honest status code
    /// </returns>
    public static async Task<(HttpResponseMessage? response, string? fileName, AccountFastDownloadInfoDto? accountInfo, string? errorMessage, AnnaDownloadFailure failure)>
        DownloadBookFromAnnasArchiveAsync(
            string md5,
            string? title,
            AnnasArchiveDownloads anna,
            string memberKey)
    {
        // Get download document from Anna's Archive
        JsonElement doc;
        try
        {
            doc = await anna.GetMemberDownloadDocumentAsync(md5, memberKey);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return (null, null, null, "⏱️ Rate limit exceeded. Please wait 30-60 seconds before trying again.", AnnaDownloadFailure.RateLimited);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode == System.Net.HttpStatusCode.BadRequest ||
            ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Anna's answers 400 for an md5 it holds no record of. Routine since
            // search moved to LibGen and downloads stayed here: LibGen indexes files
            // Anna's has not. Before this it escaped as an unhandled exception and
            // the reader got a 500, which reads as "the button is broken" rather
            // than "this book is not on Anna's" — a different problem entirely.
            return (null, null, null,
                "This book is not available from Anna's Archive. It was found in another catalogue that Anna's has not indexed.",
                AnnaDownloadFailure.NotOnAnnasArchive);
        }
        catch (HttpRequestException ex)
        {
            // Every mirror refused. Distinct from the case above: nothing is known
            // about whether the book exists, only that Anna's could not be asked.
            return (null, null, null,
                $"Anna's Archive could not be reached ({(int?)ex.StatusCode ?? 0}). Please try again shortly.",
                AnnaDownloadFailure.Unavailable);
        }

        // Extract download URL
        string? downloadUrl = null;
        if (doc.TryGetProperty("download_url", out var du))
            downloadUrl = du.ValueKind == JsonValueKind.String
                        ? du.GetString()
                        : du.EnumerateArray().FirstOrDefault().GetString();

        if (string.IsNullOrEmpty(downloadUrl))
            return (null, null, null, "No download URL found.", AnnaDownloadFailure.Unavailable);

        // Extract account info
        AccountFastDownloadInfoDto? acctInfo = null;
        if (doc.TryGetProperty("account_fast_download_info", out var ai) &&
            ai.ValueKind == JsonValueKind.Object)
            acctInfo = new AccountFastDownloadInfoDto(
                ai.GetProperty("downloads_left").GetInt32(),
                ai.GetProperty("downloads_per_day").GetInt32());

        // Download the file
        var resp = await anna.GetDownloadResponseWithFallbackAsync(
            downloadUrl,
            HttpCompletionOption.ResponseHeadersRead);
        if (resp == null || !resp.IsSuccessStatusCode)
            return (null, null, acctInfo, "Download failed.", AnnaDownloadFailure.Unavailable);

        var (_, _, fileName) = BookFileNaming.For(title, md5, downloadUrl, resp);

        return (resp, fileName, acctInfo, null, AnnaDownloadFailure.None);
    }
}
