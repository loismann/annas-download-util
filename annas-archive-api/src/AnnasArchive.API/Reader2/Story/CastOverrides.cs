using AnnasArchive.API.Reader2.Ai;

namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// One correction the reader has made to somebody in the cast.
/// </summary>
/// <param name="NameKey">
/// Who this is about, as <see cref="NameMatch.Key"/> of the name they had when
/// the edit was made.
///
/// <para><b>A name, because an id cannot work.</b> Ids are assigned as actors are
/// admitted, so a rebuild renumbers everybody — a correction stored against
/// <c>a12</c> would come back attached to whoever <c>a12</c> then happened to be,
/// which is the same failure that stops answered merge questions surviving a
/// rebuild. A name is the one handle that means the same thing on both sides of
/// one.</para>
/// </param>
/// <param name="PreferredName">
/// What to call them instead. The name the model chose is kept as an alias, so
/// nothing that referred to them by it stops resolving.
/// </param>
/// <param name="Note">The reader's own words. Never read or overwritten by extraction.</param>
/// <param name="SameAs">
/// Name keys of entries the reader says are this same person — the manual
/// counterpart to the questions the merger raises on its own.
/// </param>
/// <param name="Hidden">
/// Kept off the map.
///
/// <para><b>Hidden, not deleted.</b> A cast this size has walk-ons the reader
/// will never care about, and a map crowded with them is unreadable — but the
/// extraction found them in the book, and a record that quietly forgot people
/// would be a record nobody could trust. So they stay in the cast, marked, with
/// this set; the map is what leaves them out, and one click puts them back.</para>
/// </param>
public sealed record CastOverride(
    string NameKey,
    string? PreferredName = null,
    string? Note = null,
    IReadOnlyList<string>? SameAs = null,
    bool Hidden = false)
{
    public IReadOnlyList<string> Fused => SameAs ?? [];

    /// <summary>True when there is nothing left to store — the row is then dropped.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(PreferredName)
        && string.IsNullOrWhiteSpace(Note)
        && Fused.Count == 0
        && !Hidden;
}

/// <summary>
/// Everything the reader has corrected about a book's cast.
///
/// <para><b>Never written by extraction, and never destroyed by a rebuild.</b>
/// The story model is the model's account of the book; this is the reader's, and
/// keeping them in separate artifacts is what makes "your corrections outlive a
/// rebuild" true by construction rather than by a step somebody has to remember.
/// </para>
///
/// <para>Applied as a projection on read — see <see cref="CastCorrections"/> — so
/// nothing here is destructive and every edit is undone by deleting it.</para>
/// </summary>
public sealed record CastOverrides(IReadOnlyList<CastOverride> Entries)
    : IVersionedArtifact<CastOverrides>
{
    public static int SchemaVersion => 1;

    public static CastOverrides Empty { get; } = new([]);

    /// <summary>The correction for a name, or null.</summary>
    public CastOverride? For(string nameKey) =>
        Entries.FirstOrDefault(e => e.NameKey == nameKey);

    /// <summary>
    /// This set with one entry replaced, added, or — when it says nothing —
    /// removed. Storing an empty correction would leave the reader unable to tell
    /// a cleared edit from one that never happened.
    /// </summary>
    public CastOverrides With(CastOverride entry)
    {
        var rest = Entries.Where(e => e.NameKey != entry.NameKey);

        return new CastOverrides(entry.IsEmpty ? [.. rest] : [.. rest, entry]);
    }

    public CastOverrides Without(string nameKey) =>
        new([.. Entries.Where(e => e.NameKey != nameKey)]);
}
