#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnnasArchive.Core.Services;

/// <summary>
/// The fast-download half of Anna's Archive: turning an md5 into one or more
/// file URLs, and fetching the file behind one of them.
///
/// Split out of <see cref="AnnasArchiveService"/>, which is otherwise about
/// searching and covers — the two halves shared nothing but the transport,
/// which is now injected instead. Callers that only download take this and
/// never see the search service at all.
/// </summary>
public class AnnasArchiveDownloads
{
    private readonly AnnasArchiveTransport _transport;

    public AnnasArchiveDownloads(AnnasArchiveTransport transport)
    {
        _transport = transport;
    }

    public async Task<List<string>> GetDownloadLinksAsync(string md5)
    {
        var links = await _transport.GetJsonAsync<List<string>>(
            $"/dyn/api/fast_download.json?md5={Uri.EscapeDataString(md5)}");
        return links ?? new List<string>();
    }

    public async Task<List<string>> GetMemberDownloadLinksAsync(string md5, string key)
    {
        var url = $"/dyn/api/fast_download.json?md5={Uri.EscapeDataString(md5)}"
                + $"&key={Uri.EscapeDataString(key)}"
                + "&path_index=0&domain_index=0";

        var doc = await _transport.GetJsonElementAsync(url);
        if (doc.ValueKind != JsonValueKind.Object) return new List<string>();

        var results = new List<string>();
        if (doc.TryGetProperty("download_url", out var token))
        {
            if (token.ValueKind == JsonValueKind.Array)
            {
                results.AddRange(token.EnumerateArray()
                                      .Select(e => e.GetString()!)
                                      .Where(s => !string.IsNullOrEmpty(s)));
            }
            else if (token.ValueKind == JsonValueKind.String)
            {
                var s = token.GetString();
                if (!string.IsNullOrEmpty(s)) results.Add(s);
            }
        }

        return results;
    }

    public async Task<JsonElement> GetMemberDownloadDocumentAsync(string md5, string key)
    {
        var url = $"/dyn/api/fast_download.json"
                + $"?md5={Uri.EscapeDataString(md5)}"
                + $"&key={Uri.EscapeDataString(key)}"
                + "&path_index=0&domain_index=0";

        try
        {
            var doc = await _transport.GetJsonElementAsync(url);
            if (doc.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("Failed to fetch download document.");
            return doc;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("Rate limit exceeded. Please wait before trying again.", ex);
        }
    }

    public Task<HttpResponseMessage?> GetDownloadResponseWithFallbackAsync(
        string downloadUrl,
        HttpCompletionOption completionOption)
        => _transport.GetAbsoluteAsync(downloadUrl, completionOption);
}
#nullable restore
