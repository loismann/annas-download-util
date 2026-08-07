using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnnasArchive.API.Infrastructure;
using Microsoft.Extensions.Options;
using Serilog;

namespace AnnasArchive.API.Services.PhotoPrint;

/// <summary>One printable photo in the household library.</summary>
/// <param name="TakenAt">
/// Immich's <c>localDateTime</c> — the wall-clock moment the photo was taken, not
/// the upload time. This is the field the UI sorts and filters "last week" on;
/// <c>createdAt</c> would group a Takeout import as if every photo were taken the
/// day it was imported.
/// </param>
public sealed record ImmichAsset(
    string Id,
    string FileName,
    string MimeType,
    DateTimeOffset TakenAt,
    int Width,
    int Height,
    bool IsFavorite);

public sealed record ImmichAssetPage(IReadOnlyList<ImmichAsset> Items, int Total, int? NextPage);

public sealed record ImmichSearchQuery
{
    public DateTimeOffset? TakenAfter { get; init; }
    public DateTimeOffset? TakenBefore { get; init; }
    public bool FavoritesOnly { get; init; }
    public int Page { get; init; } = 1;
    public int Size { get; init; } = 100;
}

public interface IImmichService
{
    bool IsConfigured { get; }
    Task<bool> IsReachableAsync(CancellationToken ct = default);
    Task<ImmichAssetPage> SearchAsync(ImmichSearchQuery query, CancellationToken ct = default);

    /// <summary>Full-resolution original bytes — what actually gets printed.</summary>
    Task<Stream> OpenOriginalAsync(string assetId, CancellationToken ct = default);

    /// <summary>Downscaled preview for the picker grid.</summary>
    Task<Stream> OpenThumbnailAsync(string assetId, CancellationToken ct = default);

    /// <summary>
    /// Bytes the photo library occupies, for the admin storage panel. Immich
    /// already tracks this, so no directory scan is needed — and the app
    /// container has no mount for Immich's storage anyway.
    /// </summary>
    Task<long> GetLibrarySizeBytesAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads the household photo library from Immich's REST API. Deliberately not a
/// filesystem walk of Immich's upload location: that layout is Immich's private
/// implementation detail (it changes between versions and depends on the storage
/// template), while this API surface is versioned and stable.
///
/// Endpoint shapes here were verified against the live instance rather than taken
/// from docs — see DOCS/ASSERTIONS_AND_ASSUMPTIONS.md.
/// </summary>
public sealed class ImmichService : IImmichService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PhotoPrintConfiguration _config;

    public ImmichService(IHttpClientFactory httpClientFactory, IOptions<PhotoPrintConfiguration> config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config.Value;
    }

    public bool IsConfigured => _config.Immich.IsConfigured;

    private HttpClient CreateClient() => _httpClientFactory.CreateClient("Immich");

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return false;

        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync("api/server/ping", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warning("[PhotoPrint] Immich is unreachable: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<ImmichAssetPage> SearchAsync(ImmichSearchQuery query, CancellationToken ct = default)
    {
        RequireConfigured();

        // Only stills, and nothing the user has thrown away or archived — a
        // trashed photo appearing in a print picker would be a nasty surprise.
        var body = new Dictionary<string, object?>
        {
            ["type"] = "IMAGE",
            ["isTrashed"] = false,
            ["isArchived"] = false,
            ["withExif"] = true,
            ["order"] = "desc",
            ["page"] = Math.Max(1, query.Page),
            ["size"] = Math.Clamp(query.Size, 1, 1000)
        };

        if (query.TakenAfter is { } after)
            body["takenAfter"] = after.UtcDateTime.ToString("O");
        if (query.TakenBefore is { } before)
            body["takenBefore"] = before.UtcDateTime.ToString("O");
        if (query.FavoritesOnly)
            body["isFavorite"] = true;

        using var client = CreateClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("api/search/metadata", content, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, JsonOptions, ct);
        var assets = payload?.Assets;
        if (assets?.Items is null)
            return new ImmichAssetPage([], 0, null);

        var items = assets.Items
            .Where(item => item.Id is { Length: > 0 })
            .Select(Map)
            .ToList();

        // Immich returns nextPage as a *string* page number, not a cursor.
        var nextPage = int.TryParse(assets.NextPage, out var parsed) ? parsed : (int?)null;
        return new ImmichAssetPage(items, assets.Total, nextPage);
    }

    public async Task<long> GetLibrarySizeBytesAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return 0;

        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync("api/server/statistics", ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<ServerStatistics>(stream, JsonOptions, ct);
            return payload?.Usage ?? 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The storage panel degrades to 0 for this row rather than failing —
            // one unreachable service must not blank the whole panel.
            Log.Warning("[PhotoPrint] Could not read Immich storage usage: {Message}", ex.Message);
            return 0;
        }
    }

    public Task<Stream> OpenOriginalAsync(string assetId, CancellationToken ct = default) =>
        OpenAsync($"api/assets/{Uri.EscapeDataString(assetId)}/original", assetId, ct);

    public Task<Stream> OpenThumbnailAsync(string assetId, CancellationToken ct = default) =>
        OpenAsync($"api/assets/{Uri.EscapeDataString(assetId)}/thumbnail?size=preview", assetId, ct);

    private async Task<Stream> OpenAsync(string path, string assetId, CancellationToken ct)
    {
        RequireConfigured();
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        var client = CreateClient();
        try
        {
            var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                throw new ImmichAssetNotFoundException(assetId);
            }

            response.EnsureSuccessStatusCode();

            // The caller owns the stream; it must outlive this method, so the
            // response and client are disposed by the wrapper rather than here.
            var body = await response.Content.ReadAsStreamAsync(ct);
            return new HttpOwnedStream(body, response, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void RequireConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Immich is not configured — set PhotoPrint:Immich:BaseUrl and :ApiKey.");
    }

    private static ImmichAsset Map(SearchAsset asset) => new(
        Id: asset.Id!,
        FileName: asset.OriginalFileName ?? $"{asset.Id}.jpg",
        MimeType: asset.OriginalMimeType ?? "image/jpeg",
        // localDateTime is the capture moment; fall back to file time, then upload
        // time, so an asset with incomplete metadata still sorts somewhere sane.
        TakenAt: asset.LocalDateTime ?? asset.FileCreatedAt ?? asset.CreatedAt ?? DateTimeOffset.MinValue,
        Width: asset.Width ?? 0,
        Height: asset.Height ?? 0,
        IsFavorite: asset.IsFavorite ?? false);

    // ─── Wire shapes (verified against the live instance) ────────────────

    private sealed class SearchResponse
    {
        public SearchAssets? Assets { get; set; }
    }

    /// <summary>GET /api/server/statistics — "usage" is total bytes stored.</summary>
    private sealed class ServerStatistics
    {
        public long Usage { get; set; }
        public int Photos { get; set; }
        public int Videos { get; set; }
    }

    private sealed class SearchAssets
    {
        public int Total { get; set; }
        public int Count { get; set; }
        public string? NextPage { get; set; }
        public List<SearchAsset>? Items { get; set; }
    }

    private sealed class SearchAsset
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? OriginalFileName { get; set; }
        public string? OriginalMimeType { get; set; }
        public DateTimeOffset? LocalDateTime { get; set; }
        public DateTimeOffset? FileCreatedAt { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public bool? IsFavorite { get; set; }
    }
}

public sealed class ImmichAssetNotFoundException(string assetId)
    : Exception($"Immich has no asset '{assetId}'.");

/// <summary>
/// A response stream that also owns the HTTP response and client behind it.
/// Streaming a multi-megabyte original means the caller reads long after the
/// method returns, so disposal has to ride along with the stream.
/// </summary>
internal sealed class HttpOwnedStream(Stream inner, HttpResponseMessage response, HttpClient client) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        inner.ReadAsync(buffer, ct);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        inner.ReadAsync(buffer, offset, count, ct);

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
            client.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        response.Dispose();
        client.Dispose();
        GC.SuppressFinalize(this);
    }
}
