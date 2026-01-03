namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for tracking download usage with 18-hour rolling window
/// </summary>
public interface IDownloadTrackingService
{
    /// <summary>
    /// Records a successful download
    /// </summary>
    void RecordDownload(string md5, string userEmail);

    /// <summary>
    /// Gets the current download status (downloads left, total per period)
    /// </summary>
    (int DownloadsLeft, int DownloadsPerDay) GetDownloadStatus();

    /// <summary>
    /// Gets the total number of downloads allowed per 18-hour period
    /// </summary>
    int GetDownloadLimit();
}
