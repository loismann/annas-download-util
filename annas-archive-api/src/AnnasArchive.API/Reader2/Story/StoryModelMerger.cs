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

    private readonly IReadOnlyList<int> _ingested = current.ChaptersIngested;

    public StoryModel Result() => new(
        Actors, Groups, Edges, Threads, Candidates,
        [.. _ingested.Append(Chapter).Distinct().Order()]);

    /// <summary>The actor with this id, or null. Model-supplied ids are not trusted.</summary>
    public Actor? Actor(string? id) =>
        id is null ? null : Actors.FirstOrDefault(a => a.Id == id);

    public bool IsKnownActor(string? id) => Actor(id) is not null;

    /// <summary>
    /// Whether a group with this id exists. Model-supplied group ids are trusted
    /// no further than actor ids are: one that names nothing is a group the model
    /// did not find in the digest.
    /// </summary>
    public bool IsKnownGroup(string? id) =>
        id is not null && Groups.Any(g => g.Id == id);

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
