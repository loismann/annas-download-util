namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// Everything the merge decides about groups and about relationships.
///
/// <para>Together because they are the same shape of rule: both key on something
/// stable, both add without removing, and both drop anything pointing at an actor
/// or a group that does not exist. A reference that resolves to nothing is
/// something the model did not find and could not name, and hanging an edge off it
/// would put a relationship in the model with nobody at one end of it. Nothing
/// here ever removes a reference, so a bad one is permanent.</para>
///
/// <para><b>Order matters inside this pass.</b> Groups are opened before anybody
/// is put in one, so that a household founded and populated in the same chapter
/// works; and membership is settled from both directions at once, because the
/// extraction may report it as the group's members or as the actor's groups and
/// those must not become two different answers.</para>
/// </summary>
internal static class GroupEdgeMerge
{
    public static void Apply(MergeState state, StoryDelta delta)
    {
        foreach (var arrival in delta.NewGroups) Open(state, arrival);

        Enrol(state, delta);
        Feud(state, delta);

        foreach (var change in delta.EdgeChanges) Relate(state, change);
    }

    /// <summary>
    /// Adds a group, or finds the one already open under that name. Nothing is
    /// hung off it here — who is in it and who it is at odds with are settled once
    /// every group in this chapter exists.
    /// </summary>
    private static void Open(MergeState state, NewGroup arrival)
    {
        if (string.IsNullOrWhiteSpace(arrival.Name)) return;
        if (state.Groups.Any(g => NameMatch.Same(g.Name, arrival.Name))) return;

        state.Groups.Add(new Group(
            Id: state.NextId('g', state.Groups.Select(g => g.Id)),
            Name: arrival.Name.Trim(),
            Kind: arrival.Kind,
            MemberIds: [],
            RivalGroupIds: [],
            FirstSeenChapter: state.Chapter));
    }

    /// <summary>
    /// Records which groups are at odds, once they all exist.
    ///
    /// <para>Its own pass for the same reason membership is: two factions
    /// introduced by one chapter would otherwise depend on which of them the
    /// extraction happened to list first.</para>
    /// </summary>
    private static void Feud(MergeState state, StoryDelta delta)
    {
        IEnumerable<(string Group, string Rival)> Declared()
        {
            foreach (var group in delta.NewGroups)
                foreach (var rival in group.RivalGroupIds) yield return (group.Name, rival);

            foreach (var group in delta.GroupUpdates)
                foreach (var rival in group.RivalGroupIds ?? []) yield return (group.GroupId, rival);
        }

        foreach (var (group, rival) in Declared())
        {
            // Never itself: a faction listed as its own rival would draw an edge
            // from a node to that same node.
            if (state.ResolveGroup(group) is not { } id) continue;
            if (state.ResolveGroup(rival) is not { } rivalId || rivalId == id) continue;

            if (state.Groups.FirstOrDefault(g => g.Id == id) is not { } existing) continue;

            state.Groups[state.Groups.IndexOf(existing)] = existing with
            {
                RivalGroupIds = MergeLists.Ids(existing.RivalGroupIds, [rivalId])
            };
        }
    }

    /// <summary>
    /// Settles who is in which group, from whichever side the chapter reported it.
    ///
    /// <para>Runs after both the actors and the groups exist, which is the whole
    /// reason it is a pass of its own rather than a line in each of them: an actor
    /// admitted before their household was opened has a group reference that named
    /// nothing at the moment it was read.</para>
    /// </summary>
    private static void Enrol(MergeState state, StoryDelta delta)
    {
        IEnumerable<(string Actor, string Group)> Claimed()
        {
            foreach (var actor in delta.NewActors)
                foreach (var group in actor.GroupIds) yield return (actor.CanonicalName, group);

            foreach (var update in delta.ActorUpdates)
                foreach (var group in update.GroupIds ?? []) yield return (update.ActorId, group);

            foreach (var group in delta.NewGroups)
                foreach (var member in group.MemberIds) yield return (member, group.Name);

            foreach (var group in delta.GroupUpdates)
                foreach (var member in group.MemberIds ?? []) yield return (member, group.GroupId);
        }

        foreach (var (actor, group) in Claimed())
            if (state.ResolveActor(actor) is { } actorId && state.ResolveGroup(group) is { } groupId)
                state.Enrol(actorId, groupId);
    }

    /// <summary>
    /// Starts, annotates, or ends a relationship.
    ///
    /// <para>Ending sets a chapter rather than removing the row. "They were allies
    /// until chapter forty" is the fact a reader wants; a deleted edge asserts
    /// they never were, and the model has no way to tell us it was wrong to
    /// delete it.</para>
    /// </summary>
    private static void Relate(MergeState state, EdgeChange change)
    {
        if (string.IsNullOrWhiteSpace(change.Type)) return;

        // Resolved rather than trusted, and resolved to *ids* before anything is
        // stored: an edge held under the name the chapter happened to use would
        // not be found again when the next chapter uses another one.
        if (state.ResolveActor(change.From) is not { } from) return;
        if (state.ResolveActor(change.To) is not { } to) return;
        if (from == to) return;

        var identity = (from, to, change.Type);
        var existing = state.Edges.FirstOrDefault(e => e.Identity == identity);

        if (existing is null)
        {
            // An edge reported as already over still gets recorded, ended in the
            // same chapter — the relationship existed, and losing it because the
            // chapter that mentioned it is also the chapter that closed it would
            // be the same silent loss ending-by-deletion causes.
            state.Edges.Add(new Edge(
                from, to, change.Type.Trim(), state.Chapter,
                change.Ended ? state.Chapter : null, Noted([], state.Chapter, change.Note)));

            return;
        }

        state.Edges[state.Edges.IndexOf(existing)] = existing with
        {
            EndedChapter = change.Ended ? existing.EndedChapter ?? state.Chapter : existing.EndedChapter,
            Notes = Noted(existing.Notes, state.Chapter, change.Note)
        };
    }

    /// <summary>
    /// What passed between two people, appended and chapter-tagged, on the same
    /// rule as an actor's arc and a thread's beats: deduplicated on the pair, so
    /// re-reading a chapter that says the same thing adds nothing.
    /// </summary>
    private static IReadOnlyList<EdgeNote> Noted(
        IReadOnlyList<EdgeNote> notes, int chapter, string? what)
    {
        if (string.IsNullOrWhiteSpace(what)) return notes;

        var note = new EdgeNote(chapter, what.Trim());

        return notes.Contains(note) ? notes : [.. notes, note];
    }
}
