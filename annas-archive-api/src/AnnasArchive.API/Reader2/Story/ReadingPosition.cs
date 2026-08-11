namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// The model as it stood when the reader reached a given chapter.
///
/// <para><b>The model is cumulative and the reader is not.</b> Every read goes
/// through here and nothing else is served — without it, opening the cast list in
/// chapter three of a novel tells you who dies in chapter forty.</para>
///
/// <para>Apart from <see cref="StoryModel"/> itself because it is a different
/// question. That record says what is known; this says what may be shown, which
/// is a rule about the reader rather than about the book.</para>
/// </summary>
public static class ReadingPosition
{
    /// <summary>
    /// The model as it stood when the reader reached <paramref name="chapter"/>.
    ///
    /// <para>The model is cumulative and the reader is not. Without this, opening
    /// the cast list in chapter three of a novel tells you who dies in chapter
    /// forty — so every read goes through here, and nothing else is served.</para>
    ///
    /// <para>Actors first seen ahead of the reader disappear entirely rather than
    /// appearing empty, and every edge, membership, and group that pointed at them
    /// is dropped with them: a character who is hidden but still listed as
    /// somebody's husband has not been hidden.</para>
    /// </summary>
    /// <param name="rules">
    /// The same thresholds the merge runs under, because a thread's status is
    /// recomputed here rather than taken from storage — see <see cref="Trim"/>.
    /// </param>
    public static StoryModel ThroughChapter(
        this StoryModel model, int chapter, StoryMergeRules rules)
    {
        var visible = model.Actors
            .Where(a => a.FirstSeenChapter <= chapter)
            .Select(a => a with
            {
                LastSeenChapter = Math.Min(a.LastSeenChapter, chapter),
                Arc = [.. a.Arc.Where(p => p.Chapter <= chapter)]
            })
            .ToArray();

        var known = visible.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        return new StoryModel(
            visible,
            [.. model.Groups
                .Where(g => g.FirstSeenChapter <= chapter)
                .Select(g => g with { MemberIds = [.. g.MemberIds.Where(known.Contains)] })],
            [.. model.Edges.Where(e =>
                known.Contains(e.From) && known.Contains(e.To) && e.SinceChapter <= chapter)
                .Select(e => (e.EndedChapter > chapter ? e with { EndedChapter = null } : e) with
                {
                    Notes = [.. e.Notes.Where(n => n.Chapter <= chapter)]
                })],
            [.. model.Threads.Where(t => t.StartedChapter <= chapter).Select(t => Trim(t, chapter, known, rules))],
            [.. model.CandidateMerges.Where(m =>
                m.ProposedInChapter <= chapter && known.Contains(m.ActorId) &&
                (m.OtherActorId is null || known.Contains(m.OtherActorId)))],
            [.. model.ChaptersIngested.Where(c => c <= chapter)],
            Reached(model, chapter));
    }

    /// <summary>
    /// The places the reader has reached.
    ///
    /// <para>A containing place the reader has not met is cleared rather than
    /// left dangling, on the same rule as an edge to somebody unmet: telling
    /// somebody that an inn is in a city they have never heard of is telling them
    /// about the city.</para>
    /// </summary>
    private static IReadOnlyList<Place> Reached(StoryModel model, int chapter)
    {
        var visible = model.Places.Where(p => p.FirstSeenChapter <= chapter).ToArray();
        var known = visible.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        return [.. visible.Select(p => p with
        {
            LastSeenChapter = Math.Min(p.LastSeenChapter, chapter),
            PartOf = known.Contains(p.PartOf) ? p.PartOf : ""
        })];
    }

    /// <summary>
    /// A thread as it stood at <paramref name="chapter"/>.
    ///
    /// <para>Its status is recomputed from the beats that are visible rather than
    /// read from storage, because the stored status is the one it reached
    /// <i>latest</i>. A thread resolved in chapter fifty is still running as far as
    /// a reader in chapter ten is concerned — and a thread that lay dormant at
    /// chapter thirty and was revived at forty-five must read as dormant at thirty,
    /// or the reader has been told it comes back.</para>
    /// </summary>
    private static StoryThread Trim(
        StoryThread thread, int chapter, IReadOnlySet<string> known, StoryMergeRules rules)
    {
        var beats = thread.Beats.Where(b => b.Chapter <= chapter).ToArray();

        var lastAdvanced = beats.Length > 0
            ? beats.Max(b => b.Chapter)
            : Math.Min(thread.LastAdvancedChapter, chapter);

        // Finished later means unfinished now; unfinished and quiet for long
        // enough means dormant, on the same threshold the merge sweeps by.
        var ended = thread.Status is ThreadStatus.Resolved or ThreadStatus.Abandoned;
        var status = ended && thread.LastAdvancedChapter <= chapter
            ? thread.Status
            : chapter - lastAdvanced >= rules.ThreadDormantAfterChapters
                ? ThreadStatus.Dormant
                : ThreadStatus.Active;

        var returned = thread.ReturnedInChapter <= chapter;

        return thread with
        {
            Beats = beats,
            ParticipantIds = [.. thread.ParticipantIds.Where(known.Contains)],
            LastAdvancedChapter = lastAdvanced,
            Status = status,
            ReturnedInChapter = returned ? thread.ReturnedInChapter : null,
            ReturnedAfterChapters = returned ? thread.ReturnedAfterChapters : null
        };
    }
}
