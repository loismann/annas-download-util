using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for looking up book covers from OpenLibrary and Google Books APIs.
/// </summary>
public static class CoverLookupHelpers
{
    // Relaxed cover size validation - accept any reasonable image size
    private const int MinCoverWidth = 100;
    private const int MinCoverHeight = 100;

    /// <summary>
    /// Fetches a book cover URL from Google Books API.
    /// </summary>
    public static async Task<string?> FetchGoogleBooksCoverAsync(string title, string? author, IHttpClientFactory httpFactory)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(3);

            var candidateTitles = BuildCoverTitleCandidates(title);
            foreach (var candidate in candidateTitles)
            {
                var titleQuery = Uri.EscapeDataString(candidate);
                var authorQuery = string.IsNullOrWhiteSpace(author) ? "" : $"+inauthor:{Uri.EscapeDataString(author.Trim())}";
                var url = $"https://www.googleapis.com/books/v1/volumes?q=intitle:{titleQuery}{authorQuery}&maxResults=3";

                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("volumeInfo", out var volumeInfo)) continue;
                    if (!volumeInfo.TryGetProperty("imageLinks", out var imageLinks)) continue;

                    string? urlValue = null;
                    if (imageLinks.TryGetProperty("thumbnail", out var thumb))
                        urlValue = thumb.GetString();
                    else if (imageLinks.TryGetProperty("smallThumbnail", out var smallThumb))
                        urlValue = smallThumb.GetString();

                    if (string.IsNullOrWhiteSpace(urlValue)) continue;
                    return urlValue.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                return await FetchGoogleBooksCoverAsync(title, null, httpFactory);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Fetches a book cover URL from OpenLibrary API.
    /// </summary>
    public static async Task<string?> FetchOpenLibraryCoverAsync(string title, string? author, IHttpClientFactory httpFactory)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(3);

            var candidateTitles = BuildCoverTitleCandidates(title);
            foreach (var candidate in candidateTitles)
            {
                var titleQuery = Uri.EscapeDataString(candidate);
                var authorQuery = string.IsNullOrWhiteSpace(author) ? "" : $"&author={Uri.EscapeDataString(author.Trim())}";
                var url = $"https://openlibrary.org/search.json?title={titleQuery}{authorQuery}&limit=10";

                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                    continue;

                int bestScore = -1;
                int bestCoverId = -1;

                foreach (var item in docs.EnumerateArray())
                {
                    if (!item.TryGetProperty("cover_i", out var coverProp) || coverProp.ValueKind != JsonValueKind.Number)
                        continue;

                    var coverId = coverProp.GetInt32();
                    var editionCount = item.TryGetProperty("edition_count", out var editionProp) && editionProp.ValueKind == JsonValueKind.Number
                        ? editionProp.GetInt32()
                        : 0;

                    if (editionCount > bestScore)
                    {
                        bestScore = editionCount;
                        bestCoverId = coverId;
                    }
                }

                if (bestCoverId > 0)
                    return $"https://covers.openlibrary.org/b/id/{bestCoverId}-L.jpg";
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                return await FetchOpenLibraryCoverAsync(title, null, httpFactory);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Fetches multiple cover candidates from OpenLibrary API.
    /// </summary>
    public static async Task<List<string>> FetchOpenLibraryCoverCandidatesAsync(string title, string? author, IHttpClientFactory httpFactory, int limit = 12)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new List<string>();

        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(4);

            var coverScores = new Dictionary<int, int>();
            var candidateTitles = BuildCoverTitleCandidates(title);

            foreach (var candidate in candidateTitles)
            {
                var titleQuery = Uri.EscapeDataString(candidate);
                var authorQuery = string.IsNullOrWhiteSpace(author) ? "" : $"&author={Uri.EscapeDataString(author.Trim())}";
                var url = $"https://openlibrary.org/search.json?title={titleQuery}{authorQuery}&limit=20";

                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in docs.EnumerateArray())
                {
                    if (!item.TryGetProperty("cover_i", out var coverProp) || coverProp.ValueKind != JsonValueKind.Number)
                        continue;

                    var coverId = coverProp.GetInt32();
                    var editionCount = item.TryGetProperty("edition_count", out var editionProp) && editionProp.ValueKind == JsonValueKind.Number
                        ? editionProp.GetInt32()
                        : 0;

                    if (coverScores.TryGetValue(coverId, out var existing))
                    {
                        if (editionCount > existing)
                            coverScores[coverId] = editionCount;
                    }
                    else
                    {
                        coverScores[coverId] = editionCount;
                    }
                }
            }

            var covers = coverScores
                .OrderByDescending(kvp => kvp.Value)
                .Take(limit)
                .Select(kvp => $"https://covers.openlibrary.org/b/id/{kvp.Key}-L.jpg")
                .ToList();

            if (covers.Count == 0 && !string.IsNullOrWhiteSpace(author))
                return await FetchOpenLibraryCoverCandidatesAsync(title, null, httpFactory, limit);

            return covers;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Fetches multiple cover candidates from Google Books API.
    /// </summary>
    public static async Task<List<string>> FetchGoogleBooksCoverCandidatesAsync(string title, string? author, IHttpClientFactory httpFactory, int limit = 12)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new List<string>();

        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);

            var query = string.IsNullOrWhiteSpace(author)
                ? $"intitle:{title}"
                : $"intitle:{title} inauthor:{author}";
            var url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}&maxResults={Math.Max(limit, 5)}";

            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return new List<string>();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return new List<string>();

            var results = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("volumeInfo", out var info)) continue;
                if (!info.TryGetProperty("imageLinks", out var imageLinks)) continue;

                string? urlValue = null;
                if (imageLinks.TryGetProperty("thumbnail", out var thumb))
                    urlValue = thumb.GetString();
                else if (imageLinks.TryGetProperty("smallThumbnail", out var smallThumb))
                    urlValue = smallThumb.GetString();

                if (string.IsNullOrWhiteSpace(urlValue)) continue;

                // Upgrade to larger image by modifying URL parameters
                urlValue = urlValue.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                // Remove zoom parameter to get full-size image, or change &zoom=1 to &zoom=0
                urlValue = urlValue.Replace("&zoom=1", "&zoom=0", StringComparison.OrdinalIgnoreCase);
                urlValue = urlValue.Replace("?zoom=1", "?zoom=0", StringComparison.OrdinalIgnoreCase);
                // Some URLs have &edge=curl which reduces quality - remove it
                urlValue = urlValue.Replace("&edge=curl", "", StringComparison.OrdinalIgnoreCase);

                results.Add(urlValue);
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Builds a list of title candidates for cover lookup by simplifying the title.
    /// </summary>
    public static List<string> BuildCoverTitleCandidates(string title)
    {
        var candidates = new List<string>();
        var trimmed = title.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return candidates;

        string Simplify(string value)
        {
            var withoutBracket = Regex.Replace(value, @"\[[^\]]+\]", "").Trim();
            var withoutParens = Regex.Replace(withoutBracket, @"\([^)]+\)", "").Trim();
            var withoutSeries = Regex.Replace(withoutParens, @"\bbook\s+\d+\b", "", RegexOptions.IgnoreCase).Trim();
            var withoutDash = Regex.Replace(withoutSeries, @"\s*-\s*\d+\s*-\s*", " ").Trim();
            return Regex.Replace(withoutDash, @"\s{2,}", " ").Trim();
        }

        var baseTitle = Simplify(trimmed);
        candidates.Add(baseTitle);

        var colonSplit = baseTitle.Split(':')[0].Trim();
        if (!string.Equals(colonSplit, baseTitle, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(colonSplit))
            candidates.Add(colonSplit);

        if (!string.Equals(trimmed, baseTitle, StringComparison.OrdinalIgnoreCase))
            candidates.Add(trimmed);

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Validates if the cover image size is acceptable.
    /// </summary>
    public static bool IsCoverSizeValid(int width, int height)
    {
        // Accept any image that's at least 100x100 pixels
        // No ratio restrictions - any aspect ratio is fine
        return width >= MinCoverWidth && height >= MinCoverHeight;
    }

    /// <summary>
    /// Tries to get the image size from the byte data.
    /// </summary>
    public static bool TryGetImageSize(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (data.Length < 10)
            return false;

        // PNG: 89 50 4E 47 0D 0A 1A 0A, IHDR at offset 12
        if (data.Length >= 24 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            width = ReadInt32BigEndian(data, 16);
            height = ReadInt32BigEndian(data, 20);
            return width > 0 && height > 0;
        }

        // GIF: "GIF87a" or "GIF89a"
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
        {
            width = data[6] | (data[7] << 8);
            height = data[8] | (data[9] << 8);
            return width > 0 && height > 0;
        }

        // JPEG
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return TryGetJpegSize(data, out width, out height);
        }

        return false;
    }

    /// <summary>
    /// Determines the image file extension from URL or byte data.
    /// </summary>
    public static string DetermineImageExtension(string url, byte[] imageData)
    {
        var urlLower = url.ToLowerInvariant();
        if (urlLower.EndsWith(".jpg") || urlLower.EndsWith(".jpeg"))
            return ".jpg";
        if (urlLower.EndsWith(".png"))
            return ".png";
        if (urlLower.EndsWith(".gif"))
            return ".gif";

        if (imageData.Length >= 4)
        {
            if (imageData[0] == 0x89 && imageData[1] == 0x50 && imageData[2] == 0x4E && imageData[3] == 0x47)
                return ".png";
            if (imageData[0] == 0xFF && imageData[1] == 0xD8 && imageData[2] == 0xFF)
                return ".jpg";
            if (imageData[0] == 0x47 && imageData[1] == 0x49 && imageData[2] == 0x46)
                return ".gif";
        }

        return ".jpg";
    }

    private static int ReadInt32BigEndian(byte[] data, int offset)
    {
        return (data[offset] << 24)
             | (data[offset + 1] << 16)
             | (data[offset + 2] << 8)
             | data[offset + 3];
    }

    private static bool TryGetJpegSize(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;

        int index = 2;
        while (index + 9 < data.Length)
        {
            if (data[index] != 0xFF)
            {
                index++;
                continue;
            }

            byte marker = data[index + 1];
            if (marker == 0xD9 || marker == 0xDA)
                break;

            if (index + 3 >= data.Length)
                break;

            int length = (data[index + 2] << 8) + data[index + 3];
            if (length < 2 || index + 2 + length > data.Length)
                break;

            if (marker == 0xC0 || marker == 0xC2)
            {
                height = (data[index + 5] << 8) + data[index + 6];
                width = (data[index + 7] << 8) + data[index + 8];
                return width > 0 && height > 0;
            }

            index += 2 + length;
        }

        return false;
    }
}
