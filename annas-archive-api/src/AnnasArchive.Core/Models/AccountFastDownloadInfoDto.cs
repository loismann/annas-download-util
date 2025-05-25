namespace AnnasArchive.Core.Models;

/// <summary>
/// Represents how many fast‐download slots you have remaining.
/// </summary>
public record AccountFastDownloadInfoDto(
    int DownloadsLeft,
    int DownloadsPerDay
);
