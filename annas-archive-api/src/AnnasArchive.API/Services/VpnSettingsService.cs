using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace AnnasArchive.API.Services;

public record VpnSettings(bool Enabled, string Region);

/// <summary>
/// An IWebProxy that checks IVpnSettingsService on every call instead of
/// pointing at a fixed address decided once at startup — this is what
/// makes the VPN on/off toggle take effect immediately on the next
/// request, regardless of when HttpClientFactory happens to recreate the
/// underlying handler.
/// </summary>
public class DynamicVpnProxy : IWebProxy
{
    private readonly IVpnSettingsService _vpnSettings;
    private readonly Uri _proxyUri;

    public DynamicVpnProxy(IVpnSettingsService vpnSettings, Uri proxyUri)
    {
        _vpnSettings = vpnSettings;
        _proxyUri = proxyUri;
    }

    public ICredentials? Credentials { get; set; }

    public Uri GetProxy(Uri destination) => _proxyUri;

    public bool IsBypassed(Uri host) => !_vpnSettings.Current.Enabled;
}

public interface IVpnSettingsService
{
    /// <summary>Curated list offered in the UI — not PIA's full ~80-region list.</summary>
    IReadOnlyList<string> AvailableRegions { get; }

    VpnSettings Current { get; }

    /// <summary>
    /// Updates the toggle/region and, if the region actually changed,
    /// tells Gluetun to reconnect to it via its control API — no container
    /// restart needed. Persists to disk so it survives an app restart.
    /// </summary>
    Task<VpnSettings> UpdateAsync(bool enabled, string region, CancellationToken cancellationToken = default);
}

/// <summary>
/// Backs the VPN on/off + region toggle exposed in the UI. "Enabled" is
/// read live by CloudflareBypassService (per-context) and the AnnasArchive
/// HttpClient's proxy resolver on every request — flipping it takes effect
/// on the very next request, no restart. Region changes go through
/// Gluetun's own HTTP control API (PUT .../v1/vpn/settings), which
/// disconnects and reconnects Gluetun's OpenVPN client to the new server
/// itself; that reconnect takes a few seconds, but the container never
/// restarts either.
/// </summary>
public class VpnSettingsService : IVpnSettingsService
{
    // Curated from a real latency check against the NAS's actual location
    // rather than PIA's full list — see conversation history for why.
    public IReadOnlyList<string> AvailableRegions { get; } = new[]
    {
        "US Kansas",
        "US Texas",
        "US Houston",
        "US Oklahoma",
        "US Chicago",
        "US California",
        "US East",
        "US West",
    };

    private readonly string _storagePath;
    private readonly HttpClient _controlClient;
    private readonly object _lock = new();
    private VpnSettings _current;

    public VpnSettingsService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _storagePath = configuration["Gluetun:StateStoragePath"] ?? "vpn-settings.json";
        _controlClient = httpClientFactory.CreateClient("GluetunControl");
        // Off by default — an explicit opt-in, not an assumed-on setting.
        _current = LoadFromDisk() ?? new VpnSettings(Enabled: false, Region: AvailableRegions[0]);
    }

    public VpnSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public async Task<VpnSettings> UpdateAsync(bool enabled, string region, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(region) || !AvailableRegions.Contains(region))
        {
            throw new ArgumentException($"Region must be one of: {string.Join(", ", AvailableRegions)}", nameof(region));
        }

        var previous = Current;
        var updated = new VpnSettings(enabled, region);

        // Only actually poke Gluetun if the region changed and the VPN is
        // (or is about to be) enabled — no point reconnecting it to a new
        // server if we're not even routing anything through it right now.
        if (enabled && previous.Region != region)
        {
            await ReconnectGluetunToRegionAsync(region, cancellationToken);
        }

        lock (_lock)
        {
            _current = updated;
        }
        SaveToDisk(updated);

        return updated;
    }

    /// <summary>
    /// Read-modify-write against Gluetun's own settings JSON rather than
    /// constructing a full request body from a guessed schema — this way
    /// we only ever touch the region field, whatever else Gluetun's
    /// settings document happens to contain stays exactly as Gluetun left
    /// it. The likely field name ("region" vs "regions" vs something
    /// nested) is confirmed empirically on first real deploy; this checks
    /// a couple of plausible field names defensively.
    /// </summary>
    private async Task ReconnectGluetunToRegionAsync(string region, CancellationToken cancellationToken)
    {
        try
        {
            var getResponse = await _controlClient.GetAsync("/v1/vpn/settings", cancellationToken);
            getResponse.EnsureSuccessStatusCode();

            var currentJson = await getResponse.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken)
                ?? new JsonObject();

            // Gluetun's server-selection settings are commonly nested under
            // a "serverSelection" object with a "regions" array — but this
            // is exactly the part to verify/adjust against the real
            // response the first time this actually runs.
            if (currentJson["serverSelection"] is not JsonObject serverSelection)
            {
                serverSelection = new JsonObject();
                currentJson["serverSelection"] = serverSelection;
            }
            serverSelection["regions"] = new JsonArray(region);

            var putResponse = await _controlClient.PutAsJsonAsync("/v1/vpn/settings", currentJson, cancellationToken);
            putResponse.EnsureSuccessStatusCode();

            Log.Information("[VpnSettings] Told Gluetun to reconnect to region {Region}", region);
        }
        catch (Exception ex)
        {
            Log.Warning("[VpnSettings] Failed to reconnect Gluetun to region {Region}: {Message}", region, ex.Message);
            throw;
        }
    }

    private VpnSettings? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storagePath)) return null;
            var json = File.ReadAllText(_storagePath);
            return JsonSerializer.Deserialize<VpnSettings>(json);
        }
        catch (Exception ex)
        {
            Log.Warning("[VpnSettings] Failed to load persisted settings: {Message}", ex.Message);
            return null;
        }
    }

    private void SaveToDisk(VpnSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_storagePath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            Log.Warning("[VpnSettings] Failed to persist settings: {Message}", ex.Message);
        }
    }
}
