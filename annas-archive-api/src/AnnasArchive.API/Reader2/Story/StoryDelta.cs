namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// How sure the model is that an alias belongs to an actor it named.
///
/// <para>Only <see cref="High"/> is applied without asking. Everything else is a
/// question for the reader, and <see cref="Low"/> is what an unparseable answer
/// becomes — an unreadable confidence is not a confident one.</para>
/// </summary>
public enum AliasConfidence { Low = 0, Medium = 1, High = 2 }

/// <summary>Somebody this chapter introduced. Ids are ours to assign, never the model's.</summary>
public sealed record NewActor(
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    ActorTier Tier,
    IReadOnlyList<string> GroupIds,
    string Role,
    string Dossier,
    string Status,
    string ArcChange);

/// <summary>
/// A change to somebody already in the model. Every field is optional: the model
/// reports what this chapter altered, and a null means "unchanged", not "empty".
/// </summary>
public sealed record ActorUpdate(
    string ActorId,
    ActorTier? Tier = null,
    string? Role = null,
    string? Dossier = null,
    string? Status = null,
    string? ArcChange = null,
    IReadOnlyList<string>? GroupIds = null,
    IReadOnlyList<string>? Aliases = null);

/// <summary>
/// A name the model thinks belongs to an actor already in the digest.
///
/// <para>A hint, never an instruction. The merger decides, and anything it cannot
/// decide becomes a <see cref="CandidateMerge"/>.</para>
/// </summary>
public sealed record AliasHint(string Alias, string ActorId, AliasConfidence Confidence);

public sealed record NewGroup(
    string Name,
    GroupKind Kind,
    IReadOnlyList<string> MemberIds,
    IReadOnlyList<string> RivalGroupIds);

public sealed record GroupUpdate(
    string GroupId,
    IReadOnlyList<string>? MemberIds = null,
    IReadOnlyList<string>? RivalGroupIds = null);

/// <summary>
/// A relationship starting, changing, or ending.
/// </summary>
/// <param name="Ended">
/// True ends the relationship rather than removing it — the row stays with an
/// <c>EndedChapter</c>, because when an alliance broke is worth more than the
/// fact that one existed.
/// </param>
public sealed record EdgeChange(
    string From,
    string To,
    string Type,
    string Note = "",
    bool Ended = false);

public sealed record NewThread(string Name, IReadOnlyList<string> ParticipantIds, string FirstBeat);

/// <summary>
/// Somewhere this chapter went, or first named.
/// </summary>
/// <param name="PartOf">
/// What contains it, by id or by name. Resolved by the merge like every other
/// reference — a place named for the first time in the same chapter as the city
/// it sits in has no id yet when the model writes this.
/// </param>
public sealed record NewPlace(
    string Name,
    IReadOnlyList<string> Aliases,
    PlaceKind Kind,
    string Description,
    string PartOf = "");

/// <summary>
/// A change to a place already in the model. Every field is optional, on the same
/// rule as <see cref="ActorUpdate"/>: null means unchanged, not empty.
/// </summary>
public sealed record PlaceUpdate(
    string PlaceId,
    PlaceKind? Kind = null,
    string? Description = null,
    string? PartOf = null,
    IReadOnlyList<string>? Aliases = null);

public sealed record ThreadBeat(string ThreadId, string WhatMoved);

/// <summary>
/// What one chapter adds to the story model, as the extraction call reports it.
///
/// <para>Everything here is a <i>proposal</i>. The model reads and suggests;
/// <see cref="StoryModelMerger"/> is pure C# and decides. That division is the
/// whole design: alias discipline is a correctness problem, and correctness
/// problems do not belong to a language model.</para>
/// </summary>
public sealed record StoryDelta(
    int Chapter,
    IReadOnlyList<NewActor> NewActors,
    IReadOnlyList<ActorUpdate> ActorUpdates,
    IReadOnlyList<AliasHint> AliasHints,
    IReadOnlyList<NewGroup> NewGroups,
    IReadOnlyList<GroupUpdate> GroupUpdates,
    IReadOnlyList<EdgeChange> EdgeChanges,
    IReadOnlyList<NewThread> NewThreads,
    IReadOnlyList<ThreadBeat> ThreadBeats,
    IReadOnlyList<NewPlace> NewPlaces,
    IReadOnlyList<PlaceUpdate> PlaceUpdates)
{
    public static StoryDelta Empty(int chapter) =>
        new(chapter, [], [], [], [], [], [], [], [], [], []);
}
