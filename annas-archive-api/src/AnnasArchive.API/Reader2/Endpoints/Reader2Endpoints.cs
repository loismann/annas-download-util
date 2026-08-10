using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>
/// The one <c>/api/reader2</c> group. Every Reader II route hangs off it, so the
/// auth and rate-limiting pair is declared once here and inherited — a route
/// added in a later phase cannot ship without them by forgetting a line. An
/// architecture test checks that from the other direction.
/// </summary>
public static class Reader2Endpoints
{
    public const string RoutePrefix = "/api/reader2";

    public static WebApplication MapReader2Endpoints(this WebApplication app)
    {
        // Resolved here, at startup, for their side effects. LensRegistry validates
        // every registered lens in its constructor; Reader2Options.Validate rejects
        // a budget of zero or a temperature set alongside a reasoning effort. Both
        // must fail the deploy rather than a reader's first click — Reader I shipped
        // a token budget nothing read for months precisely because nothing checked.
        app.Services.GetRequiredService<ILensRegistry>();
        app.Services.GetRequiredService<Reader2Options>().Validate();

        var group = app.MapGroup(RoutePrefix)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapLensRoutes();
        group.MapBookRoutes();
        group.MapChapterRoutes();
        group.MapAiRoutes();
        group.MapReadingRoutes();
        group.MapVocabularyRoutes();
        group.MapStoryRoutes();

        return app;
    }

    /// <summary>
    /// The failure shape every Reader II route answers in, so a client has one
    /// thing to read rather than a different envelope per endpoint.
    /// </summary>
    internal static IResult Problem(int status, string message) =>
        Results.Json(new { error = message }, statusCode: status);

    internal static IResult BadRequest(string message) => Problem(400, message);

    internal static IResult NotFound(string message) => Problem(404, message);

    /// <summary>
    /// Parses a book id from a route value. Anything that is not sixteen hex
    /// characters never reached the database in the first place, so it is a 404
    /// rather than a 400 — from the reader's side there is no such book either way.
    /// </summary>
    internal static bool TryBookRef(string? bookId, out BookRef book, out IResult failure)
    {
        if (BookRef.TryParse(bookId, out book))
        {
            failure = Results.Empty;
            return true;
        }

        failure = NotFound("No such book.");
        return false;
    }
}
