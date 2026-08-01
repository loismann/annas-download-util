using System.Net;
using System.Net.Sockets;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Input validation helpers for endpoint parameters.
/// </summary>
public static class ValidationHelpers
{
    /// <summary>
    /// Validates that a string does not exceed the maximum length.
    /// </summary>
    /// <param name="value">The string value to validate.</param>
    /// <param name="paramName">The parameter name for error messages.</param>
    /// <param name="maxLength">Maximum allowed length (default 500).</param>
    /// <returns>An IResult with BadRequest if invalid, null if valid.</returns>
    public static IResult? ValidateStringLength(string? value, string paramName, int maxLength = 500)
    {
        if (!string.IsNullOrEmpty(value) && value.Length > maxLength)
            return ApiResponse.BadRequest($"{paramName} exceeds maximum length of {maxLength}");
        return null;
    }

    /// <summary>
    /// Validates that a string is a valid absolute HTTP/HTTPS URL.
    /// </summary>
    /// <param name="url">The URL string to validate.</param>
    /// <param name="paramName">The parameter name for error messages.</param>
    /// <returns>An IResult with BadRequest if invalid, null if valid.</returns>
    public static IResult? ValidateUrl(string? url, string paramName)
    {
        if (!string.IsNullOrEmpty(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return ApiResponse.BadRequest($"{paramName} is not a valid URL");

            // Only allow http/https schemes (reject file://, ftp://, etc.)
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return ApiResponse.BadRequest($"{paramName} is not a valid URL");
        }
        return null;
    }

    /// <summary>
    /// Resolves a user-supplied URL and rejects it if it points anywhere inside
    /// this host's own network — the SSRF guard for endpoints that fetch an
    /// arbitrary URL server-side.
    ///
    /// The resolve step is the point: this process sits on the compose network
    /// alongside Sonarr/Radarr/Gluetun-control/Seq, several of which are
    /// unauthenticated or key-only, so a URL that merely *looks* public
    /// ("http://cdn.example.com/x.jpg") but resolves to 172.18.x.x would
    /// otherwise be fetched from a trusted position on that network. Checking
    /// every resolved address also covers a hostname that returns a mix.
    /// </summary>
    /// <param name="uri">An already-parsed absolute http(s) URI.</param>
    /// <param name="ct">Cancellation token for the DNS lookup.</param>
    /// <returns>True when every resolved address is a public one.</returns>
    public static async Task<bool> IsPubliclyRoutableAsync(Uri uri, CancellationToken ct = default)
    {
        IPAddress[] addresses;
        try
        {
            // A literal IP in the URL parses here without a DNS round trip.
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            // Unresolvable is not fetchable — fail closed rather than handing
            // the request to HttpClient to find out.
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    /// <summary>Whether one address is outside every reserved/private range.</summary>
    private static bool IsPublicAddress(IPAddress address)
    {
        // Map IPv4-in-IPv6 (::ffff:192.168.0.1) down before range-checking, or
        // a private v4 address wearing a v6 costume would read as public.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => false,                                  // 0.0.0.0/8 "this network"
                10 => false,                                 // 10.0.0.0/8 private
                127 => false,                                // loopback (covered above; explicit)
                169 when b[1] == 254 => false,               // 169.254.0.0/16 link-local + cloud metadata
                172 when b[1] >= 16 && b[1] <= 31 => false,  // 172.16.0.0/12 private (Docker lives here)
                192 when b[1] == 168 => false,               // 192.168.0.0/16 private
                100 when b[1] >= 64 && b[1] <= 127 => false, // 100.64.0.0/10 CGNAT (Tailscale)
                >= 224 => false,                             // multicast + reserved
                _ => true
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !address.IsIPv6LinkLocal
                && !address.IsIPv6SiteLocal
                && !address.IsIPv6Multicast
                && !address.GetAddressBytes()[0].Equals((byte)0xfd) // fc00::/7 unique-local
                && !address.GetAddressBytes()[0].Equals((byte)0xfc);
        }

        return false;
    }

    /// <summary>
    /// Validates that an integer is non-negative.
    /// </summary>
    /// <param name="value">The integer value to validate.</param>
    /// <param name="paramName">The parameter name for error messages.</param>
    /// <returns>An IResult with BadRequest if invalid, null if valid.</returns>
    public static IResult? ValidateNonNegativeInt(int value, string paramName)
    {
        if (value < 0)
            return ApiResponse.BadRequest($"{paramName} must be a non-negative integer");
        return null;
    }

    /// <summary>
    /// Validates that an integer is positive (greater than zero).
    /// </summary>
    /// <param name="value">The integer value to validate.</param>
    /// <param name="paramName">The parameter name for error messages.</param>
    /// <returns>An IResult with BadRequest if invalid, null if valid.</returns>
    public static IResult? ValidatePositiveInt(int value, string paramName)
    {
        if (value <= 0)
            return ApiResponse.BadRequest($"{paramName} must be a positive integer");
        return null;
    }

    /// <summary>
    /// Validates that a file path does not contain path traversal attacks.
    /// Checks for "..", "//", and absolute paths.
    /// </summary>
    /// <param name="path">The file path to validate.</param>
    /// <param name="paramName">The parameter name for error messages.</param>
    /// <returns>An IResult with BadRequest if invalid, null if valid.</returns>
    public static IResult? ValidateFilePath(string? path, string paramName)
    {
        if (!string.IsNullOrEmpty(path))
        {
            if (path.Contains("..") || path.Contains("//") || Path.IsPathRooted(path))
                return ApiResponse.BadRequest($"{paramName} contains invalid path characters");
        }
        return null;
    }

    /// <summary>
    /// Validates that a file name does not contain path traversal attacks.
    /// More strict than ValidateFilePath - also rejects forward/back slashes.
    /// </summary>
    /// <param name="fileName">The file name to validate.</param>
    /// <param name="paramName">The parameter name for error messages.</param>
    /// <returns>An IResult with BadRequest if invalid, null if valid.</returns>
    public static IResult? ValidateFileName(string? fileName, string paramName)
    {
        if (!string.IsNullOrEmpty(fileName))
        {
            if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return ApiResponse.BadRequest($"{paramName} contains invalid characters");
        }
        return null;
    }

    /// <summary>
    /// Validates a long value is non-negative.
    /// </summary>
    /// <param name="value">The long value to validate.</param>
    /// <param name="paramName">The parameter name for error messages.</param>
    /// <returns>An IResult with BadRequest if invalid, null if valid.</returns>
    public static IResult? ValidateNonNegativeLong(long value, string paramName)
    {
        if (value < 0)
            return ApiResponse.BadRequest($"{paramName} must be a non-negative value");
        return null;
    }

    /// <summary>
    /// Combines multiple validation results, returning the first error or null if all pass.
    /// </summary>
    /// <param name="validations">Array of validation results.</param>
    /// <returns>The first non-null IResult, or null if all validations pass.</returns>
    public static IResult? CombineValidations(params IResult?[] validations)
    {
        foreach (var validation in validations)
        {
            if (validation != null)
                return validation;
        }
        return null;
    }
}
