namespace AnnasArchive.API.Constants;

/// <summary>
/// The household members and the ebook library's owner-tag convention
/// ("Paul's Books"), previously re-hardcoded at every use site. The media/
/// audiobook libraries store owners as a plain name list instead; until that
/// storage convention is unified, every conversion between the two shapes
/// goes through here — never hand-built "'s Books" strings.
/// Mirrors the frontend's constants/owners.ts.
/// </summary>
public static class HouseholdOwners
{
    public static readonly string[] Names = { "Paul", "Mom", "Dad" };

    public static string BookTagFor(string name) => $"{name}'s Books";

    public static readonly string[] BookTags = Names.Select(BookTagFor).ToArray();

    public static bool IsBookOwnerTag(string tag) =>
        BookTags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps a book owner tag ("Paul's Books") to the bare member name ("Paul"), or null.</summary>
    public static string? NameForBookTag(string tag) =>
        Names.FirstOrDefault(n => string.Equals(tag, BookTagFor(n), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The single mapping from whatever a caller happens to be holding — a JWT
    /// display name, a stored requester label like "Paul (Admin)", a bare name —
    /// onto one of the three household members, or null when it names nobody.
    ///
    /// This existed twice, in <c>LibraryHelpers.ResolveUserDisplayName</c> and in
    /// the audiobook reconciler, written independently. Both callers then treated
    /// null as "quietly skip tagging", which is how items reach a library owned by
    /// no one. Resolution lives here; refusing to be silent about a null is
    /// <see cref="AnnasArchive.API.Services.MediaOwnership"/>'s job.
    /// </summary>
    public static string? ResolveName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        var normalized = rawName.Trim().ToLowerInvariant();
        return Names.FirstOrDefault(name => normalized.Contains(name.ToLowerInvariant()));
    }
}
