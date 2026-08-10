namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// Everything the merge decides about groups and about relationships.
///
/// <para>Together because they are the same shape of rule: both key on something
/// stable, both add without removing, and both drop anything pointing at an actor
/// or a group that does not exist. A model that invents an id is reporting
/// something it did not find in the digest, and hanging an edge off that id would
/// put a relationship in the model with nobody at one end of it. Nothing here ever
/// removes a reference, so an invented one is permanent.</para>
/// </summary>
internal static class GroupEdgeMerge
{
    public static void Apply(MergeState state, StoryDelta delta)
    {
        foreach (var arrival in delta.NewGroups) Open(state, arrival);
        foreach (var update in delta.GroupUpdates) Update(state, update);
        foreach (var change in delta.EdgeChanges) Relate(state, change);
    }

    private static void Open(MergeState state, NewGroup arrival)
    {
        if (string.IsNullOrWhiteSpace(arrival.Name)) return;

        if (state.Groups.FirstOrDefault(g => NameMatch.Same(g.Name, arrival.Name)) is { } existing)
        {
            state.Groups[state.Groups.IndexOf(existing)] = existing with
            {
                MemberIds = MergeLists.Ids(existing.MemberIds, arrival.MemberIds.Where(state.IsKnownActor)),
                RivalGroupIds = MergeLists.Ids(
                    existing.RivalGroupIds, arrival.RivalGroupIds.Where(state.IsKnownGroup))
            };

            return;
        }

        state.Groups.Add(new Group(
            Id: state.NextId('g', state.Groups.Select(g => g.Id)),
            Name: arrival.Name.Trim(),
            Kind: arrival.Kind,
            MemberIds: MergeLists.Ids([], arrival.MemberIds.Where(state.IsKnownActor)),
            RivalGroupIds: MergeLists.Ids([], arrival.RivalGroupIds.Where(state.IsKnownGroup)),
            FirstSeenChapter: state.Chapter));
    }

    private static void Update(MergeState state, GroupUpdate update)
    {
        if (state.Groups.FirstOrDefault(g => g.Id == update.GroupId) is not { } group) return;

        state.Groups[state.Groups.IndexOf(group)] = group with
        {
            MemberIds = MergeLists.Ids(group.MemberIds, (update.MemberIds ?? []).Where(state.IsKnownActor)),
            RivalGroupIds = MergeLists.Ids(
                group.RivalGroupIds, (update.RivalGroupIds ?? []).Where(state.IsKnownGroup))
        };
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
        if (!state.IsKnownActor(change.From) || !state.IsKnownActor(change.To)) return;
        if (change.From == change.To) return;

        var identity = (change.From, change.To, change.Type);
        var existing = state.Edges.FirstOrDefault(e => e.Identity == identity);

        if (existing is null)
        {
            // An edge reported as already over still gets recorded, ended in the
            // same chapter — the relationship existed, and losing it because the
            // chapter that mentioned it is also the chapter that closed it would
            // be the same silent loss ending-by-deletion causes.
            state.Edges.Add(new Edge(
                change.From, change.To, change.Type.Trim(), state.Chapter,
                change.Ended ? state.Chapter : null, change.Note));

            return;
        }

        state.Edges[state.Edges.IndexOf(existing)] = existing with
        {
            EndedChapter = change.Ended ? existing.EndedChapter ?? state.Chapter : existing.EndedChapter,
            Note = string.IsNullOrWhiteSpace(change.Note) ? existing.Note : change.Note.Trim()
        };
    }
}
