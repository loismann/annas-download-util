using System.Text.Json.Nodes;
using AnnasArchive.Core.Exceptions;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Shared plumbing for the *arr family (Sonarr/Radarr), whose REST v3 APIs are
/// deliberately near-identical: X-Api-Key auth from "{Name}:BaseUrl"/"{Name}:ApiKey"
/// config, JSON GET helpers, queue retrieval and cleanup (removeFromClient=true so
/// the download client actually cancels the job and deletes its data), force-grab
/// of a specific release, and dynamic root-folder/quality-profile resolution
/// (profile IDs aren't stable across installs, so they're never hardcoded).
/// Subclasses add only their media-specific endpoints.
/// </summary>
public abstract class ArrServiceBase
{
    protected readonly HttpClient Http;
    protected readonly string ServiceName;
    private readonly string _queueQuery;
    private readonly string _rootFolderHint;

    protected ArrServiceBase(
        HttpClient http,
        IConfiguration configuration,
        string serviceName,
        string queueIncludeParam,
        string rootFolderHint)
    {
        Http = http;
        ServiceName = serviceName;
        _queueQuery = $"/api/v3/queue?{queueIncludeParam}";
        _rootFolderHint = rootFolderHint;

        var baseUrl = configuration[$"{serviceName}:BaseUrl"];
        var apiKey = configuration[$"{serviceName}:ApiKey"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            Http.BaseAddress = new Uri(baseUrl);
        }
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            Http.DefaultRequestHeaders.Remove("X-Api-Key");
            Http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }
    }

    protected async Task<JsonArray> GetJsonArrayAsync(string path, CancellationToken ct)
    {
        var response = await Http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonArray ?? [];
    }

    protected async Task<JsonObject> GetJsonObjectAsync(string path, CancellationToken ct)
    {
        var response = await Http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonObject ?? [];
    }

    public Task<JsonObject> GetQueueAsync(CancellationToken ct = default) =>
        GetJsonObjectAsync(_queueQuery, ct);

    public async Task GrabReleaseAsync(JsonObject release, CancellationToken ct = default)
    {
        var response = await Http.PostAsJsonAsync("/api/v3/release", release, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Warning("[{Service}] Grab release failed ({StatusCode}): {Body}", ServiceName, response.StatusCode, body);
            throw new ExternalApiException(ServiceName, ArrErrorParsing.ExtractMessage(body), response.StatusCode, isTransient: false);
        }

        Log.Information("[{Service}] Manually grabbed release '{Title}'", ServiceName, release["title"]?.ToString());
    }

    /// <summary>Finds every queue entry whose <paramref name="idField"/> matches and
    /// removes it with removeFromClient=true — telling qBittorrent/SABnzbd to actually
    /// cancel the job and delete its data, not just clear the *arr's own queue view.
    /// Best-effort: a queue item that fails to remove (e.g. a race where it finished
    /// importing in the meantime) is logged, not thrown — it shouldn't block the
    /// delete that's about to happen anyway.</summary>
    protected async Task RemoveQueueItemsForAsync(string idField, int id, CancellationToken ct)
    {
        try
        {
            var queue = await GetQueueAsync(ct);
            var records = queue["records"] as JsonArray ?? [];
            foreach (var record in records)
            {
                if (record is not JsonObject obj) continue;
                if ((int?)obj[idField] != id) continue;

                var queueId = (int?)obj["id"];
                if (queueId is null) continue;

                var deleteResponse = await Http.DeleteAsync(
                    $"/api/v3/queue/{queueId}?removeFromClient=true&blocklist=false", ct);
                if (!deleteResponse.IsSuccessStatusCode)
                {
                    Log.Warning("[{Service}] Remove queue item {QueueId} for {IdField} {Id} failed: {StatusCode}",
                        ServiceName, queueId, idField, id, deleteResponse.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[{Service}] Failed to clear queue items for {IdField} {Id}: {Message}",
                ServiceName, idField, id, ex.Message);
        }
    }

    protected async Task<(string rootFolderPath, int qualityProfileId)> ResolveDefaultsAsync(CancellationToken ct)
    {
        var rootFolders = await GetJsonArrayAsync("/api/v3/rootfolder", ct);
        var rootFolderPath = rootFolders.Count > 0 ? rootFolders[0]?["path"]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(rootFolderPath))
            throw new InvalidOperationException(
                $"{ServiceName} has no root folder configured — add one (e.g. {_rootFolderHint}) in {ServiceName}'s Media Management settings first.");

        var profiles = await GetJsonArrayAsync("/api/v3/qualityprofile", ct);
        if (profiles.Count == 0)
            throw new InvalidOperationException($"{ServiceName} has no quality profile configured.");
        var qualityProfileId = (int)(profiles[0]?["id"] ?? 0);

        return (rootFolderPath, qualityProfileId);
    }
}
