namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// Everything the merge decides about plot threads, and the dormancy sweep.
///
/// <para>Dormancy is the mechanism behind "we have not seen Dólokhov since
/// chapter 61". It is derived from the beats rather than asserted by the model,
/// because a model reading chapter 74 has no idea what chapter 61 contained —
/// and that is exactly the question the reader has.</para>
/// </summary>
internal static class ThreadMerge
{
    public static void Apply(MergeState state, StoryDelta delta)
    {
        foreach (var arrival in delta.NewThreads) Open(state, arrival);
        foreach (var beat in delta.ThreadBeats) Advance(state, beat);

        Sweep(state);
    }

    /// <summary>
    /// Starts a thread, unless one of that name is already running — the model
    /// sees only names in the digest and will re-propose a thread it has already
    /// opened under slightly different wording.
    /// </summary>
    private static void Open(MergeState state, NewThread arrival)
    {
        if (string.IsNullOrWhiteSpace(arrival.Name)) return;

        if (state.Threads.FirstOrDefault(t => NameMatch.Same(t.Name, arrival.Name)) is { } existing)
        {
            Advance(state, existing, arrival.FirstBeat);
            return;
        }

        state.Threads.Add(new StoryThread(
            Id: state.NextId('t', state.Threads.Select(t => t.Id)),
            Name: arrival.Name.Trim(),
            Status: ThreadStatus.Active,
            ParticipantIds: [.. arrival.ParticipantIds
                .Select(state.ResolveActor)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)],
            StartedChapter: state.Chapter,
            LastAdvancedChapter: state.Chapter,
            Beats: Appended([], state.Chapter, arrival.FirstBeat),
            RelatedThreads: []));
    }

    private static void Advance(MergeState state, ThreadBeat beat)
    {
        if (state.ResolveThread(beat.ThreadId) is not { } id) return;

        if (state.Threads.FirstOrDefault(t => t.Id == id) is { } thread)
            Advance(state, thread, beat.WhatMoved);
    }

    /// <summary>
    /// Records a movement. A thread that had gone dormant wakes up here, and how
    /// long it was gone is kept — that gap is the fact worth telling the reader.
    /// </summary>
    private static void Advance(MergeState state, StoryThread thread, string? whatMoved)
    {
        var beats = Appended(thread.Beats, state.Chapter, whatMoved);
        var returning = thread.Status == ThreadStatus.Dormant;

        state.Replace(thread with
        {
            Beats = beats,
            Status = ThreadStatus.Active,
            LastAdvancedChapter = Math.Max(thread.LastAdvancedChapter, state.Chapter),
            ReturnedInChapter = returning ? state.Chapter : thread.ReturnedInChapter,
            ReturnedAfterChapters = returning
                ? state.Chapter - thread.LastAdvancedChapter
                : thread.ReturnedAfterChapters
        });
    }

    /// <summary>
    /// Marks every thread that has gone quiet for long enough.
    ///
    /// <para>Runs after the beats, so a thread advanced by this very chapter is
    /// never swept — and only touches active threads, because a resolved thread is
    /// finished rather than dormant and saying otherwise would promise a return
    /// that is not coming.</para>
    /// </summary>
    private static void Sweep(MergeState state)
    {
        foreach (var thread in state.Threads.ToArray())
            if (thread.Status == ThreadStatus.Active &&
                state.Chapter - thread.LastAdvancedChapter >= state.Rules.ThreadDormantAfterChapters)
                state.Replace(thread with { Status = ThreadStatus.Dormant });
    }

    /// <summary>Append-only, chapter-tagged, deduplicated on the pair.</summary>
    private static IReadOnlyList<Beat> Appended(IReadOnlyList<Beat> beats, int chapter, string? whatMoved)
    {
        if (string.IsNullOrWhiteSpace(whatMoved)) return beats;

        var beat = new Beat(chapter, whatMoved.Trim());

        return beats.Contains(beat) ? beats : [.. beats, beat];
    }
}
