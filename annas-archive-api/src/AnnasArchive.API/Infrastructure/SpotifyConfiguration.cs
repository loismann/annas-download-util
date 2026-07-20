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
    /// Long-lived refresh token for OAuth.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;
}
