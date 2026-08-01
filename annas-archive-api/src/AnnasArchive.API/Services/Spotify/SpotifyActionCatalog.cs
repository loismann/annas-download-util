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
    ExplainCapability
}

public sealed record SpotifyActionDefinition(
    SpotifyReadAction Action,
    string WireName,
    string PromptDescription,
    bool RequiresQuery = false,
    bool RequiresPlaylistReference = false
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
                    : "What should I search for?",
                envelope.Confidence);
        }

        if (definition.RequiresPlaylistReference && string.IsNullOrWhiteSpace(arguments.PlaylistReference))
            return Unresolved("Which playlist do you mean?", envelope.Confidence);

        return new SpotifyValidatedCommand(action, arguments, envelope.Confidence, envelope.Clarification);
    }

    private static SpotifyValidatedCommand Unresolved(string? clarification, double confidence = 0d) =>
        new(SpotifyReadAction.Unknown, new SpotifyCommandArguments(), confidence, clarification);
}
