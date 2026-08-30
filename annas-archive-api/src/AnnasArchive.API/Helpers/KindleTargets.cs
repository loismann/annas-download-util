namespace AnnasArchive.API.Helpers;

/// <summary>
/// Everything that follows from "who is this book being sent to": the address, the
/// Dropbox folder, and the owner tag it gets afterwards.
///
/// <para><b>Why one place.</b> This was resolved in four, each with a catch-all
/// <c>else</c>, and they did not agree. An unrecognised target got Mom's email
/// (<c>GetKindleEmailForTarget</c>), Mom's Dropbox folder
/// (<c>GetDropboxFolderForKindleTarget</c>), and <b>Dad's</b> owner tag
/// (<c>LibraryHelpers.GetKindleTargetTag</c>) — a book emailed to one person and
/// recorded as the other's. Nothing was wrong today, because validation happened to
/// allow exactly the two values every branch was written for. That is the whole
/// safety margin: add a third person to a validator and books go to the wrong
/// Kindle, silently, with a success message.</para>
///
/// <para><b>So there is no <c>else</c> here.</b> An unknown target resolves to
/// <c>null</c> and the caller has to deal with it, which makes "sent to the wrong
/// person" unrepresentable rather than merely unlikely.</para>
/// </summary>
public sealed record KindleTarget(
    string Key,
    string HouseholdName,
    string EmailConfigKey,
    string DropboxFolder)
{
    private static readonly KindleTarget[] All =
    {
        new("dad", "Dad", "Email:DadsKindleEmail", "/dad_downloads"),
        new("mom", "Mom", "Email:MomsKindleEmail", "/mom_downloads")
    };

    /// <summary>
    /// The target, or null if it names nobody. Matched case-insensitively: the three
    /// resolvers this replaces all lowercased before comparing, so accepting only
    /// lowercase here would have been a behaviour change rather than a tidy-up.
    /// </summary>
    public static KindleTarget? For(string? target) =>
        string.IsNullOrWhiteSpace(target)
            ? null
            : All.FirstOrDefault(t => string.Equals(t.Key, target.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The owner tag the book carries afterwards — the <i>recipient's</i>,
    /// never the tag of whoever happened to be signed in and pressed the button.</summary>
    public string BookTag => Constants.HouseholdOwners.BookTagFor(HouseholdName);

    /// <summary>The configured address, or a throw naming the missing setting.</summary>
    public string EmailAddress(IConfiguration cfg) =>
        cfg[EmailConfigKey] ?? throw new InvalidOperationException($"{EmailConfigKey} not configured");

    /// <summary>For the "must be 'dad' or 'mom'" message, built from the list rather
    /// than written out again beside it.</summary>
    public static string Names => string.Join(" or ", All.Select(t => $"'{t.Key}'"));
}
