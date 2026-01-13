using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping LibGen-related endpoints.
/// </summary>
public static class LibGenEndpoints
{
    /// <summary>
    /// Maps LibGen endpoints to the application.
    /// </summary>
    public static WebApplication MapLibGenEndpoints(this WebApplication app)
    {
        // LibGen search endpoint
        app.MapGet("/api/libgen/book", HandleLibGenSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleLibGenSearch(
        [FromQuery] string name,
        LibGenService svc,
        IValidationService validation,
        IConfiguration cfg,
        [FromQuery] bool exact = false)
    {
        Console.WriteLine($"[API LibGen Search] Received request: name='{name}', exact={exact}");

        if (!validation.IsValidSearchQuery(name))
        {
            Console.WriteLine($"[API LibGen Search] Validation failed for query: '{name}'");
            return Results.BadRequest(new {
                error = "Query parameter 'name' is required and must be between 1 and 500 characters."
            });
        }

        var searchLimit = cfg.GetValue<int>("Anna:SearchLimit", 25);
        Console.WriteLine($"[API LibGen Search] Calling LibGenService.SearchAsync...");
        var books = (await svc.SearchAsync(name, searchLimit, exact)).ToList();
        Console.WriteLine($"[API LibGen Search] Service returned {books.Count} books");

        if (exact)
        {
            var originalCount = books.Count;
            books = books
                .Where(b => string.Equals(b.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            Console.WriteLine($"[API LibGen Search] After exact filter: {books.Count} books (was {originalCount})");
        }

        if (books.Any())
        {
            var result = books.Count == 1 ? Results.Ok(books[0]) : Results.Ok(books);
            Console.WriteLine($"[API LibGen Search] Returning {books.Count} books");
            return result;
        }
        else
        {
            Console.WriteLine($"[API LibGen Search] No books found, returning 404");
            return ApiResponse.NotFound("No books found matching that name.");
        }
    }
}
