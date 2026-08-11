namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// Everything the merge decides about places.
///
/// <para>The same discipline as <see cref="ActorMerge"/>, and for the same
/// reason: a book that calls one city Ravensmarch, the Marches, and simply "the
/// capital" will otherwise be recorded as having three cities, and a reader
/// asking where they last saw somebody gets three answers.</para>
///
/// <para>Simpler than the cast in one respect on purpose. Places are not fused by
/// a reader's answer and raise no candidate merges: a wrong place merge is
/// cheaply visible and cheaply undone, while a wrong <i>person</i> merge is a
/// story nobody can see is wrong. So an ambiguous name here is left alone rather
/// than becoming a question, and the reader is never asked about geography.</para>
/// </summary>
internal static class PlaceMerge
{
    public static void Apply(MergeState state, StoryDelta delta)
    {
        foreach (var arrival in delta.NewPlaces) Admit(state, arrival);
        foreach (var update in delta.PlaceUpdates) Update(state, update);

        // After both, because a place may be reported inside one that arrives in
        // the same chapter — and at this point everything named has an id.
        Contain(state, delta);
    }

    /// <summary>
    /// Adds a place — unless it is one already here under another name, in which
    /// case this chapter's report becomes an update.
    /// </summary>
    private static void Admit(MergeState state, NewPlace arrival)
    {
        if (string.IsNullOrWhiteSpace(arrival.Name)) return;

        var names = arrival.Aliases.Prepend(arrival.Name).ToArray();
        var existing = state.Places.FirstOrDefault(
            p => names.Any(n => p.AllNames.Any(known => NameMatch.Same(known, n))));

        if (existing is not null)
        {
            Replace(state, existing with
            {
                LastSeenChapter = state.Chapter,
                Aliases = MergeLists.Names(
                    existing.Aliases, names.Where(n => !NameMatch.Same(n, existing.Name))),
                Kind = existing.Kind is PlaceKind.Other ? arrival.Kind : existing.Kind,
                Description = Prefer(existing.Description, arrival.Description)
            });

            return;
        }

        state.Places.Add(new Place(
            Id: state.NextId('p', state.Places.Select(p => p.Id)),
            Name: arrival.Name.Trim(),

            // Filtered against the canonical name, as an actor's are: a model
            // listing a place's own name among its aliases would otherwise put it
            // in the digest twice on every chapter for the life of the book.
            Aliases: MergeLists.Names([], arrival.Aliases.Where(n => !NameMatch.Same(n, arrival.Name))),
            Kind: arrival.Kind,
            Description: arrival.Description.Trim(),

            // Left empty and filled by Contain below, once everything this chapter
            // named has an id to point at.
            PartOf: "",
            FirstSeenChapter: state.Chapter,
            LastSeenChapter: state.Chapter));
    }

    /// <summary>
    /// Applies a change to a place already here. An unknown reference is dropped
    /// rather than invented into a place: the model does not assign ids.
    /// </summary>
    private static void Update(MergeState state, PlaceUpdate update)
    {
        if (Find(state, update.PlaceId) is not { } place) return;

        Replace(state, place with
        {
            LastSeenChapter = state.Chapter,
            Kind = update.Kind ?? place.Kind,
            Description = Prefer(place.Description, update.Description ?? ""),
            Aliases = MergeLists.Names(
                place.Aliases, (update.Aliases ?? []).Where(n => !NameMatch.Same(n, place.Name)))
        });
    }

    /// <summary>
    /// Puts each place inside whatever contains it, once every name has an id.
    ///
    /// <para>Resolved rather than stored as written, and refused when it would
    /// make a place contain itself. A cycle here is not a tidiness problem: the
    /// panel walks the chain upward to say where somewhere is, and a loop would
    /// walk forever.</para>
    /// </summary>
    private static void Contain(MergeState state, StoryDelta delta)
    {
        var wanted = delta.NewPlaces
            .Select(p => (Reference: p.Name, Container: p.PartOf))
            .Concat(delta.PlaceUpdates
                .Where(u => u.PartOf is not null)
                .Select(u => (Reference: u.PlaceId, Container: u.PartOf!)));

        foreach (var (reference, container) in wanted)
        {
            if (Find(state, reference) is not { } place) continue;
            if (state.ResolvePlace(container) is not { } inside) continue;
            if (inside == place.Id || Encloses(state, place.Id, inside)) continue;

            Replace(state, place with { PartOf = inside });
        }
    }

    /// <summary>Whether <paramref name="inner"/> already contains <paramref name="outer"/>.</summary>
    private static bool Encloses(MergeState state, string inner, string outer)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var at = outer;

        while (state.Places.FirstOrDefault(p => p.Id == at) is { PartOf: not "" } step && seen.Add(at))
        {
            if (step.PartOf == inner) return true;

            at = step.PartOf;
        }

        return false;
    }

    private static Place? Find(MergeState state, string? reference) =>
        state.ResolvePlace(reference) is { } id
            ? state.Places.FirstOrDefault(p => p.Id == id)
            : null;

    private static void Replace(MergeState state, Place place)
    {
        var at = state.Places.FindIndex(p => p.Id == place.Id);

        if (at >= 0) state.Places[at] = place;
    }

    /// <summary>
    /// What is already recorded wins over a blank, and a blank never overwrites.
    /// A chapter that mentions a place in passing must not empty the description
    /// written by the chapter that arrived there.
    /// </summary>
    private static string Prefer(string existing, string arriving) =>
        string.IsNullOrWhiteSpace(arriving) ? existing : arriving.Trim();
}
