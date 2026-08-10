using System.Text;

namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// The compacted state sent with every extraction call.
///
/// <para><b>This is what bounds the cost of the whole feature.</b> The extraction
/// call gets the chapter summary plus this, and nothing else — for each actor only
/// the id, canonical name, aliases, and tier; for threads and groups only the id
/// and name. No dossiers, no arcs, no beats. Without the cap, a book with 580
/// named characters produces a digest of roughly 17k tokens on every chapter,
/// which would make the story model cost more than the summaries it is built
/// from.</para>
///
/// <para><b>Why not tier alone.</b> Eliding purely by tier is the obvious answer
/// and it fails in a specific way: the actors dropped are exactly the minor ones,
/// and a minor actor missing from the digest is one the model cannot resolve an
/// alias to — so it invents a duplicate, and a duplicate is permanent. So the cap
/// is filled in tier order but only from actors seen in the last
/// <c>recentChapters</c> chapters, which means a walk-on introduced two
/// chapters ago beats a secondary character last seen two hundred pages back. It
/// is the second who is safe to drop: nobody is about to mention them under a new
/// name.</para>
///
/// <para>Majors are kept unconditionally, over the cap if it comes to that. The
/// cap protects a budget; dropping a protagonist breaks the feature.</para>
/// </summary>
public static class StoryDigest
{
    /// <summary>
    /// The digest for a model about to ingest <paramref name="chapter"/>, holding
    /// at most <paramref name="maxActors"/> actors.
    /// </summary>
    public static string Build(StoryModel model, int chapter, int maxActors, int recentChapters)
    {
        var text = new StringBuilder();

        Section(text, "Actors", Keep(model.Actors, chapter, maxActors, recentChapters).Select(Describe));
        Section(text, "Groups", model.Groups.Select(g => $"{g.Id}: {g.Name}"));
        Section(text, "Threads", model.Threads.Select(t => $"{t.Id}: {t.Name}"));

        return text.Length == 0 ? "(nothing recorded yet)" : text.ToString().TrimEnd();
    }

    /// <summary>
    /// Which actors survive the cap: every major, then whoever was seen most
    /// recently, up to the limit.
    /// </summary>
    /// <remarks>
    /// Returned in a stable order — tier, then most recently seen, then id — so
    /// that two digests built from the same model are byte-identical. A digest
    /// that reordered itself would change the prompt input on every call for no
    /// reason, which is both a wasted cache and an unreadable diff.
    /// </remarks>
    public static IReadOnlyList<Actor> Keep(
        IReadOnlyList<Actor> actors, int chapter, int maxActors, int recentChapters)
    {
        var ordered = actors
            .OrderByDescending(a => a.Tier)
            .ThenByDescending(a => a.LastSeenChapter)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToArray();

        if (maxActors <= 0 || ordered.Length <= maxActors) return ordered;

        var kept = ordered.Where(a => a.Tier == ActorTier.Major).ToList();

        // Majors alone can exceed the cap in a book with a very large principal
        // cast. Keeping them all is the deliberate choice: the cap protects the
        // budget, but dropping a protagonist breaks the feature outright.
        foreach (var actor in ordered.Where(a =>
                     a.Tier != ActorTier.Major && chapter - a.LastSeenChapter <= recentChapters))
        {
            if (kept.Count >= maxActors) break;
            kept.Add(actor);
        }

        return kept;
    }

    /// <summary>Id, name, aliases, tier — the four fields, and nothing that costs words.</summary>
    private static string Describe(Actor actor)
    {
        var aliases = actor.Aliases.Count > 0 ? $" (aka {string.Join("; ", actor.Aliases)})" : "";

        return $"{actor.Id}: {actor.CanonicalName}{aliases} [{actor.Tier.ToString().ToLowerInvariant()}]";
    }

    private static void Section(StringBuilder text, string heading, IEnumerable<string> lines)
    {
        var written = lines.ToArray();
        if (written.Length == 0) return;

        text.Append(heading).Append('\n');
        foreach (var line in written) text.Append("- ").Append(line).Append('\n');
        text.Append('\n');
    }
}
