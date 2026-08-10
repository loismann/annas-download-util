using AnnasArchive.API.Reader2.Story;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// Builders for the story-model tests, so each test states only what it is about.
///
/// <para>Every merge test is "this model, plus this chapter, gives that" — and
/// written out longhand the model and the delta bury the one field the test
/// exists to check.</para>
/// </summary>
public static class Cast
{
    public static Actor Actor(
        string id, string name, ActorTier tier = ActorTier.Secondary,
        int firstSeen = 0, int lastSeen = 0,
        IReadOnlyList<string>? aliases = null, IReadOnlyList<ArcPoint>? arc = null) =>
        new(id, name, aliases ?? [], tier, [], Role: "", Dossier: "",
            firstSeen, lastSeen, Status: "", arc ?? []);

    public static StoryThread StoryThread(
        string id, string name, int started = 0, int lastAdvanced = 0,
        ThreadStatus status = ThreadStatus.Active, IReadOnlyList<Beat>? beats = null) =>
        new(id, name, status, [], started, lastAdvanced, beats ?? [], []);

    public static StoryModel Model(
        IReadOnlyList<Actor>? actors = null,
        IReadOnlyList<StoryThread>? threads = null,
        IReadOnlyList<Edge>? edges = null,
        IReadOnlyList<Group>? groups = null,
        IReadOnlyList<CandidateMerge>? candidates = null,
        IReadOnlyList<int>? ingested = null) =>
        new(actors ?? [], groups ?? [], edges ?? [], threads ?? [], candidates ?? [], ingested ?? []);

    public static NewActor Arriving(
        string name, ActorTier tier = ActorTier.Secondary,
        IReadOnlyList<string>? aliases = null, string arcChange = "") =>
        new(name, aliases ?? [], tier, [], Role: "", Dossier: "", Status: "", arcChange);

    /// <summary>A delta carrying one kind of thing, so a test names only what it changes.</summary>
    public static StoryDelta Delta(
        int chapter,
        IReadOnlyList<NewActor>? newActors = null,
        IReadOnlyList<ActorUpdate>? updates = null,
        IReadOnlyList<AliasHint>? hints = null,
        IReadOnlyList<EdgeChange>? edges = null,
        IReadOnlyList<NewThread>? newThreads = null,
        IReadOnlyList<ThreadBeat>? beats = null,
        IReadOnlyList<NewGroup>? groups = null,
        IReadOnlyList<GroupUpdate>? groupUpdates = null) =>
        new(chapter, newActors ?? [], updates ?? [], hints ?? [], groups ?? [],
            groupUpdates ?? [], edges ?? [], newThreads ?? [], beats ?? []);

    /// <summary>The merge under its shipped thresholds unless a test needs otherwise.</summary>
    public static StoryModel Merge(StoryModel model, StoryDelta delta, StoryMergeRules? rules = null) =>
        StoryModelMerger.Merge(model, delta, rules ?? StoryMergeRules.Default);

    /// <summary>The reading-position filter under those same thresholds.</summary>
    public static StoryModel Through(this StoryModel model, int chapter, StoryMergeRules? rules = null) =>
        model.ThroughChapter(chapter, rules ?? StoryMergeRules.Default);

    /// <summary>How far back the digest reaches for a non-major, as shipped.</summary>
    public const int RecentChapters = 20;

    public static string Digest(StoryModel model, int chapter, int maxActors, int recent = RecentChapters) =>
        StoryDigest.Build(model, chapter, maxActors, recent);

    public static IReadOnlyList<Actor> Kept(
        IReadOnlyList<Actor> actors, int chapter, int maxActors, int recent = RecentChapters) =>
        StoryDigest.Keep(actors, chapter, maxActors, recent);

    public static Actor ById(this StoryModel model, string id) =>
        model.Actors.Single(a => a.Id == id);
}
