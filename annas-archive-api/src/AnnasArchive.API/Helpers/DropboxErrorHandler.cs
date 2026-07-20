using Dropbox.Api;
using Dropbox.Api.Files;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Centralized error handling for Dropbox operations.
/// Consolidates the common try-catch patterns across multiple handlers.
/// </summary>
public static class DropboxErrorHandler
{
    /// <summary>
    /// Executes a Dropbox operation with standardized error handling.
    /// Returns a structured result with success status and error message.
    /// </summary>
    /// <typeparam name="T">The result type on success</typeparam>
    /// <param name="operation">The Dropbox operation to execute</param>
    /// <param name="operationName">Name of the operation for logging</param>
    /// <returns>A tuple of (success, result, errorMessage)</returns>
    public static async Task<(bool Success, T? Result, string? ErrorMessage)> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string operationName) where T : class
    {
        try
        {
            var result = await operation();
            return (true, result, null);
        }
        catch (ApiException<UploadError> ex)
        {
            var details = ex.ErrorResponse?.ToString() ?? ex.ToString();
            Log.Warning("[{Operation}] Dropbox upload failed: {ErrorMessage} | Details: {Details}",
                operationName, ex.Message, details);
            return (false, null, "Failed to upload file to Dropbox. Please try again.");
        }
        catch (HttpException ex)
        {
            var details = ex.ToString();
            Log.Warning("[{Operation}] Dropbox HTTP error ({StatusCode}): {ErrorMessage} | Uri: {RequestUri} | Details: {Details}",
                operationName, ex.StatusCode, ex.Message, ex.RequestUri, details);
            return (false, null, "Failed to upload file to Dropbox. Please try again.");
        }
        catch (DropboxException ex)
        {
            Log.Warning("[{Operation}] Dropbox error: {Exception}", operationName, ex);
            return (false, null, "Failed to upload file to Dropbox. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[{Operation}] HTTP request failed: {Exception}", operationName, ex);
            return (false, null, "Failed to upload file to Dropbox. Please try again.");
        }
        catch (ArgumentException ex)
        {
            Log.Information("[{Operation}] Invalid argument: {Message}", operationName, ex.Message);
            return (false, null, $"Invalid parameter: {ex.ParamName ?? "unknown"}");
        }
        catch (Exception ex)
        {
            Log.Warning("[{Operation}] Unexpected error: {ErrorMessage}", operationName, ex.Message);
            return (false, null, "Failed to upload file to Dropbox. Please try again.");
        }
    }

    /// <summary>
    /// Executes a Dropbox operation that may fail but isn't critical (e.g., backup operations).
    /// Logs warnings but doesn't propagate errors.
    /// </summary>
    /// <typeparam name="T">The result type on success</typeparam>
    /// <param name="operation">The Dropbox operation to execute</param>
    /// <param name="operationName">Name of the operation for logging</param>
    /// <returns>The result if successful, null otherwise</returns>
    public static async Task<T?> ExecuteNonCriticalAsync<T>(
        Func<Task<T>> operation,
        string operationName) where T : class
    {
        try
        {
            return await operation();
        }
        catch (ApiException<UploadError> ex)
        {
            var details = ex.ErrorResponse?.ToString() ?? ex.ToString();
            Log.Information("[{Operation}] Dropbox backup failed (non-critical): {ErrorMessage} | Details: {Details}",
                operationName, ex.Message, details);
        }
        catch (HttpException ex)
        {
            Log.Information("[{Operation}] Dropbox backup failed (non-critical, HTTP {StatusCode}): {ErrorMessage}",
                operationName, ex.StatusCode, ex.Message);
        }
        catch (DropboxException ex)
        {
            Log.Information("[{Operation}] Dropbox backup failed (non-critical): {Exception}", operationName, ex);
        }
        catch (ArgumentException ex)
        {
            Log.Information("[{Operation}] Dropbox backup failed (non-critical, ArgumentException): {Message}",
                operationName, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Information("[{Operation}] Dropbox backup failed (non-critical): {ErrorMessage}",
                operationName, ex.Message);
        }

        return null;
    }
}
