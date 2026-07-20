using System.Text.Json;
using System.Text.RegularExpressions;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for Anna's Archive download operations.
/// </summary>
public static class AnnaDownloadHelpers
{
    /// <summary>
    /// Downloads a book from Anna's Archive using member credentials.
    /// </summary>
    /// <param name="md5">The MD5 hash of the book</param>
    /// <param name="title">Optional title for the file name</param>
    /// <param name="anna">The Anna's Archive service</param>
    /// <param name="memberKey">The member key for authentication</param>
    /// <returns>
    /// - response: The HttpResponseMessage with the file content (caller must dispose)
    /// - fileName: Sanitized file name with appropriate extension
    /// - accountInfo: Account download info if available (null - tracking happens at endpoint level)
    /// - errorMessage: Error message if something went wrong (null on success)
    /// </returns>
    public static async Task<(HttpResponseMessage? response, string? fileName, AccountFastDownloadInfoDto? accountInfo, string? errorMessage)>
        DownloadBookFromAnnaArchiveAsync(
            string md5,
            string? title,
            AnnaArchiveService anna,
            string memberKey)
    {
        // Get download document from Anna's Archive
        JsonElement doc;
        try
        {
            doc = await anna.GetMemberDownloadDocumentAsync(md5, memberKey);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Rate limit"))
        {
            return (null, null, null, "⏱️ Rate limit exceeded. Please wait 30-60 seconds before trying again.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return (null, null, null, "⏱️ Rate limit exceeded. Please wait 30-60 seconds before trying again.");
        }

        // Extract download URL
        string? downloadUrl = null;
        if (doc.TryGetProperty("download_url", out var du))
            downloadUrl = du.ValueKind == JsonValueKind.String
                        ? du.GetString()
                        : du.EnumerateArray().FirstOrDefault().GetString();

        if (string.IsNullOrEmpty(downloadUrl))
            return (null, null, null, "No download URL found.");

        // Extract account info
        AccountFastDownloadInfoDto? acctInfo = null;
        if (doc.TryGetProperty("account_fast_download_info", out var ai) &&
            ai.ValueKind == JsonValueKind.Object)
            acctInfo = new AccountFastDownloadInfoDto(
                ai.GetProperty("downloads_left").GetInt32(),
                ai.GetProperty("downloads_per_day").GetInt32());

        // Download the file
        var resp = await anna.GetDownloadResponseWithFallbackAsync(
            downloadUrl,
            HttpCompletionOption.ResponseHeadersRead);
        if (resp == null || !resp.IsSuccessStatusCode)
            return (null, null, acctInfo, "Download failed.");

        // Sanitize title
        var rawTitle  = !string.IsNullOrWhiteSpace(title) ? title : md5;
        var safeTitle = Regex.Replace(rawTitle, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");

        // Determine file extension
        var ext = Path.GetExtension(new Uri(downloadUrl).AbsolutePath);
        if (string.IsNullOrEmpty(ext))
            ext = resp.Content.Headers.ContentType?.MediaType switch
            {
                "application/pdf"                 => ".pdf",
                "application/epub+zip"            => ".epub",
                "application/x-mobipocket-ebook"  => ".mobi",
                _                                 => ".bin"
            };

        var fileName = $"{safeTitle}{ext}";

        return (resp, fileName, acctInfo, null);
    }
}
