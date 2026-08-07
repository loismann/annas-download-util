using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AnnasArchive.API.Helpers;

namespace AnnasArchive.API.Services;

/// <summary>AutoSearch is decided by the server during preview and carried in
/// the token, so the browser can see what will happen without being able to
/// upgrade a review-only request into an automatic grab.</summary>
public sealed record AudiobookRequestPreviewToken(
    string Token,
    string Asin,
    string Region,
    bool AutoSearch,
    DateTimeOffset ExpiresAt,
    /// <summary>The indexers had nothing for this book when the preview was built.
    /// Carried on the token, not just shown in the UI, so confirming still has to
    /// acknowledge it — the browser cannot quietly drop the warning.</summary>
    bool NoReleasesFound = false);

public sealed record AudiobookSeriesPreviewToken(
    string Token,
    string SeriesAsin,
    string Region,
    IReadOnlyList<string> Asins,
    DateTimeOffset ExpiresAt);

public sealed record ListenarrReleaseSelectionToken(
    string Token,
    int ListenarrId,
    string Asin,
    string DownloadReference,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Short-lived, owner-scoped capabilities for reviewed mutations. Tokens are
/// lost on restart by design; the browser must rerun preview/release search.
/// </summary>
public sealed class AudiobookRequestTokenStore(TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, StoredPreview> _previews = new();
    private readonly ConcurrentDictionary<string, StoredRelease> _releases = new();
    private readonly ConcurrentDictionary<string, StoredSeries> _series = new();

    public AudiobookRequestPreviewToken CreatePreview(
        string ownerKey, string asin, string region, bool autoSearch, bool noReleasesFound = false)
    {
        PurgeExpired();
        var token = NewToken();
        var expiresAt = timeProvider.GetUtcNow().Add(Lifetime);
        _previews[token] = new StoredPreview(
            HashOwner(ownerKey), asin, region, autoSearch, expiresAt, noReleasesFound);
        return new AudiobookRequestPreviewToken(
            token, asin, region, autoSearch, expiresAt, noReleasesFound);
    }

    public AudiobookRequestPreviewToken? ConsumePreview(string ownerKey, string token)
    {
        if (!_previews.TryRemove(token, out var stored) ||
            stored.ExpiresAt <= timeProvider.GetUtcNow() ||
            !OwnerMatches(ownerKey, stored.OwnerHash))
            return null;

        return new AudiobookRequestPreviewToken(
            token, stored.Asin, stored.Region, stored.AutoSearch, stored.ExpiresAt, stored.NoReleasesFound);
    }

    /// <summary>Holds the exact set of editions the server classified as
    /// requestable. Confirmation may only ever be a subset of this set, so a
    /// browser cannot append an ASIN the preview never offered.</summary>
    public AudiobookSeriesPreviewToken CreateSeries(
        string ownerKey, string seriesAsin, string region, IReadOnlyList<string> asins)
    {
        PurgeExpired();
        var token = NewToken();
        var expiresAt = timeProvider.GetUtcNow().Add(Lifetime);
        _series[token] = new StoredSeries(HashOwner(ownerKey), seriesAsin, region, asins, expiresAt);
        return new AudiobookSeriesPreviewToken(token, seriesAsin, region, asins, expiresAt);
    }

    public AudiobookSeriesPreviewToken? ConsumeSeries(string ownerKey, string token)
    {
        if (!_series.TryRemove(token, out var stored) ||
            stored.ExpiresAt <= timeProvider.GetUtcNow() ||
            !OwnerMatches(ownerKey, stored.OwnerHash))
            return null;

        return new AudiobookSeriesPreviewToken(
            token, stored.SeriesAsin, stored.Region, stored.Asins, stored.ExpiresAt);
    }

    public ListenarrReleaseSelectionToken CreateRelease(
        string ownerKey, int listenarrId, string asin, string downloadReference)
    {
        PurgeExpired();
        var token = NewToken();
        var expiresAt = timeProvider.GetUtcNow().Add(Lifetime);
        _releases[token] = new StoredRelease(
            HashOwner(ownerKey), listenarrId, asin, downloadReference, expiresAt);
        return new ListenarrReleaseSelectionToken(
            token, listenarrId, asin, downloadReference, expiresAt);
    }

    public ListenarrReleaseSelectionToken? ConsumeRelease(
        string ownerKey, int listenarrId, string token)
    {
        if (!_releases.TryRemove(token, out var stored) ||
            stored.ExpiresAt <= timeProvider.GetUtcNow() ||
            stored.ListenarrId != listenarrId ||
            !OwnerMatches(ownerKey, stored.OwnerHash))
            return null;

        return new ListenarrReleaseSelectionToken(
            token, stored.ListenarrId, stored.Asin, stored.DownloadReference, stored.ExpiresAt);
    }

    public static string StableUserId(string ownerKey) => HashOwner(ownerKey);

    private void PurgeExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in _previews.Where(entry => entry.Value.ExpiresAt <= now))
            _previews.TryRemove(entry.Key, out _);
        foreach (var entry in _releases.Where(entry => entry.Value.ExpiresAt <= now))
            _releases.TryRemove(entry.Key, out _);
        foreach (var entry in _series.Where(entry => entry.Value.ExpiresAt <= now))
            _series.TryRemove(entry.Key, out _);
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string HashOwner(string ownerKey) =>
        HouseholdIdentity.OwnerHash(ownerKey);

    private static bool OwnerMatches(string ownerKey, string expectedHash)
    {
        var actual = Convert.FromHexString(HashOwner(ownerKey));
        var expected = Convert.FromHexString(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private sealed record StoredPreview(
        string OwnerHash, string Asin, string Region, bool AutoSearch, DateTimeOffset ExpiresAt,
        bool NoReleasesFound);

    private sealed record StoredSeries(
        string OwnerHash,
        string SeriesAsin,
        string Region,
        IReadOnlyList<string> Asins,
        DateTimeOffset ExpiresAt);

    private sealed record StoredRelease(
        string OwnerHash,
        int ListenarrId,
        string Asin,
        string DownloadReference,
        DateTimeOffset ExpiresAt);
}
