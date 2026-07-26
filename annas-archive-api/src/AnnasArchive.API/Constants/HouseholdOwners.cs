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
}
