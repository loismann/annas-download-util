namespace AnnasArchive.API.Helpers;

/// <summary>
/// The error body every endpoint answers with.
///
/// <para>The shape is <c>{ error }</c>, and the value of putting it here is that
/// it was previously written out by hand at ~290 call sites. That is not merely
/// untidy: it is why the one site that drifted went unnoticed. A quiz endpoint
/// answered <c>{ errors }</c> — plural, no singular <c>error</c> — which the
/// frontend interceptor cannot read, so its validation messages were computed,
/// serialised, sent, and silently thrown away in favour of "Http failure
/// response … 400 Bad Request".</para>
///
/// <para>Deliberately still just <c>{ error }</c> and not the richer
/// <c>{ error, errorCode, details }</c> that <c>UseGlobalExceptionHandler</c>
/// produces. The frontend interceptor already synthesises an
/// <c>errorCode</c> of <c>HTTP_&lt;status&gt;</c> and a <c>details</c> object
/// when the body carries neither, so adding them to every endpoint would be
/// ceremony nothing reads. <see cref="ValidationFailed"/> is the exception,
/// because there the extra field carries information the status code cannot.</para>
/// </summary>
public static class ApiResponse
{
    /// <summary>400 — the request was malformed or asked for something invalid.</summary>
    public static IResult BadRequest(string errorMessage) =>
        Results.BadRequest(new { error = errorMessage });

    /// <summary>404 — the thing asked for is not here.</summary>
    public static IResult NotFound(string errorMessage) =>
        Results.NotFound(new { error = errorMessage });

    /// <summary>
    /// 409 — the request is valid but conflicts with what already exists.
    ///
    /// <para>Added because 23 call sites already answered <c>Conflict</c> by hand
    /// while this class claimed to be the standard and had no method for it. A
    /// "standard" missing a case the code needs is a standard that gets bypassed.</para>
    /// </summary>
    public static IResult Conflict(string errorMessage) =>
        Results.Conflict(new { error = errorMessage });

    /// <summary>500 — we broke.</summary>
    public static IResult InternalError(string errorMessage) =>
        Results.Problem(detail: errorMessage, statusCode: 500);

    // No Unauthorized/Forbid/bodiless-NotFound wrapper. This class exists to keep
    // one error *body* in one place, and those three have no body to keep — a
    // wrapper would be a pure alias for `Results.X()`, which is why the
    // `Unauthorized()` that used to live here had zero callers while ten sites
    // called `Results.Unauthorized()` directly.
    //
    // The 26 bodiless responses in the endpoints are deliberate, not unconverted.
    // Three of them carry real intent: LibraryCoverEndpoints answers a non-image,
    // a path outside the root, and a file that is not there *identically*, because
    // a distinguishable response would tell an unauthenticated caller which books
    // the library holds. Giving those a message would undo that.

    /// <summary>
    /// 400 with the individual validation failures attached.
    ///
    /// <para>The only response that carries more than <c>error</c>, because a list
    /// of "what specifically is wrong" is information the status code and a single
    /// sentence cannot hold. <paramref name="errors"/> lands under
    /// <c>details.errors</c>, matching the <c>Record&lt;string, string[]&gt;</c>
    /// shape the frontend interceptor already reads from
    /// <c>UseGlobalExceptionHandler</c>, and <c>error</c> still carries a readable
    /// summary so a caller that only knows the standard shape still shows
    /// something useful.</para>
    /// </summary>
    public static IResult ValidationFailed(string errorMessage, IEnumerable<string> errors)
    {
        var list = errors as string[] ?? errors.ToArray();

        return Results.BadRequest(new
        {
            error = errorMessage,
            errorCode = "VALIDATION_ERROR",
            details = new Dictionary<string, string[]> { ["errors"] = list }
        });
    }
}
