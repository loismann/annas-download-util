using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AnnasArchive.API.Services;

public sealed record AudiobookRequestPreviewToken(
    string Token,
    string Asin,
    string Region,
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

    public AudiobookRequestPreviewToken CreatePreview(string ownerKey, string asin, string region)
    {
        PurgeExpired();
        var token = NewToken();
        var expiresAt = timeProvider.GetUtcNow().Add(Lifetime);
        _previews[token] = new StoredPreview(HashOwner(ownerKey), asin, region, expiresAt);
        return new AudiobookRequestPreviewToken(token, asin, region, expiresAt);
    }

    public AudiobookRequestPreviewToken? ConsumePreview(string ownerKey, string token)
    {
        if (!_previews.TryRemove(token, out var stored) ||
            stored.ExpiresAt <= timeProvider.GetUtcNow() ||
            !OwnerMatches(ownerKey, stored.OwnerHash))
            return null;

        return new AudiobookRequestPreviewToken(token, stored.Asin, stored.Region, stored.ExpiresAt);
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
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string HashOwner(string ownerKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)));

    private static bool OwnerMatches(string ownerKey, string expectedHash)
    {
        var actual = Convert.FromHexString(HashOwner(ownerKey));
        var expected = Convert.FromHexString(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private sealed record StoredPreview(
        string OwnerHash, string Asin, string Region, DateTimeOffset ExpiresAt);

    private sealed record StoredRelease(
        string OwnerHash,
        int ListenarrId,
        string Asin,
        string DownloadReference,
        DateTimeOffset ExpiresAt);
}
