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

    /// <summary>
    /// Every way the configured household (<c>Auth:AccessCodes</c>) and the roster
    /// above can disagree. Empty means every configured member owns things under
    /// their own name.
    ///
    /// This exists because <see cref="ResolveName"/> fails by returning null, and
    /// null is indistinguishable from "this add had no user" at the call site — so
    /// <see cref="Services.MediaOwnership.Assign"/> logs one warning per item and
    /// carries on. A member whose display name stops resolving therefore silently
    /// unowns everything they add, one warning at a time, forever. That is not
    /// hypothetical: a corrupted <c>Name</c> did exactly this until 2026-08-06.
    ///
    /// Checked at startup and by the <c>household-owners</c> health check, because
    /// all of it is knowable before a single item goes unowned.
    /// </summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        var members = Helpers.HouseholdIdentity.Members(configuration);
        if (members.Count == 0)
            return [];

        var problems = new List<string>();
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in members)
        {
            var resolved = ResolveName(member.Name);
            if (resolved is null)
            {
                problems.Add(
                    $"\"{member.Name}\" resolves to no household member, so everything they " +
                    $"add will be left unowned. Its name must contain one of: {string.Join(", ", Names)}.");
                continue;
            }

            // Resolution is a substring match, first match wins, in the order of
            // Names — so "Paula (Mom)" contains "paul" and resolves to Paul, not
            // Mom. A collision is the only symptom that surfaces, and it means one
            // person is quietly filing things under another's name.
            if (claimed.TryGetValue(resolved, out var alreadyClaimedBy))
            {
                problems.Add(
                    $"\"{member.Name}\" and \"{alreadyClaimedBy}\" both resolve to {resolved}, " +
                    $"so their libraries are merged. Names are matched as substrings, first match " +
                    $"wins in the order {string.Join(", ", Names)}.");
                continue;
            }

            claimed[resolved] = member.Name;
        }

        // Roster drift the other way: a name here that nobody is configured as.
        // Harmless on its own — it just means an owner filter no one can match —
        // but it is how the two lists start disagreeing.
        foreach (var name in Names.Where(name => !claimed.ContainsKey(name)))
        {
            problems.Add(
                $"{name} is in the household roster but no configured member resolves to them, " +
                $"so their owner filter will always be empty.");
        }

        return problems;
    }
}
