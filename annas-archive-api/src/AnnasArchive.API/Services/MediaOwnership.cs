using System.Security.Claims;
using AnnasArchive.API.Constants;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// The one way anything in this app becomes owned by a household member.
///
/// Every media type used to do this its own way: movies and TV tagged inside the
/// Sonarr/Radarr add handler, audiobooks only once a Listenarr import reconciled,
/// ebooks by writing a tag onto the book's own metadata. Three trigger points and
/// two copies of the name resolver — and all of them shared one habit that is the
/// actual bug: <c>if (owner is not null)</c>, with no else. An unresolvable user, a
/// bulk-import row with no owner column, or an add that never went through the app
/// at all produced an item owned by nobody, silently, with nothing in the log to
/// say so.
///
/// So: resolution has exactly one implementation
/// (<see cref="HouseholdOwners.ResolveName"/>), assignment has exactly one, and
/// failing to assign is always recorded with the reason and the call site.
/// </summary>
public static class MediaOwnership
{
    /// <summary>The household member behind the current request, or null when the
    /// authenticated name maps to nobody.</summary>
    public static string? ResolveMember(HttpContext? context) =>
        HouseholdOwners.ResolveName(context?.User?.FindFirst(ClaimTypes.Name)?.Value);

    /// <summary>
    /// Tags one library item with one member. <paramref name="source"/> names the
    /// call site and appears in the log on both paths, so "why does this have no
    /// owner" is answerable from the log alone rather than by reasoning about which
    /// of five code paths added it.
    /// </summary>
    /// <returns>True when an owner was recorded.</returns>
    public static bool Assign(
        IMediaMetadataService metadata,
        string mediaType,
        string id,
        string? rawName,
        string source)
    {
        var member = HouseholdOwners.ResolveName(rawName);
        if (member is null)
        {
            Log.Warning(
                "[Ownership] {Source} left {Type}:{Id} unowned — \"{RawName}\" is not a household member",
                source, mediaType, id, rawName ?? "(none)");
            return false;
        }

        try
        {
            metadata.AddOwner(mediaType, id, member);
            Log.Information("[Ownership] {Source} tagged {Type}:{Id} as {Member}",
                source, mediaType, id, member);
            return true;
        }
        catch (Exception ex)
        {
            // The acquisition itself already succeeded by the time we get here, so a
            // failed tag write must not turn a successful add into an error for the
            // caller. Loud in the log, invisible to the request.
            Log.Warning("[Ownership] {Source} failed to tag {Type}:{Id} as {Member}: {Message}",
                source, mediaType, id, member, ex.Message);
            return false;
        }
    }

    /// <summary>Convenience for the add handlers, which all have an
    /// <see cref="HttpContext"/> and want the caller to become the owner.</summary>
    public static bool AssignToCaller(
        IMediaMetadataService metadata, string mediaType, string id, HttpContext? context, string source) =>
        Assign(metadata, mediaType, id, context?.User?.FindFirst(ClaimTypes.Name)?.Value, source);
}
