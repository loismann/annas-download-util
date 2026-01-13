namespace AnnasArchive.API.Helpers;

/// <summary>
/// Standard API Response Helper for consistent error responses.
/// </summary>
public static class ApiResponse
{
    /// <summary>
    /// Returns a standardized 400 Bad Request with error message
    /// </summary>
    public static IResult BadRequest(string errorMessage) =>
        Results.BadRequest(new { error = errorMessage });

    /// <summary>
    /// Returns a standardized 404 Not Found with error message
    /// </summary>
    public static IResult NotFound(string errorMessage) =>
        Results.NotFound(new { error = errorMessage });

    /// <summary>
    /// Returns a standardized 500 Internal Server Error with error message
    /// </summary>
    public static IResult InternalError(string errorMessage) =>
        Results.Problem(detail: errorMessage, statusCode: 500);

    /// <summary>
    /// Returns a standardized 401 Unauthorized
    /// </summary>
    public static IResult Unauthorized() =>
        Results.Unauthorized();
}
