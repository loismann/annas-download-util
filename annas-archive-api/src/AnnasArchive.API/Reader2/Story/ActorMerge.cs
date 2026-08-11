namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// Everything the merge decides about people.
///
/// <para>This is where the feature is won or lost. A long novel names one person
/// four ways and two people almost the same way, and the two failures are not
/// symmetric: a duplicate entry is untidy and visible, while a wrong merge is a
/// story the reader has no way to know is wrong. Every rule below leans the same
/// direction because of that.</para>
/// </summary>
internal static class ActorMerge
{
    public static void Apply(MergeState state, StoryDelta delta)
    {
        foreach (var arrival in delta.NewActors) Admit(state, arrival);
        foreach (var update in delta.ActorUpdates) Update(state, update);
        foreach (var hint in delta.AliasHints) Consider(state, hint);
    }

    /// <summary>
    /// Adds somebody new — unless they are somebody already here under another
    /// name, in which case this chapter's report becomes an update.
    ///
    /// <para>The digest gives the model every actor's names, so a duplicate here
    /// means it did not recognise one. Inserting anyway is how a cast list grows a
    /// second Prince Andrew.</para>
    /// </summary>
    private static void Admit(MergeState state, NewActor arrival)
    {
        var names = arrival.Aliases.Prepend(arrival.CanonicalName).ToArray();
        var existing = state.Actors.FirstOrDefault(a => names.Any(n => NameMatch.Answers(a, n)));

        if (existing is not null)
        {
            state.Replace(Touch(existing, state.Chapter) with
            {
                Aliases = MergeLists.Names(
                    existing.Aliases, names.Where(n => !NameMatch.Same(n, existing.CanonicalName))),
                Tier = Retier(existing, arrival.Tier, state),
                Role = Prefer(existing.Role, arrival.Role),
                Dossier = Prefer(existing.Dossier, arrival.Dossier),
                Status = Prefer(existing.Status, arrival.Status),
                Arc = Appended(existing.Arc, state.Chapter, arrival.ArcChange)
            });

            return;
        }

        state.Actors.Add(new Actor(
            Id: state.NextId('a', state.Actors.Select(a => a.Id)),
            CanonicalName: arrival.CanonicalName.Trim(),

            // Filtered against the canonical name for the same reason the branch
            // above filters against it: a model listing somebody's own name among
            // their aliases would otherwise put it in the digest twice, on every
            // chapter, for the life of the book.
            Aliases: MergeLists.Names([], arrival.Aliases.Where(n => !NameMatch.Same(n, arrival.CanonicalName))),
            Tier: arrival.Tier,

            // Empty, and filled by GroupEdgeMerge once the groups exist. A group
            // founded in this same chapter has no id at this point in the merge,
            // so resolving the reference here would silently drop it.
            GroupIds: [],
            Role: arrival.Role,
            Dossier: arrival.Dossier,
            FirstSeenChapter: state.Chapter,
            LastSeenChapter: state.Chapter,
            Status: arrival.Status,
            Arc: Appended([], state.Chapter, arrival.ArcChange)));
    }

    /// <summary>
    /// Applies a change to somebody already here. An unknown id is dropped rather
    /// than invented into an actor: the model does not assign ids, so an id we do
    /// not recognise is a mistake, not a new person.
    /// </summary>
    private static void Update(MergeState state, ActorUpdate update)
    {
        if (state.Actor(state.ResolveActor(update.ActorId)) is not { } actor) return;

        state.Replace(Touch(actor, state.Chapter) with
        {
            Tier = update.Tier is { } tier ? Retier(actor, tier, state) : actor.Tier,
            Role = Prefer(actor.Role, update.Role),
            Dossier = Prefer(actor.Dossier, update.Dossier),
            Status = update.Status is { Length: > 0 } status ? status : actor.Status,
            Aliases = MergeLists.Names(
                actor.Aliases, (update.Aliases ?? []).Where(n => !NameMatch.Same(n, actor.CanonicalName))),
            Arc = Appended(actor.Arc, state.Chapter, update.ArcChange)
        });
    }

    /// <summary>
    /// Decides what to do with a name the model thinks belongs to somebody.
    ///
    /// <para>Four ways this becomes something other than a change: the reader has
    /// already said no to it, the name already belongs to a different actor, the
    /// model was not certain, or the target does not exist. Only an unambiguous,
    /// high-confidence hint nobody has refused is applied — which is the whole of
    /// "never auto-merge".</para>
    /// </summary>
    private static void Consider(MergeState state, AliasHint hint)
    {
        if (string.IsNullOrWhiteSpace(hint.Alias)) return;
        if (state.Actor(state.ResolveActor(hint.ActorId)) is not { } target) return;
        if (NameMatch.Answers(target, hint.Alias)) return;

        // A refusal outranks any confidence the model reports. Without this the
        // same hint arriving one tier more confident in a later chapter applies
        // the alias anyway, and the reader's answer is overwritten by the thing
        // they were asked to correct.
        if (state.WasRefused(target.Id, hint.Alias)) return;

        var rival = state.Actors.FirstOrDefault(a =>
            a.Id != target.Id && NameMatch.Answers(a, hint.Alias));

        if (rival is not null)
        {
            state.Ask(target.Id, rival.Id, hint.Alias, rival.Arc.Count > 0
                ? $"'{hint.Alias}' already belongs to {rival.CanonicalName}, who has their own story so far."
                : $"'{hint.Alias}' already belongs to {rival.CanonicalName}.");

            return;
        }

        if (hint.Confidence != AliasConfidence.High)
        {
            state.Ask(target.Id, null, hint.Alias,
                $"The extraction was not certain that '{hint.Alias}' is {target.CanonicalName}.");

            return;
        }

        state.Replace(target with { Aliases = MergeLists.Names(target.Aliases, [hint.Alias]) });
    }

    /// <summary>
    /// The tier to keep. Promotion is immediate; demotion waits for real absence.
    ///
    /// <para>A protagonist has quiet chapters, and a model reading one chapter at a
    /// time will call them minor in every one of them. Absence is measured from
    /// the last chapter they were seen in <i>before</i> this one, which is why
    /// this is computed before the actor is touched.</para>
    /// </summary>
    private static ActorTier Retier(Actor actor, ActorTier proposed, MergeState state) =>
        proposed > actor.Tier || state.Chapter - actor.LastSeenChapter >= state.Rules.TierDemotionAfterChapters
            ? proposed
            : actor.Tier;

    /// <summary>Being named in a chapter is being seen in it. Never moves backwards.</summary>
    private static Actor Touch(Actor actor, int chapter) =>
        actor with
        {
            LastSeenChapter = Math.Max(actor.LastSeenChapter, chapter),
            FirstSeenChapter = Math.Min(actor.FirstSeenChapter, chapter)
        };

    /// <summary>
    /// History, appended and chapter-tagged. Deduplicated on the pair, so
    /// re-reading a chapter that says the same thing adds nothing.
    /// </summary>
    private static IReadOnlyList<ArcPoint> Appended(
        IReadOnlyList<ArcPoint> arc, int chapter, string? change)
    {
        if (string.IsNullOrWhiteSpace(change)) return arc;

        var point = new ArcPoint(chapter, change.Trim());

        return arc.Contains(point) ? arc : [.. arc, point];
    }

    /// <summary>
    /// A later description replaces an earlier one; an empty one never does.
    /// A chapter with nothing to say about somebody's role must not erase it.
    /// </summary>
    private static string Prefer(string existing, string? offered) =>
        string.IsNullOrWhiteSpace(offered) ? existing : offered.Trim();
}
