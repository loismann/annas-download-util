using AnnasArchive.API.Helpers;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// The shape every "fetch a book description from source X" endpoint has:
/// require a title, ask the source, answer 200 with whatever came back.
///
/// <para>Four endpoints were written out separately — Google Books, OpenLibrary
/// and Wikipedia in <c>BookSearchEndpoints</c>, plus the AI one in
/// <c>AnnaDownloadEndpoints</c> — and all four had drifted into the same logging
/// defect independently (see <see cref="FetchAsync"/>). One copy is one place to
/// fix it.</para>
/// </summary>
public static class DescriptionEndpoint
{
    /// <summary>
    /// Runs one description lookup and turns it into a response.
    /// </summary>
    ///
    /// <remarks>
    /// <para><b>The title is a Serilog argument, never part of the template.</b>
    /// All four copies previously built their outcome line by interpolation —
    /// <c>Log.Information(found ? $"… for '{title}'" : …)</c> — which hands
    /// Serilog a message template containing the title's own text. A title with a
    /// brace in it, and scraped titles do have them, is then parsed as a
    /// placeholder and the line is mangled or dropped. It is the same defect
    /// <c>GamingEndpoints</c> carries a warning comment about, arrived at four
    /// separate times.</para>
    ///
    /// <para>A source that has no description is a normal answer, not a failure:
    /// the caller simply tries the next source. So this is 200 with a null
    /// description, never a 404 — a 404 would make the frontend special-case
    /// "missing" per source, and would put an ordinary miss in the error logs.</para>
    /// </remarks>
    ///
    /// <param name="source">Source name for the log line, e.g. "Google Books".</param>
    /// <param name="title">Required; blank is rejected before the source is asked,
    /// so a bad request never costs an upstream call.</param>
    /// <param name="author">Passed through as-is. The sources disagree about
    /// whether they accept null, so adapting it is the caller's job.</param>
    /// <param name="lookup">The description, or null when this source has none.</param>
    public static async Task<IResult> FetchAsync(
        string source,
        string? title,
        string? author,
        Func<string, string?, Task<string?>> lookup)
    {
        if (string.IsNullOrWhiteSpace(title))
            return ApiResponse.BadRequest("title is required.");

        Log.Information("{Source} description lookup: title='{Title}', author='{Author}'", source, title, author);

        var description = await lookup(title, author);

        // Blank counts as "not found" for the log line only — the body still
        // carries exactly what the source returned. Three of the four callers
        // used to report a whitespace-only description as a hit.
        Log.Information("{Source} description {Outcome} for '{Title}'",
            source, string.IsNullOrWhiteSpace(description) ? "not found" : "found", title);

        return Results.Ok(new { description });
    }
}
