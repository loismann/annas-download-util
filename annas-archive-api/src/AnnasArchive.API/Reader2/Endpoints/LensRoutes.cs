using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>
/// <c>GET /lenses</c> — the registry, served.
///
/// <para>This route is the server half of the extensibility guarantee: the
/// picker renders from this response and never from a hard-coded list, so a
/// fourth book type appears in the UI with no frontend change at all. The
/// contract test in the test project registers a lens that exists only there and
/// asserts it shows up here.</para>
/// </summary>
internal static class LensRoutes
{
    public static RouteGroupBuilder MapLensRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/lenses", HandleList);
        return group;
    }

    private static IResult HandleList(ILensRegistry lenses) =>
        Results.Ok(lenses.All
            .Select(lens => LensResponse.From(lens, isDefault: lens.Key == lenses.Default.Key))
            .ToArray());
}
