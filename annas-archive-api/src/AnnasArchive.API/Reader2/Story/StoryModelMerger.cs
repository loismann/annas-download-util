namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// The thresholds the merge is tuned by, so no rule reads configuration itself.
/// </summary>
/// <param name="ThreadDormantAfterChapters">
/// Chapters without a beat before a thread is called dormant.
/// </param>
/// <param name="TierDemotionAfterChapters">
/// Chapters of absence before a proposed demotion is honoured. Promotion has no
/// such gate — somebody becoming important is news, somebody being quiet is not.
/// </param>
public sealed record StoryMergeRules(int ThreadDormantAfterChapters, int TierDemotionAfterChapters)
{
    public static StoryMergeRules Default { get; } = new(10, 10);
}

/// <summary>
/// Folds one chapter's extraction into the story model.
///
/// <para><b>Pure C#, and the correctness core of the feature.</b> The model reads
/// prose and proposes; every decision that could be wrong in a way the reader
/// cannot see is taken here, where it can be tested exhaustively without a
/// network call. Nothing in this file talks to anything.</para>
///
/// <para>Three rules hold everything else together: <i>nothing is ever deleted</i>,
/// <i>anything uncertain becomes a question rather than a change</i>, and
/// <i>chapter-tagged history is append-only</i>. Reader I had none of the three,
/// which is why its cast list silently lost people.</para>
/// </summary>
public static class StoryModelMerger
{
    /// <summary>
    /// <paramref name="current"/> with <paramref name="delta"/> folded in, or
    /// unchanged if this chapter is already in.
    /// </summary>
    /// <remarks>
    /// The idempotency check lives here as well as in the caller. The caller skips
    /// early to avoid paying for an extraction it will discard; this one is what
    /// makes the guarantee true, because a merge is not reversible and a second
    /// application would append every arc point and beat twice.
    /// </remarks>
    public static StoryModel Merge(StoryModel current, StoryDelta delta, StoryMergeRules rules)
    {
        if (current.HasIngested(delta.Chapter)) return current;

        var state = new MergeState(current, delta.Chapter, rules);

        ActorMerge.Apply(state, delta);
        GroupEdgeMerge.Apply(state, delta);
        ThreadMerge.Apply(state, delta);
        PlaceMerge.Apply(state, delta);

        return state.Result();
    }
}

/// <summary>
/// The one list rule every merge pass shares: added, never removed, never twice.
///
/// <para>Two methods rather than one, because an id is not a name. Ids compare
/// exactly; names compare through <see cref="NameMatch"/>. Folding them together
/// would work by accident today and stop the moment an id is not a bare
/// token.</para>
/// </summary>
internal static class MergeLists
{
    public static IReadOnlyList<string> Ids(IReadOnlyList<string> existing, IEnumerable<string> additions) =>
        [.. existing.Concat(additions.Where(id => !string.IsNullOrWhiteSpace(id)))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Names added, never removed, and never the same name twice.
    ///
    /// <para>Deduplicated by <see cref="NameMatch"/> rather than by string, so
    /// "Bezúkhov" does not join a list that already holds "Bezukhov". Shared with
    /// <see cref="MergeResolution"/> deliberately: fusing two entries is exactly
    /// where two spellings of one person meet, so it is the last place that
    /// should be running a weaker rule than the merge does.</para>
    /// </summary>
    public static IReadOnlyList<string> Names(IReadOnlyList<string> existing, IEnumerable<string> additions)
    {
        var kept = existing.ToList();

        foreach (var addition in additions)
        {
            if (string.IsNullOrWhiteSpace(addition)) continue;
            if (kept.Any(k => NameMatch.Same(k, addition))) continue;

            kept.Add(addition.Trim());
        }

        return kept;
    }
}

/// <summary>
/// The model part-way through a merge.
///
/// <para>Mutable, and deliberately not public. The merge is a pure function from
/// the outside; inside, four passes each add to the same lists, and threading
/// five immutable collections through them would obscure the rules rather than
/// protect anything.</para>
/// </summary>
internal sealed class MergeState(StoryModel current, int chapter, StoryMergeRules rules)
{
    public int Chapter { get; } = chapter;
    public StoryMergeRules Rules { get; } = rules;

    public List<Actor> Actors { get; } = [.. current.Actors];
    public List<Group> Groups { get; } = [.. current.Groups];
    public List<Edge> Edges { get; } = [.. current.Edges];
    public List<StoryThread> Threads { get; } = [.. current.Threads];
    public List<CandidateMerge> Candidates { get; } = [.. current.CandidateMerges];
    public List<Place> Places { get; } = [.. current.Places];

    private readonly IReadOnlyList<int> _ingested = current.ChaptersIngested;

    public StoryModel Result() => new(
        Actors, Groups, Edges, Threads, Candidates,
        [.. _ingested.Append(Chapter).Distinct().Order()],
        Places);

    /// <summary>The actor with this id, or null. Model-supplied ids are not trusted.</summary>
    public Actor? Actor(string? id) =>
        id is null ? null : Actors.FirstOrDefault(a => a.Id == id);

    public bool IsKnownActor(string? id) => Actor(id) is not null;

    /// <summary>
    /// The id of the actor a reference names, or null when it names nobody.
    ///
    /// <para><b>An id or a name, because an id alone cannot work.</b> Ids are
    /// assigned here and travel to the model only in the digest, so somebody this
    /// chapter introduces has no id the extraction could possibly have used. When
    /// only ids were accepted, every relationship between two people who arrive in
    /// the same chapter was dropped — which on a book's first ingest is every
    /// relationship there is, and the record came back a list of strangers.</para>
    ///
    /// <para>A name matching two actors resolves to neither. Actors who answer to
    /// one name are normally fused on admission, so a surviving pair is one the
    /// reader has already refused to merge, and picking either would put a
    /// relationship on a person the material never gave it to.</para>
    /// </summary>
    public string? ResolveActor(string? reference) =>
        Resolve(reference, Actors, a => a.Id, NameMatch.Answers);

    /// <summary>
    /// The id of the group a reference names, resolved exactly as an actor's is,
    /// and for the same reason: a group founded this chapter has no id yet.
    /// </summary>
    public string? ResolveGroup(string? reference) =>
        Resolve(reference, Groups, g => g.Id, (g, name) => NameMatch.Same(g.Name, name));

    public string? ResolveThread(string? reference) =>
        Resolve(reference, Threads, t => t.Id, (t, name) => NameMatch.Same(t.Name, name));

    /// <summary>
    /// The id of the place a reference names. Same rule again: a room named in the
    /// same chapter as the house it is in has no id when the model writes it down.
    /// </summary>
    public string? ResolvePlace(string? reference) =>
        Resolve(reference, Places, p => p.Id, (p, name) => p.AllNames.Any(n => NameMatch.Same(n, name)));

    /// <summary>
    /// Id first, then an unambiguous name. Shared because "what does this
    /// reference point at" is one question, and three copies of the answer would
    /// be three chances for one of them to start guessing.
    /// </summary>
    private static string? Resolve<T>(
        string? reference, List<T> among, Func<T, string> id, Func<T, string, bool> answers)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        if (among.FirstOrDefault(item => id(item) == reference) is { } exact) return id(exact);

        var named = among.Where(item => answers(item, reference)).ToArray();

        return named.Length == 1 ? id(named[0]) : null;
    }

    /// <summary>
    /// Puts an actor in a group, on both sides.
    ///
    /// <para>Membership is written in two places — <see cref="Story.Actor.GroupIds"/>
    /// and <see cref="Group.MemberIds"/> — and the cast list reads one while the
    /// filters read the other. One method writes both, so the two can never
    /// disagree about who is in a household.</para>
    /// </summary>
    public void Enrol(string actorId, string groupId)
    {
        if (Actor(actorId) is not { } actor) return;
        if (Groups.FirstOrDefault(g => g.Id == groupId) is not { } group) return;

        Replace(actor with { GroupIds = MergeLists.Ids(actor.GroupIds, [groupId]) });
        Groups[Groups.IndexOf(group)] = group with
        {
            MemberIds = MergeLists.Ids(group.MemberIds, [actorId])
        };
    }

    /// <summary>
    /// Whether the reader has already said this name is not this actor's.
    ///
    /// <para>An answered question stays answered however confident a later chapter
    /// is. This is the only signal in the model that came from a person, and it
    /// outranks every one that came from a model.</para>
    /// </summary>
    public bool WasRefused(string actorId, string alias) =>
        Candidates.Any(c => c.Declined && c.ActorId == actorId && NameMatch.Same(c.Alias, alias));

    public void Replace(Actor actor) => Replace(Actors, a => a.Id == actor.Id, actor);

    public void Replace(StoryThread thread) => Replace(Threads, t => t.Id == thread.Id, thread);

    /// <summary>
    /// Swaps one item for its successor in place.
    /// </summary>
    /// <remarks>
    /// Throws rather than silently doing nothing when the item is not there: every
    /// caller has just read the thing it is replacing, so a miss is a merge rule
    /// that has lost track of its own state, and a lost update to a stored model
    /// is not something a reader could ever notice.
    /// </remarks>
    private static void Replace<T>(List<T> items, Predicate<T> match, T replacement)
    {
        var at = items.FindIndex(match);

        if (at < 0)
            throw new InvalidOperationException(
                $"The story merge tried to replace a {typeof(T).Name} that is no longer in the model.");

        items[at] = replacement;
    }

    /// <summary>
    /// The next free id of a prefix — <c>a3</c>, <c>t7</c>.
    ///
    /// <para>Short because every id travels in the digest on every extraction call,
    /// and sequential because a stable, readable id is what makes a stored model
    /// diffable by a person trying to work out what a merge did.</para>
    /// </summary>
    public string NextId(char prefix, IEnumerable<string> existing)
    {
        var highest = existing
            .Where(id => id.Length > 1 && id[0] == prefix)
            .Select(id => int.TryParse(id[1..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{highest + 1}";
    }

    /// <summary>
    /// Records a question instead of making a change.
    ///
    /// <para>Deduplicated on the actor and the name in question, so a novel that
    /// keeps using an ambiguous name does not produce forty identical questions
    /// for the reader to dismiss one at a time.</para>
    /// </summary>
    public void Ask(string actorId, string? otherActorId, string alias, string reason)
    {
        if (Candidates.Any(c =>
                c.ActorId == actorId && c.OtherActorId == otherActorId &&
                NameMatch.Same(c.Alias, alias)))
            return;

        Candidates.Add(new CandidateMerge(
            NextId('m', Candidates.Select(c => c.Id)), actorId, otherActorId, alias, reason, Chapter));
    }
}
