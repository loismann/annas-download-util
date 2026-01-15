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
