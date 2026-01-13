namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for cover image validation and processing.
/// Note: Cover fetching has been moved to IOpenLibraryService and IGoogleBooksService.
/// </summary>
public static class CoverLookupHelpers
{
    // Relaxed cover size validation - accept any reasonable image size
    private const int MinCoverWidth = 100;
    private const int MinCoverHeight = 100;

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
