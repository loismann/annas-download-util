using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services;

public interface IListenarrService
{
    bool IsEnabled { get; }
    bool IsConfigured { get; }
    Task<ListenarrIntegrationStatus> GetIntegrationStatusAsync(CancellationToken ct = default);
    Task<ListenarrReady> GetReadyAsync(CancellationToken ct = default);
    Task<ListenarrServiceHealth> GetHealthAsync(CancellationToken ct = default);
    Task<ListenarrSystemInfo> GetSystemInfoAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ListenarrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ListenarrQualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ListenarrIndexer>> GetIndexersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ListenarrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ListenarrLibraryItem>> GetLibraryAsync(CancellationToken ct = default);
    Task<ListenarrAudibleSearchResponse> SearchAudibleAsync(
        string query, string region, string? language = null, CancellationToken ct = default);
    Task<ListenarrAudibleBook?> GetAudibleMetadataAsync(
        string asin, string region, CancellationToken ct = default);
    Task<ListenarrLibraryItem?> GetLibraryByAsinAsync(string asin, CancellationToken ct = default);
    Task<ListenarrQualityProfile> GetDefaultQualityProfileAsync(CancellationToken ct = default);
    Task<ListenarrLibraryAddResponse> AddToLibraryAsync(
        ListenarrAddToLibraryRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ListenarrIndexerSearchResult>> SearchIndexersAsync(
        string query, CancellationToken ct = default);
    Task<ListenarrSendDownloadResponse> SendToDownloadClientAsync(
        string downloadReference, int audiobookId, CancellationToken ct = default);
    Task<IReadOnlyList<ListenarrDownload>> GetDownloadsAsync(CancellationToken ct = default);
    Task RemoveDownloadFromQueueAsync(string downloadId, CancellationToken ct = default);
    Task RetryImportAsync(string downloadId, CancellationToken ct = default);
}

/// <summary>
/// Small, typed adapter for Listenarr's versioned v1 API. This intentionally
/// does not inherit ArrServiceBase: Sonarr/Radarr's v3 models and lifecycle are
/// not compatible with Listenarr's audiobook contract.
/// </summary>
public sealed class ListenarrService : IListenarrService
{
    private readonly HttpClient _http;
    public bool IsEnabled { get; }
    public bool IsConfigured { get; }

    public ListenarrService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        IsEnabled = configuration.GetValue<bool>("Listenarr:Enabled", false);

        var baseUrl = configuration["Listenarr:BaseUrl"];
        var apiKey = configuration["Listenarr:ApiKey"];
        IsConfigured = Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl)
            && !string.IsNullOrWhiteSpace(apiKey);

        if (parsedBaseUrl is not null)
            _http.BaseAddress = parsedBaseUrl;

        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ListenarrIntegrationStatus> GetIntegrationStatusAsync(CancellationToken ct = default)
    {
        if (_http.BaseAddress is null)
            return UnreachableStatus("Listenarr base URL is not configured.");

        var readyTask = GetReadyAsync(ct);
        var healthTask = GetHealthAsync(ct);
        var infoTask = GetSystemInfoAsync(ct);
        var rootsTask = GetRootFoldersAsync(ct);
        var profilesTask = GetQualityProfilesAsync(ct);
        var indexersTask = GetIndexersAsync(ct);
        var clientsTask = GetDownloadClientsAsync(ct);
        var libraryTask = GetLibraryAsync(ct);

        await Task.WhenAll(readyTask, healthTask, infoTask, rootsTask, profilesTask,
            indexersTask, clientsTask, libraryTask);

        var ready = await readyTask;
        var health = await healthTask;
        var info = await infoTask;
        var roots = await rootsTask;
        var profiles = await profilesTask;
        var indexers = await indexersTask;
        var clients = await clientsTask;
        var library = await libraryTask;

        var failures = new List<string>();
        if (!IsConfigured) failures.Add("API key is not configured.");
        if (!ready.IsReady) failures.Add("Listenarr is not ready.");
        if (!ready.DatabaseConnected) failures.Add("Listenarr database is unavailable.");
        if (!ready.MigrationsCurrent) failures.Add("Listenarr database migrations are not current.");
        if (!string.Equals(health.Status, "healthy", StringComparison.OrdinalIgnoreCase))
            failures.Add("Listenarr reports unhealthy dependencies.");
        if (roots.Count == 0) failures.Add("No audiobook root folder is configured.");
        if (profiles.Count == 0) failures.Add("No quality profile is configured.");
        if (!indexers.Any(indexer => indexer.IsEnabled)) failures.Add("No enabled indexer is configured.");
        if (!clients.Any(client => client.IsEnabled)) failures.Add("No enabled download client is configured.");

        return new ListenarrIntegrationStatus(
            IsEnabled,
            IsConfigured,
            Reachable: true,
            ready.IsReady,
            ready.Status,
            ready.DatabaseConnected,
            ready.MigrationsCurrent,
            health.Status,
            info.Version ?? health.Version,
            roots.Count,
            profiles.Count,
            indexers.Count(indexer => indexer.IsEnabled),
            clients.Count(client => client.IsEnabled),
            library.Count,
            ReadOnlyGatePassed: failures.Count == 0,
            failures);
    }

    public Task<ListenarrReady> GetReadyAsync(CancellationToken ct = default) =>
        GetRequiredAsync<ListenarrReady>("/api/v1/system/ready", ct);

    public Task<ListenarrServiceHealth> GetHealthAsync(CancellationToken ct = default) =>
        GetRequiredAsync<ListenarrServiceHealth>("/api/v1/system/health", ct);

    public Task<ListenarrSystemInfo> GetSystemInfoAsync(CancellationToken ct = default) =>
        GetRequiredAsync<ListenarrSystemInfo>("/api/v1/system/info", ct);

    public async Task<IReadOnlyList<ListenarrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default) =>
        await GetListAsync<ListenarrRootFolder>("/api/v1/rootfolders", ct);

    public async Task<IReadOnlyList<ListenarrQualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default) =>
        await GetListAsync<ListenarrQualityProfile>("/api/v1/qualityprofile", ct);

    public async Task<IReadOnlyList<ListenarrIndexer>> GetIndexersAsync(CancellationToken ct = default) =>
        await GetListAsync<ListenarrIndexer>("/api/v1/indexers", ct);

    public async Task<IReadOnlyList<ListenarrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default) =>
        await GetListAsync<ListenarrDownloadClient>("/api/v1/download-clients", ct);

    public async Task<IReadOnlyList<ListenarrLibraryItem>> GetLibraryAsync(CancellationToken ct = default) =>
        await GetListAsync<ListenarrLibraryItem>("/api/v1/library", ct);

    public Task<ListenarrAudibleSearchResponse> SearchAudibleAsync(
        string query, string region, string? language = null, CancellationToken ct = default)
    {
        var path = $"/api/v1/search/audible?query={Uri.EscapeDataString(query)}&region={Uri.EscapeDataString(region)}";
        if (!string.IsNullOrWhiteSpace(language))
            path += $"&language={Uri.EscapeDataString(language)}";

        return GetRequiredAsync<ListenarrAudibleSearchResponse>(path, ct);
    }

    public async Task<ListenarrAudibleBook?> GetAudibleMetadataAsync(
        string asin, string region, CancellationToken ct = default)
    {
        var path = $"/api/v1/metadata/audible/{Uri.EscapeDataString(asin)}" +
            $"?region={Uri.EscapeDataString(region)}&cache=true";
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ListenarrAudibleBook>(cancellationToken: ct);
    }

    public async Task<ListenarrLibraryItem?> GetLibraryByAsinAsync(
        string asin, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(
            $"/api/v1/library/by-asin/{Uri.EscapeDataString(asin)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ListenarrLibraryItem>(cancellationToken: ct);
    }

    public Task<ListenarrQualityProfile> GetDefaultQualityProfileAsync(CancellationToken ct = default) =>
        GetRequiredAsync<ListenarrQualityProfile>("/api/v1/qualityprofile/default", ct);

    public async Task<ListenarrLibraryAddResponse> AddToLibraryAsync(
        ListenarrAddToLibraryRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("/api/v1/library/add", request, ct);
        if (response.StatusCode != HttpStatusCode.Conflict)
            response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<ListenarrLibraryAddEnvelope>(
            cancellationToken: ct)
            ?? throw new InvalidOperationException("Listenarr returned an empty library-add response.");
        return new ListenarrLibraryAddResponse(
            envelope.Message,
            envelope.Audiobook,
            response.StatusCode == HttpStatusCode.Conflict);
    }

    public async Task<IReadOnlyList<ListenarrIndexerSearchResult>> SearchIndexersAsync(
        string query, CancellationToken ct = default) =>
        await GetListAsync<ListenarrIndexerSearchResult>(
            $"/api/v1/search/indexers?query={Uri.EscapeDataString(query)}&sortBy=Seeders&sortDirection=Descending&isAutomaticSearch=false",
            ct);

    public async Task<ListenarrSendDownloadResponse> SendToDownloadClientAsync(
        string downloadReference, int audiobookId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("/api/v1/download/send", new
        {
            downloadReference,
            audiobookId
        }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ListenarrSendDownloadResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Listenarr returned an empty download-send response.");
    }

    public async Task<IReadOnlyList<ListenarrDownload>> GetDownloadsAsync(CancellationToken ct = default) =>
        await GetListAsync<ListenarrDownload>("/api/v1/downloads", ct);

    public async Task RemoveDownloadFromQueueAsync(string downloadId, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(
            $"/api/v1/download/queue/{Uri.EscapeDataString(downloadId)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RetryImportAsync(string downloadId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(
            $"/api/v1/downloads/{Uri.EscapeDataString(downloadId)}/retry-import", null, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"Listenarr returned an empty response for {path}.");
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken: ct) ?? [];
    }

    private ListenarrIntegrationStatus UnreachableStatus(string failure) => new(
        IsEnabled,
        IsConfigured,
        Reachable: false,
        Ready: false,
        ReadyStatus: null,
        DatabaseConnected: false,
        MigrationsCurrent: false,
        ServiceHealth: null,
        Version: null,
        RootFolderCount: 0,
        QualityProfileCount: 0,
        EnabledIndexerCount: 0,
        EnabledDownloadClientCount: 0,
        LibraryItemCount: 0,
        ReadOnlyGatePassed: false,
        GateFailures: [failure]);

    private sealed record ListenarrLibraryAddEnvelope(
        string? Message,
        ListenarrLibraryItem? Audiobook);
}
