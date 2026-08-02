using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

/// <summary>
/// Every action the assistant is allowed to take in the read-only phase.
///
/// The enum is the authority, not the prompt. The model returns a string, this
/// maps it, and anything unrecognised becomes <see cref="Unknown"/> — so a model
/// that invents <c>delete_everything</c> produces a polite refusal instead of a
/// dispatch. Write actions are deliberately absent: they cannot be reached by
/// wording a sentence differently, only by building the reviewed plan flow.
/// </summary>
public enum SpotifyReadAction
{
    Unknown,
    SearchTracks,
    ListPlaylists,
    FindPlaylists,
    InspectPlaylist,
    ListPlaylistItems,
    FindItemInPlaylists,
    AnalyzeLibrary,
    ComparePlaylists,
    GetTopItems,
    GetRecentPlaylistContexts,
    GetKnownMusic,
    SuggestMusic,
    RefineMusicDraft,
    CompareDraftToKnownMusic,
    PlanCreatePlaylist,
    PlanAddItems,
    PlanRenamePlaylist,
    PlanRemoveItems,
    PlanMergePlaylists,
    PlanRemovePlaylistsFromLibrary,
    ExplainCapability
}

public sealed record SpotifyActionDefinition(
    SpotifyReadAction Action,
    string WireName,
    string PromptDescription,
    bool RequiresQuery = false,
    bool RequiresPlaylistReference = false,
    /// <summary>Needs two or more named playlists — merge cannot mean one thing.</summary>
    bool RequiresPlaylistReferences = false
);

/// <summary>
/// One table describing every action: its wire name, what the model is told about
/// it, and which arguments it cannot run without. The prompt is generated from
/// this table, so an action can never be advertised to the model without also
/// being dispatchable, and vice versa.
/// </summary>
public static class SpotifyActionCatalog
{
    public const int SchemaVersion = 1;

    private static readonly SpotifyActionDefinition[] Definitions =
    [
        new(SpotifyReadAction.SearchTracks, "search_tracks",
            "Search Spotify's catalog for songs. Put the search text in arguments.query.",
            RequiresQuery: true),

        new(SpotifyReadAction.ListPlaylists, "list_playlists",
            "List all of the user's playlists."),

        new(SpotifyReadAction.FindPlaylists, "find_playlists",
            "List only playlists whose name matches something. Put the name fragment in arguments.query.",
            RequiresQuery: true),

        new(SpotifyReadAction.InspectPlaylist, "inspect_playlist",
            "Describe one playlist: owner, visibility, how many items, whether its contents are readable. "
            + "Put the playlist name exactly as the user said it in arguments.playlistReference.",
            RequiresPlaylistReference: true),

        new(SpotifyReadAction.ListPlaylistItems, "list_playlist_items",
            "List the songs or episodes inside one playlist. "
            + "Put the playlist name exactly as the user said it in arguments.playlistReference.",
            RequiresPlaylistReference: true),

        new(SpotifyReadAction.FindItemInPlaylists, "find_item_in_playlists",
            "Find which playlists contain a particular song. Put the song title, and the "
            + "artist if the user gave one, in arguments.query.",
            RequiresQuery: true),

        new(SpotifyReadAction.AnalyzeLibrary, "analyze_playlist_library",
            "Scan the whole library for cleanup opportunities: empty playlists, duplicate "
            + "songs within a playlist, playlists that are near-copies of each other, and "
            + "confusingly similar names. Use for 'find duplicates', 'what can I clean up', "
            + "'which playlists are empty'."),

        new(SpotifyReadAction.ComparePlaylists, "compare_playlists",
            "Compare two named playlists and report what they share. Put both names in "
            + "arguments.query separated by ' and '.",
            RequiresQuery: true),

        new(SpotifyReadAction.GetTopItems, "get_top_items",
            "The user's most-played tracks or artists. Put 'artists' or 'tracks' in "
            + "arguments.query, and if they asked about a period put 'short_term' (4 weeks), "
            + "'medium_term' (6 months) or 'long_term' (years) in arguments.timeRange."),

        new(SpotifyReadAction.GetRecentPlaylistContexts, "get_recent_playlist_contexts",
            "Which playlists appear most often in recent listening history."),

        new(SpotifyReadAction.GetKnownMusic, "get_known_music",
            "Summarize what artists and tracks appear in the user's accessible playlists, top-item windows, and recent history."),

        new(SpotifyReadAction.SuggestMusic, "suggest_music",
            "Start a new editable music-discovery draft from a historical theme, place, era, genre, mood, or requested artists. "
            + "Put the user's complete musical idea in arguments.query.", RequiresQuery: true),

        new(SpotifyReadAction.RefineMusicDraft, "refine_music_draft",
            "Refine the active music-discovery draft, such as more gospel, less country, or lesser-known artists. "
            + "Put the user's refinement in arguments.query.", RequiresQuery: true),

        new(SpotifyReadAction.CompareDraftToKnownMusic, "compare_draft_to_known_music",
            "Explain which candidates in the active discovery draft are or are not represented in accessible known-music evidence."),

        // Plan-producing actions. These never write — they build a reviewable plan
        // that the user confirms separately, so wording a sentence differently can
        // never cause a change on its own.
        new(SpotifyReadAction.PlanCreatePlaylist, "plan_create_playlist",
            "The user wants to actually create the playlist from the current discovery draft. "
            + "Put any name they gave in arguments.query."),

        new(SpotifyReadAction.PlanAddItems, "plan_add_items",
            "Add the current draft's tracks to an existing playlist. Put the playlist name in "
            + "arguments.playlistReference.",
            RequiresPlaylistReference: true),

        new(SpotifyReadAction.PlanRenamePlaylist, "plan_rename_playlist",
            "Rename a playlist. Put the current name in arguments.playlistReference and the new "
            + "name in arguments.query.",
            RequiresPlaylistReference: true, RequiresQuery: true),

        new(SpotifyReadAction.PlanRemoveItems, "plan_remove_items",
            "Remove songs from a playlist — for example the duplicates found by an analysis. Put "
            + "the playlist name in arguments.playlistReference.",
            RequiresPlaylistReference: true),

        // Phase 8. Both need a list of names, which the model copies from the user's
        // own words into arguments.playlistReferences. It supplies no IDs and makes
        // no selection of its own — "clean up whatever you think" resolves to
        // nothing and is refused.
        new(SpotifyReadAction.PlanMergePlaylists, "plan_merge_playlists",
            "Combine several playlists into one. Put every playlist name the user listed in "
            + "arguments.playlistReferences, and the name they want for the combined playlist in "
            + "arguments.query. Set arguments.removeSources to true ONLY if they explicitly said to "
            + "get rid of the originals afterwards.",
            RequiresPlaylistReferences: true),

        new(SpotifyReadAction.PlanRemovePlaylistsFromLibrary, "plan_remove_playlists_from_library",
            "Take playlists out of the user's library — Spotify's unfollow; there is no delete. Put "
            + "every playlist name they gave in arguments.playlistReferences. If they said to clear out "
            + "the empty ones without naming any, leave that empty and put their words in arguments.query "
            + "so the server can look up which playlists are actually empty."),

        new(SpotifyReadAction.ExplainCapability, "explain_capability",
            "The user is asking what this assistant can or cannot do, or why something is unavailable.")
    ];

    private static readonly Dictionary<string, SpotifyActionDefinition> ByWireName =
        Definitions.ToDictionary(d => d.WireName, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<SpotifyReadAction, SpotifyActionDefinition> ByAction =
        Definitions.ToDictionary(d => d.Action);

    public static IReadOnlyList<SpotifyActionDefinition> All => Definitions;

    public static SpotifyReadAction Parse(string? wireName) =>
        wireName != null && ByWireName.TryGetValue(wireName.Trim(), out var definition)
            ? definition.Action
            : SpotifyReadAction.Unknown;

    public static string WireNameOf(SpotifyReadAction action) =>
        ByAction.TryGetValue(action, out var definition) ? definition.WireName : "unknown";

    /// <summary>The action list handed to the model, built from the same table that dispatches.</summary>
    public static string PromptActionList() =>
        string.Join("\n", Definitions.Select(d => $"- {d.WireName}: {d.PromptDescription}"));

    /// <summary>
    /// Turns a raw envelope into something dispatchable, or explains what is
    /// missing. A high confidence score never substitutes for a required argument:
    /// the model being sure it understood is not evidence that it supplied a name.
    /// </summary>
    public static SpotifyValidatedCommand Validate(SpotifyCommandEnvelope? envelope)
    {
        if (envelope == null)
            return Unresolved("I could not understand that. Could you rephrase it?");

        if (envelope.SchemaVersion != SchemaVersion)
        {
            return Unresolved(
                "I could not understand that. Could you rephrase it?",
                confidence: envelope.Confidence);
        }

        var action = Parse(envelope.Action);
        var arguments = envelope.Arguments ?? new SpotifyCommandArguments();

        if (action == SpotifyReadAction.Unknown)
            return Unresolved(envelope.Clarification, envelope.Confidence);

        var definition = ByAction[action];

        if (definition.RequiresQuery && string.IsNullOrWhiteSpace(arguments.Query))
        {
            return Unresolved(
                action == SpotifyReadAction.FindPlaylists
                    ? "Which playlists should I look for? Tell me part of the name."
                    : action == SpotifyReadAction.SuggestMusic
                        ? "What musical theme, era, place, genre, or mood should I explore?"
                    : "What should I search for?",
                envelope.Confidence);
        }

        if (definition.RequiresPlaylistReference && string.IsNullOrWhiteSpace(arguments.PlaylistReference))
            return Unresolved("Which playlist do you mean?", envelope.Confidence);

        // Merge needs a real list. One name is not a merge, and no names at all is the
        // "just tidy it all up however you like" request the spec refuses outright.
        if (definition.RequiresPlaylistReferences && NamedPlaylists(arguments).Count < 2)
        {
            return Unresolved(
                "Which playlists should I merge? Name them and I will show you exactly what would happen "
                + "before anything changes.",
                envelope.Confidence);
        }

        return new SpotifyValidatedCommand(action, arguments, envelope.Confidence, envelope.Clarification);
    }

    /// <summary>
    /// Playlist names the user actually gave, from either argument shape. The
    /// singular field is folded in so "merge Road Trip and Road Trip 2" still counts
    /// as two when the model splits them across both.
    /// </summary>
    public static IReadOnlyList<string> NamedPlaylists(SpotifyCommandArguments arguments)
    {
        var names = (arguments.PlaylistReferences ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToList();

        if (!string.IsNullOrWhiteSpace(arguments.PlaylistReference))
            names.Add(arguments.PlaylistReference.Trim());

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SpotifyValidatedCommand Unresolved(string? clarification, double confidence = 0d) =>
        new(SpotifyReadAction.Unknown, new SpotifyCommandArguments(), confidence, clarification);
}
