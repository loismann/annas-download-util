namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// Answering one of the merger's questions.
///
/// <para><b>The only path by which an actor is ever removed.</b> Extraction never
/// deletes anybody — that is the rule that stops a cast list quietly losing people
/// — so fusing two entries requires somebody to look at them and say they are one
/// person. Pure C#, like the merge itself.</para>
/// </summary>
public static class MergeResolution
{
    /// <summary>
    /// Applies the reader's answer to one candidate.
    /// </summary>
    /// <param name="accept">
    /// True fuses the two, or adds the alias when there is only one actor in
    /// question. False marks the question answered so it is not asked again.
    /// </param>
    /// <returns>The model, unchanged if there is no such open question.</returns>
    public static StoryModel Resolve(StoryModel model, string mergeId, bool accept)
    {
        if (model.CandidateMerges.FirstOrDefault(m => m.Id == mergeId && !m.Declined) is not { } candidate)
            return model;

        var answered = model.CandidateMerges
            .Select(m => m.Id == mergeId ? m with { Declined = true } : m)
            .ToArray();

        if (!accept) return model with { CandidateMerges = answered };

        var settled = model with { CandidateMerges = answered };

        return candidate.OtherActorId is null
            ? AddAlias(settled, candidate)
            : Fuse(settled, candidate);
    }

    /// <summary>The simple answer: yes, that name is theirs.</summary>
    private static StoryModel AddAlias(StoryModel model, CandidateMerge candidate) =>
        model.Actors.FirstOrDefault(a => a.Id == candidate.ActorId) is not { } actor
            ? model
            : model with
            {
                Actors = [.. model.Actors.Select(a => a.Id != actor.Id
                    ? a
                    : a with { Aliases = MergeLists.Names(a.Aliases, [candidate.Alias]) })]
            };

    private static StoryModel Fuse(StoryModel model, CandidateMerge candidate) =>
        Fuse(model, candidate.ActorId, candidate.OtherActorId ?? "");

    /// <summary>
    /// Two entries become one. The kept actor absorbs everything the other had
    /// and every reference to the other is repointed, because a fused actor whose
    /// edges still name a deleted id is worse than the duplicate was.
    /// </summary>
    /// <remarks>
    /// Public because the reader can now say two entries are one person directly,
    /// without the merger having raised the question. That is the same operation
    /// and must not become a second implementation of it — fusing is exactly where
    /// two spellings of one person meet, and two rules for it would disagree.
    /// </remarks>
    public static StoryModel Fuse(StoryModel model, string keepId, string goneId)
    {
        var keep = model.Actors.FirstOrDefault(a => a.Id == keepId);
        var absorbed = model.Actors.FirstOrDefault(a => a.Id == goneId);

        if (keep is null || absorbed is null || keep.Id == absorbed.Id) return model;

        var fused = keep with
        {
            // The merge's own rule, not a second one: fusing is exactly where two
            // spellings of one person meet, so "Bezúkhov" must not survive next to
            // "Bezukhov" in the list the fuse existed to tidy.
            Aliases = MergeLists.Names(
                keep.Aliases,
                absorbed.AllNames.Where(n => !NameMatch.Same(n, keep.CanonicalName))),
            Tier = (ActorTier)Math.Max((int)keep.Tier, (int)absorbed.Tier),
            GroupIds = MergeLists.Ids(keep.GroupIds, absorbed.GroupIds),
            Role = keep.Role.Length > 0 ? keep.Role : absorbed.Role,
            Dossier = keep.Dossier.Length > 0 ? keep.Dossier : absorbed.Dossier,
            Status = keep.Status.Length > 0 ? keep.Status : absorbed.Status,
            FirstSeenChapter = Math.Min(keep.FirstSeenChapter, absorbed.FirstSeenChapter),
            LastSeenChapter = Math.Max(keep.LastSeenChapter, absorbed.LastSeenChapter),

            // Both histories, in chapter order, deduplicated on the pair — the same
            // append-only rule the merge follows, applied to two lists at once.
            Arc = [.. keep.Arc.Concat(absorbed.Arc).Distinct().OrderBy(p => p.Chapter)]
        };

        var gone = absorbed.Id;

        return model with
        {
            Actors = [.. model.Actors.Where(a => a.Id != gone).Select(a => a.Id == fused.Id ? fused : a)],
            Groups = [.. model.Groups.Select(g => g with { MemberIds = Repoint(g.MemberIds, gone, fused.Id) })],
            Edges = Repoint(model.Edges, gone, fused.Id),
            Threads = [.. model.Threads.Select(t =>
                t with { ParticipantIds = Repoint(t.ParticipantIds, gone, fused.Id) })],
            CandidateMerges = [.. model.CandidateMerges
                .Where(m => m.ActorId != gone && m.OtherActorId != gone)]
        };
    }

    private static IReadOnlyList<string> Repoint(IReadOnlyList<string> ids, string gone, string kept) =>
        [.. ids.Select(id => id == gone ? kept : id).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Edges repointed at the surviving actor, with the self-edges that repointing
    /// creates dropped: if the two entries were one person, "X allied with X" was
    /// never a relationship.
    /// </summary>
    private static IReadOnlyList<Edge> Repoint(IReadOnlyList<Edge> edges, string gone, string kept)
    {
        var moved = edges
            .Select(e => e with
            {
                From = e.From == gone ? kept : e.From,
                To = e.To == gone ? kept : e.To
            })
            .Where(e => e.From != e.To);

        return [.. moved
            .GroupBy(e => e.Identity)
            .Select(g => g.Aggregate((a, b) => a with
            {
                SinceChapter = Math.Min(a.SinceChapter, b.SinceChapter),

                // Still running under either entry means still running. An ended
                // edge and a live one for the same pair are the same relationship
                // seen twice, and it did not end.
                EndedChapter = a.EndedChapter is null || b.EndedChapter is null
                    ? null
                    : Math.Max(a.EndedChapter.Value, b.EndedChapter.Value),

                // Both histories, in chapter order. Two entries for one pair are
                // one relationship recorded twice, and keeping whichever half had
                // something to say would throw away the other half of it.
                Notes = [.. a.Notes.Union(b.Notes).OrderBy(n => n.Chapter)]
            }))];
    }
}
