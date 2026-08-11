using AnnasArchive.API.Reader2.Ai;

namespace AnnasArchive.API.Reader2.Story;

/// <summary>One chapter-tagged change in somebody's arc.</summary>
public sealed record ArcPoint(int Chapter, string Change);

/// <summary>One chapter-tagged movement in a thread.</summary>
public sealed record Beat(int Chapter, string WhatMoved);

/// <summary>One chapter-tagged thing that passed between two actors.</summary>
public sealed record EdgeNote(int Chapter, string What);

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
    IReadOnlyList<ArcPoint> Arc,

    /// <summary>
    /// The reader's own words about this person.
    ///
    /// <para>Written only by <see cref="CastCorrections"/> and never by the merge:
    /// extraction has no business editing what a person wrote, and nothing here
    /// is ever sent to a model. Defaulted, so a model stored before it existed
    /// loads with an empty note — which is the truth about it.</para>
    /// </summary>
    string ReaderNote = "",

    /// <summary>
    /// Kept off the map, at the reader's word.
    ///
    /// <para>Projected by <see cref="CastCorrections"/> from what the reader
    /// stored, never written by extraction — so it survives a rebuild, and so a
    /// walk-on hidden in chapter three stays hidden when chapter forty mentions
    /// them again. They remain in the cast list, marked, because the extraction
    /// did find them and a record that silently forgot people would be worth
    /// less than one that is merely crowded.</para>
    /// </summary>
    bool Hidden = false)
{
    /// <summary>Every name this actor answers to, canonical first.</summary>
    public IEnumerable<string> AllNames => Aliases.Prepend(CanonicalName);
}

/// <summary>
/// Somewhere the book goes.
/// </summary>
/// <param name="PartOf">
/// The place this one sits inside, as an id — a room in a house, a house in a
/// city, a city in a realm. Empty when nothing contains it, or when the book has
/// not said. Held rather than inferred from the prose, because "where is this"
/// is the question a reader asks about a name they half-remember, and a flat list
/// of ninety names cannot answer it.
/// </param>
/// <param name="Aliases">
/// The same discipline as <see cref="Actor.Aliases"/>, and for the same reason:
/// a book that calls one city three things will otherwise be recorded as having
/// three cities.
/// </param>
public sealed record Place(
    string Id,
    string Name,
    IReadOnlyList<string> Aliases,
    PlaceKind Kind,
    string Description,
    string PartOf,
    int FirstSeenChapter,
    int LastSeenChapter)
{
    /// <summary>Every name this place answers to, canonical first.</summary>
    public IEnumerable<string> AllNames => Aliases.Prepend(Name);
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
/// <param name="Notes">
/// What has passed between the two, append-only and chapter-tagged, on the same
/// rule as an actor's arc and a thread's beats.
///
/// <para>This was one overwritten string. The chapter that made two people allies
/// and the chapter that strained it are both the answer to "how do these two know
/// each other", and keeping only the latest meant the record could describe a
/// relationship it could not account for. Nothing here is ever rewritten, so the
/// reading-position filter can serve the part of it the reader has reached.</para>
/// </param>
public sealed record Edge(
    string From,
    string To,
    string Type,
    int SinceChapter,
    int? EndedChapter,
    IReadOnlyList<EdgeNote> Notes)
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
    IReadOnlyList<int> ChaptersIngested,
    IReadOnlyList<Place> Places) : IVersionedArtifact<StoryModel>
{
    /// <summary>
    /// 4 since <see cref="Places"/>. A stored model written before it has no
    /// places in it and nothing to upcast them from — the prose they would be
    /// read out of is a chapter summary that was not asked to name any.
    ///
    /// <para>A bump empties the stored model, which is why it is not done for a
    /// wording change. Here it is unavoidable and honest: the record genuinely
    /// cannot answer the question the new tab asks of it, and a rebuild reads
    /// every summarised chapter again for no new summaries.</para>
    /// </summary>
    public static int SchemaVersion => 4;

    public static StoryModel Empty { get; } = new([], [], [], [], [], [], []);

    public bool HasIngested(int chapter) => ChaptersIngested.Contains(chapter);

}
