using AnnasArchive.API.Reader2.Ai;

namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// How much of the story somebody is. Ordered by importance so that "promotes
/// freely, demotes slowly" is a comparison rather than a table.
/// </summary>
public enum ActorTier { Mentioned = 0, Minor = 1, Secondary = 2, Major = 3 }

/// <summary>What kind of thing a group is. The same set serves both story lenses.</summary>
public enum GroupKind { Family, Household, MilitaryUnit, SocialCircle, PoliticalFaction, Other }

public enum ThreadStatus { Active, Dormant, Resolved, Abandoned }

/// <summary>One chapter-tagged change in somebody's arc.</summary>
public sealed record ArcPoint(int Chapter, string Change);

/// <summary>One chapter-tagged movement in a thread.</summary>
public sealed record Beat(int Chapter, string WhatMoved);

/// <summary>A named link between two threads — "mirrors", "caused-by", "converges-with".</summary>
public sealed record RelatedThread(string ThreadId, string Relation);

/// <summary>
/// A character, or a commander or formation under the military lens.
/// </summary>
/// <param name="Aliases">
/// Every other name this person has appeared under. This is the field the whole
/// feature turns on: <i>Pierre</i>, <i>Pyotr Kirillovich</i>, and <i>Count
/// Bezukhov</i> are one actor, and a model that regenerates the cast each time
/// cannot know that.
/// </param>
/// <param name="Arc">Append-only and chapter-tagged. Nothing here is ever rewritten.</param>
public sealed record Actor(
    string Id,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    ActorTier Tier,
    IReadOnlyList<string> GroupIds,
    string Role,
    string Dossier,
    int FirstSeenChapter,
    int LastSeenChapter,
    string Status,
    IReadOnlyList<ArcPoint> Arc)
{
    /// <summary>Every name this actor answers to, canonical first.</summary>
    public IEnumerable<string> AllNames => Aliases.Prepend(CanonicalName);
}

/// <summary>A family, a household, a faction, or a formation.</summary>
/// <param name="FirstSeenChapter">
/// The chapter this group was first reported in. Carried for the same reason an
/// actor carries one: without it the reading-position filter has nothing to
/// filter on, and every faction in the book — which in a thriller is most of the
/// plot — is readable from chapter one.
/// </param>
public sealed record Group(
    string Id,
    string Name,
    GroupKind Kind,
    IReadOnlyList<string> MemberIds,
    IReadOnlyList<string> RivalGroupIds,
    int FirstSeenChapter = 0);

/// <summary>
/// A relationship between two actors.
/// </summary>
/// <param name="EndedChapter">
/// Set when the relationship ends. The edge is kept rather than deleted, because
/// "they were allies until chapter 40" is the interesting fact and deleting the
/// edge would leave the model asserting they never were.
/// </param>
public sealed record Edge(
    string From,
    string To,
    string Type,
    int SinceChapter,
    int? EndedChapter,
    string Note)
{
    /// <summary>Identity. Two actors can be related in more than one way at once.</summary>
    public (string From, string To, string Type) Identity => (From, To, Type);
}

/// <summary>
/// A strand of plot, or an operation.
/// </summary>
/// <param name="ReturnedInChapter">
/// When a dormant thread last picked up again, with <paramref name="ReturnedAfterChapters"/>
/// saying how long the gap was. This is what lets the reader be told "this has
/// not moved since chapter 61" instead of being handed a name they last saw
/// three hundred pages ago.
/// </param>
public sealed record StoryThread(
    string Id,
    string Name,
    ThreadStatus Status,
    IReadOnlyList<string> ParticipantIds,
    int StartedChapter,
    int LastAdvancedChapter,
    IReadOnlyList<Beat> Beats,
    IReadOnlyList<RelatedThread> RelatedThreads,
    int? ReturnedInChapter = null,
    int? ReturnedAfterChapters = null);

/// <summary>
/// Something the merger would not decide on its own, waiting for somebody to say.
///
/// <para>Silently merging two characters is worse than showing two entries: the
/// first is a wrong story the reader cannot see, the second is an untidy list
/// they can fix in a click. Everything the merger is not sure of lands here.</para>
/// </summary>
/// <param name="OtherActorId">
/// The actor this one might be the same person as, or null when the question is
/// only whether <paramref name="Alias"/> belongs to <paramref name="ActorId"/> at
/// all. Both are one-click answers; they are different questions.
/// </param>
/// <param name="Declined">
/// Set when the reader has said no. The row stays rather than being deleted, so
/// the next chapter that raises the same ambiguity finds it already answered —
/// otherwise a novel that keeps using a contested name would ask again every
/// twenty pages, and the reader would learn to dismiss without reading.
/// </param>
public sealed record CandidateMerge(
    string Id,
    string ActorId,
    string? OtherActorId,
    string Alias,
    string Reason,
    int ProposedInChapter,
    bool Declined = false);

/// <summary>
/// Everything known about a book's cast, accumulated one chapter at a time.
///
/// <para>Reader I rebuilt this from every summary in the book on each open, capped
/// at 5-15 characters, and silently dropped whoever did not fit. This is stored
/// as one artifact per (book, lens) and merged incrementally, so cost scales with
/// the number of named entities rather than with chapters read.</para>
/// </summary>
/// <param name="ChaptersIngested">
/// Which chapters have been folded in. Makes ingestion idempotent and a back-fill
/// resumable — and it is the only thing that does, since the merge itself is not
/// reversible.
/// </param>
public sealed record StoryModel(
    IReadOnlyList<Actor> Actors,
    IReadOnlyList<Group> Groups,
    IReadOnlyList<Edge> Edges,
    IReadOnlyList<StoryThread> Threads,
    IReadOnlyList<CandidateMerge> CandidateMerges,
    IReadOnlyList<int> ChaptersIngested) : IVersionedArtifact<StoryModel>
{
    /// <summary>
    /// 2 since <see cref="Group.FirstSeenChapter"/> — a stored model written
    /// before it has no chapter to filter groups on, and serving one would put
    /// every faction in the book in front of a reader in chapter three.
    /// </summary>
    public static int SchemaVersion => 2;

    public static StoryModel Empty { get; } = new([], [], [], [], [], []);

    public bool HasIngested(int chapter) => ChaptersIngested.Contains(chapter);

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
    public StoryModel ThroughChapter(int chapter, StoryMergeRules rules)
    {
        var visible = Actors
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
            [.. Groups
                .Where(g => g.FirstSeenChapter <= chapter)
                .Select(g => g with { MemberIds = [.. g.MemberIds.Where(known.Contains)] })],
            [.. Edges.Where(e =>
                known.Contains(e.From) && known.Contains(e.To) && e.SinceChapter <= chapter)
                .Select(e => e.EndedChapter > chapter ? e with { EndedChapter = null } : e)],
            [.. Threads.Where(t => t.StartedChapter <= chapter).Select(t => Trim(t, chapter, known, rules))],
            [.. CandidateMerges.Where(m =>
                m.ProposedInChapter <= chapter && known.Contains(m.ActorId) &&
                (m.OtherActorId is null || known.Contains(m.OtherActorId)))],
            [.. ChaptersIngested.Where(c => c <= chapter)]);
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
