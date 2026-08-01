namespace AnnasArchive.API.Infrastructure;

/// <summary>
/// Configuration options for Spotify API integration.
/// </summary>
public class SpotifyConfiguration
{
    public const string SectionName = "Spotify";

    /// <summary>
    /// Spotify API Client ID.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Spotify API Client Secret.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Exact callback URI allowlisted in the Spotify developer dashboard.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Public browser origin used after the callback, for example
    /// https://my-server.example. The callback returns to /spotifinator.
    /// </summary>
    public string FrontendBaseUrl { get; set; } = string.Empty;
}
