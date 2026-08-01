using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyConversationService
{
    Task<SpotifyConversationResponse> HandleAsync(
        SpotifyConversationRequest request, CancellationToken token = default);
}

/// <summary>
/// Routes a validated action to its handler and writes the reply.
///
/// Every sentence the user reads about their Spotify data is composed here, from
/// values Spotify returned — the language model classifies intent and is never
/// asked to describe results. That is what makes counts trustworthy: a number in
/// the reply came from the API response, not from a model paraphrasing one.
/// </summary>
public sealed class SpotifyConversationService : ISpotifyConversationService
{
    private const int ItemPageSize = 50;

    private readonly ISpotifyCommandParser _parser;
    private readonly ISpotifyService _spotify;
    private readonly ISpotifyInventoryService _inventory;

    public SpotifyConversationService(
        ISpotifyCommandParser parser, ISpotifyService spotify, ISpotifyInventoryService inventory)
    {
        _parser = parser;
        _spotify = spotify;
        _inventory = inventory;
    }

    public async Task<SpotifyConversationResponse> HandleAsync(
        SpotifyConversationRequest request,
        CancellationToken token = default)
    {
        var command = await _parser.ParseAsync(request.Message, conversationContext: null, token);

        // A playlist the user picked from a disambiguation card wins over anything
        // re-derived from their words — they already answered that question.
        var pinnedPlaylistId = request.PlaylistId;

        return command.Action switch
        {
            SpotifyReadAction.SearchTracks => await SearchTracksAsync(command, token),
            SpotifyReadAction.ListPlaylists => await ListPlaylistsAsync(token),
            SpotifyReadAction.FindPlaylists => await FindPlaylistsAsync(command, token),
            SpotifyReadAction.InspectPlaylist => await InspectPlaylistAsync(command, pinnedPlaylistId, token),
            SpotifyReadAction.ListPlaylistItems => await ListItemsAsync(command, pinnedPlaylistId, request.Offset ?? 0, token),
            SpotifyReadAction.FindItemInPlaylists => await FindItemAsync(command, token),
            SpotifyReadAction.AnalyzeLibrary => await AnalyzeLibraryAsync(command, token),
            SpotifyReadAction.ComparePlaylists => await ComparePlaylistsAsync(command, token),
            SpotifyReadAction.GetTopItems => await TopItemsAsync(command, token),
            SpotifyReadAction.GetRecentPlaylistContexts => await RecentContextsAsync(token),
            SpotifyReadAction.ExplainCapability => ExplainCapability(command),
            _ => Unknown(command)
        };
    }

    // ─── Handlers ────────────────────────────────────────────────────────────

    private async Task<SpotifyConversationResponse> SearchTracksAsync(
        SpotifyValidatedCommand command, CancellationToken token)
    {
        var query = command.Arguments.Query!;
        var results = await _spotify.SearchTracksAsync(query, command.Arguments.Limit ?? 10, token);

        var message = results.Tracks.Count == 0
            ? $"Nothing on Spotify matched “{query}”."
            : $"Found {results.Total} {Plural(results.Total, "match", "matches")} for “{query}”. "
              + $"Showing the top {results.Tracks.Count}:";

        return Respond(command, message, results);
    }

    private async Task<SpotifyConversationResponse> ListPlaylistsAsync(CancellationToken token)
    {
        var playlists = await _spotify.GetUserPlaylistsAsync(token);
        return new SpotifyConversationResponse(
            SpotifyActionCatalog.WireNameOf(SpotifyReadAction.ListPlaylists),
            1.0,
            DescribeInventory(playlists),
            playlists);
    }

    private async Task<SpotifyConversationResponse> FindPlaylistsAsync(
        SpotifyValidatedCommand command, CancellationToken token)
    {
        var query = command.Arguments.Query!;
        var all = await _spotify.GetUserPlaylistsAsync(token);
        var matches = SpotifyPlaylistResolver.Filter(query, all);

        var message = matches.Count == 0
            ? $"None of your {all.Count} playlists have “{query}” in the name."
            : $"{matches.Count} of your {all.Count} {Plural(all.Count, "playlist", "playlists")} "
              + $"{Plural(matches.Count, "matches", "match")} “{query}”:";

        return Respond(command, message, matches);
    }

    private async Task<SpotifyConversationResponse> InspectPlaylistAsync(
        SpotifyValidatedCommand command, string? pinnedPlaylistId, CancellationToken token)
    {
        var (resolution, playlists) = await ResolveAsync(command, pinnedPlaylistId, token);

        if (resolution.Kind != SpotifyPlaylistMatchKind.Resolved)
            return DescribeResolutionFailure(command, resolution, playlists.Count);

        var playlist = resolution.Playlist!;
        return Respond(command, DescribePlaylist(playlist), playlist);
    }

    private async Task<SpotifyConversationResponse> ListItemsAsync(
        SpotifyValidatedCommand command, string? pinnedPlaylistId, int offset, CancellationToken token)
    {
        var (resolution, playlists) = await ResolveAsync(command, pinnedPlaylistId, token);

        if (resolution.Kind != SpotifyPlaylistMatchKind.Resolved)
            return DescribeResolutionFailure(command, resolution, playlists.Count);

        var playlist = resolution.Playlist!;
        var page = await _spotify.GetPlaylistItemsAsync(playlist.Id, offset, ItemPageSize, token);

        var message = page.Access switch
        {
            SpotifyContentsAccess.Forbidden =>
                $"Spotify will not let me read what is inside “{playlist.Name}”. "
                + "That happens with playlists you follow but do not own or collaborate on — "
                + "it shows you the playlist, but not its contents.",

            SpotifyContentsAccess.Unavailable =>
                $"Spotify did not return the contents of “{playlist.Name}”. "
                + "That is not the same as it being empty — I simply cannot see inside it.",

            _ when page.Total == 0 =>
                $"“{playlist.Name}” really is empty — Spotify reports 0 items in it.",

            _ => DescribeItemPage(playlist, page)
        };

        return Respond(command, message, page);
    }

    private async Task<SpotifyConversationResponse> RecentContextsAsync(CancellationToken token)
    {
        var contexts = await _spotify.GetRecentPlaylistContextsAsync(token);
        var playlists = await _spotify.GetUserPlaylistsAsync(token);

        // Spotify's recently-played payload carries the playlist URI but not its
        // name, so join against the inventory rather than showing a bare ID.
        var named = contexts
            .Select(context => context with
            {
                Name = playlists.FirstOrDefault(p => p.Id == context.PlaylistId)?.Name
            })
            .ToList();

        var message = named.Count == 0
            ? "Spotify returned no playlist plays in the recent history it exposes. That window is "
              + "short and leaves out private sessions, so it is not evidence that you have not been listening."
            : $"The playlist that appears most often in the recent history Spotify returned is "
              + $"“{named[0].Name ?? named[0].PlaylistId}”, with {named[0].ObservedPlays} "
              + $"{Plural(named[0].ObservedPlays, "play", "plays")} observed. This is an approximation — "
              + "Spotify does not expose lifetime play counts per playlist.";

        return new SpotifyConversationResponse(
            SpotifyActionCatalog.WireNameOf(SpotifyReadAction.GetRecentPlaylistContexts),
            1.0, message, named);
    }

    private static SpotifyConversationResponse ExplainCapability(SpotifyValidatedCommand command) =>
        Respond(command,
            """
            Right now I can read your Spotify library, but not change it:

            - List your playlists, or filter them by name
            - Describe one playlist — owner, visibility, item count, whether I can read inside it
            - List the songs and episodes in a playlist you own or collaborate on
            - Search Spotify's catalog
            - Tell you which playlists show up most in recent listening history

            I cannot create, rename, merge, or delete anything yet. Those arrive with the
            reviewed change-plan flow, where you see exactly what will happen before it does.

            Two things worth knowing: a playlist you only follow may show its details but not
            its contents — Spotify's rule, not mine — and I will always say so rather than
            reporting it as empty. And Spotify has no way to truly delete a playlist; it only
            removes it from your own library.
            """,
            data: null);

    private static SpotifyConversationResponse Unknown(SpotifyValidatedCommand command) =>
        Respond(command,
            command.Clarification
            ?? "I am not sure what you are asking for. Try “show my playlists”, "
               + "“what is in <playlist name>”, or ask what I can do.",
            data: null);

    private async Task<SpotifyConversationResponse> FindItemAsync(
        SpotifyValidatedCommand command, CancellationToken token)
    {
        var query = command.Arguments.Query!;
        var library = await ReadLibraryAsync(token);
        var needle = SpotifyPlaylistResolver.Normalize(query);

        var hits = library
            .Where(c => c.IsReadable)
            .Select(c => (c.Playlist, Matches: c.Items
                .Where(i => SpotifyPlaylistResolver.Normalize($"{i.Name} {i.Artists}").Contains(needle, StringComparison.Ordinal))
                .ToList()))
            .Where(x => x.Matches.Count > 0)
            .ToList();

        var unreadable = library.Count(c => !c.IsReadable);
        var caveat = unreadable == 0
            ? ""
            : $" I could not read {unreadable} playlist(s), so it may also be in one of those.";

        var message = hits.Count == 0
            ? $"I did not find “{query}” in any playlist I can read.{caveat}"
            : $"“{query}” appears in {hits.Count} {Plural(hits.Count, "playlist", "playlists")}:{caveat}";

        return Respond(command, message, hits.Select(h => h.Playlist).ToList());
    }

    private async Task<SpotifyConversationResponse> AnalyzeLibraryAsync(
        SpotifyValidatedCommand command, CancellationToken token)
    {
        var library = await ReadLibraryAsync(token);
        var analysis = SpotifyAnalysis.Analyze(library);

        return Respond(command, DescribeAnalysis(analysis), analysis);
    }

    private async Task<SpotifyConversationResponse> ComparePlaylistsAsync(
        SpotifyValidatedCommand command, CancellationToken token)
    {
        var names = SplitPair(command.Arguments.Query!);
        if (names == null)
            return Respond(command, "Which two playlists should I compare? Name both, like “A and B”.", null);

        var playlists = await _spotify.GetUserPlaylistsAsync(token);
        var left = SpotifyPlaylistResolver.Resolve(names.Value.Left, playlists);
        var right = SpotifyPlaylistResolver.Resolve(names.Value.Right, playlists);

        if (left.Kind != SpotifyPlaylistMatchKind.Resolved)
            return DescribeResolutionFailure(command with { Arguments = command.Arguments with { PlaylistReference = names.Value.Left } }, left, playlists.Count);

        if (right.Kind != SpotifyPlaylistMatchKind.Resolved)
            return DescribeResolutionFailure(command with { Arguments = command.Arguments with { PlaylistReference = names.Value.Right } }, right, playlists.Count);

        var contents = await _inventory.GetAllContentsAsync([left.Playlist!, right.Playlist!], token);

        var unreadable = contents.Where(c => !c.IsReadable).ToList();
        if (unreadable.Count > 0)
        {
            return Respond(command,
                $"I cannot compare these — Spotify will not let me read the contents of "
                + $"“{unreadable[0].Playlist.Name}”.", null);
        }

        var overlaps = SpotifyAnalysis.FindOverlaps(contents, nearDuplicateThreshold: 0);
        if (overlaps.Count == 0)
        {
            return Respond(command,
                $"“{left.Playlist!.Name}” and “{right.Playlist!.Name}” have no songs in common.", null);
        }

        return Respond(command, DescribeOverlap(overlaps[0]), overlaps[0]);
    }

    private async Task<SpotifyConversationResponse> TopItemsAsync(
        SpotifyValidatedCommand command, CancellationToken token)
    {
        var kind = SpotifyPlaylistResolver.Normalize(command.Arguments.Query).Contains("artist", StringComparison.Ordinal)
            ? "artists"
            : "tracks";

        var top = await _spotify.GetTopItemsAsync(
            kind, command.Arguments.TimeRange ?? "medium_term", command.Arguments.Limit ?? 20, token);

        var window = top.TimeRange switch
        {
            "short_term" => "the last four weeks or so",
            "long_term" => "the last several years",
            _ => "the last six months or so"
        };

        var message = top.Items.Count == 0
            ? $"Spotify returned no top {top.Kind} for {window}."
            : $"Your most-played {top.Kind} over {window}, as Spotify ranks them:";

        return Respond(command, message, top);
    }

    // ─── Shared steps ────────────────────────────────────────────────────────

    private async Task<(SpotifyPlaylistResolution Resolution, IReadOnlyList<SpotifyPlaylistDto> Playlists)>
        ResolveAsync(SpotifyValidatedCommand command, string? pinnedPlaylistId, CancellationToken token)
    {
        var playlists = await _spotify.GetUserPlaylistsAsync(token);

        if (!string.IsNullOrWhiteSpace(pinnedPlaylistId))
        {
            var pinned = playlists.FirstOrDefault(p => p.Id == pinnedPlaylistId);
            if (pinned != null)
                return (SpotifyPlaylistResolution.Resolved(pinned, "selected"), playlists);
        }

        return (SpotifyPlaylistResolver.Resolve(command.Arguments.PlaylistReference, playlists), playlists);
    }

    private static SpotifyConversationResponse DescribeResolutionFailure(
        SpotifyValidatedCommand command, SpotifyPlaylistResolution resolution, int inventorySize)
    {
        var reference = command.Arguments.PlaylistReference;

        if (resolution.Kind == SpotifyPlaylistMatchKind.Ambiguous)
        {
            return Respond(command,
                $"{resolution.Candidates.Count} of your playlists match “{reference}”. Which one?",
                resolution.Candidates);
        }

        return Respond(command,
            $"I could not find a playlist matching “{reference}” among your {inventorySize}.",
            data: null);
    }

    private static string DescribeInventory(IReadOnlyList<SpotifyPlaylistDto> playlists)
    {
        if (playlists.Count == 0)
            return "Spotify returned no playlists for your account.";

        var owned = playlists.Count(p => p.IsOwnedByUser);
        var collaborative = playlists.Count(p => !p.IsOwnedByUser && p.IsCollaborative);
        var followed = playlists.Count - owned - collaborative;
        var unreadable = playlists.Count(p => !p.ContentsAvailable);

        var summary = $"You have {playlists.Count} {Plural(playlists.Count, "playlist", "playlists")}: "
            + $"{owned} you own, {collaborative} collaborative, {followed} followed.";

        return unreadable == 0
            ? summary
            : summary + $" Spotify did not report contents for {unreadable} of them, "
              + $"so {Plural(unreadable, "its item count is", "their item counts are")} unknown rather than zero.";
    }

    private static string DescribePlaylist(SpotifyPlaylistDto playlist)
    {
        var ownership = playlist.IsOwnedByUser
            ? "You own it"
            : playlist.IsCollaborative
                ? $"It belongs to {playlist.OwnerName ?? "someone else"} and you collaborate on it"
                : $"You follow it; {playlist.OwnerName ?? "someone else"} owns it";

        var visibility = playlist.IsPublic switch
        {
            true => "public",
            false => "private",
            null => "of unstated visibility"
        };

        var count = playlist.ContentsAvailable && playlist.TrackCount.HasValue
            ? $"{playlist.TrackCount} {Plural(playlist.TrackCount.Value, "item", "items")}"
            : "an unknown number of items — Spotify did not report its contents";

        return $"“{playlist.Name}” — {ownership}. It is {visibility} and has {count}.";
    }

    private static string DescribeItemPage(SpotifyPlaylistDto playlist, SpotifyPlaylistItemsPageDto page)
    {
        var shown = page.Items.Count;
        var first = page.Offset + 1;
        var last = page.Offset + shown;

        var message = page.Total > shown
            ? $"“{playlist.Name}” has {page.Total} items. Showing {first}–{last}:"
            : $"“{playlist.Name}” has {page.Total} {Plural(page.Total, "item", "items")}:";

        var episodes = page.Items.Count(i => i.Kind == SpotifyItemKind.Episode);
        var local = page.Items.Count(i => i.Kind == SpotifyItemKind.Local);
        var unavailable = page.Items.Count(i => i.Kind == SpotifyItemKind.Unavailable);

        var notes = new List<string>();
        if (episodes > 0) notes.Add($"{episodes} podcast {Plural(episodes, "episode", "episodes")}");
        if (local > 0) notes.Add($"{local} local {Plural(local, "file", "files")}");
        if (unavailable > 0) notes.Add($"{unavailable} no longer available on Spotify");

        return notes.Count == 0 ? message : $"{message} ({string.Join(", ", notes)})";
    }

    private async Task<IReadOnlyList<SpotifyPlaylistContents>> ReadLibraryAsync(CancellationToken token)
    {
        var playlists = await _spotify.GetUserPlaylistsAsync(token);
        return await _inventory.GetAllContentsAsync(playlists, token);
    }

    /// <summary>"A and B" → the two names. Splits on the last " and " so a playlist
    /// called "Rock and Roll" survives being the first half.</summary>
    public static (string Left, string Right)? SplitPair(string query)
    {
        const string separator = " and ";
        var index = query.LastIndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (index <= 0)
            return null;

        var left = query[..index].Trim();
        var right = query[(index + separator.Length)..].Trim();

        return left.Length == 0 || right.Length == 0 ? null : (left, right);
    }

    private static string DescribeAnalysis(SpotifyLibraryAnalysis analysis)
    {
        var lines = new List<string>
        {
            $"I read {analysis.PlaylistsRead} of your {analysis.PlaylistsScanned} playlists."
        };

        if (analysis.Unreadable.Count > 0)
        {
            lines.Add($"{analysis.Unreadable.Count} could not be read — everything below excludes "
                    + "them, so treat this as a partial picture.");
        }

        lines.Add("");
        lines.Add(analysis.Empty.Count == 0
            ? "No empty playlists."
            : $"{analysis.Empty.Count} empty {Plural(analysis.Empty.Count, "playlist", "playlists")}: "
              + string.Join(", ", analysis.Empty.Take(10).Select(e => $"“{e.Name}”"))
              + (analysis.Empty.Count > 10 ? ", and more" : ""));

        var exact = analysis.DuplicateItems.Count(d => d.Confidence == SpotifyDuplicateConfidence.Exact);
        var probable = analysis.DuplicateItems.Count - exact;
        lines.Add(analysis.DuplicateItems.Count == 0
            ? "No repeated songs inside any playlist."
            : $"{exact} exact repeat(s) — the same song twice in one playlist — and {probable} "
              + "probable repeat(s) where the title and artist match but the recording might differ.");

        var identical = analysis.OverlappingPlaylists.Count(o => o.Identical);
        var supersets = analysis.OverlappingPlaylists.Count(o => o.SupersetOf != null);
        var near = analysis.OverlappingPlaylists.Count - identical - supersets;
        lines.Add(analysis.OverlappingPlaylists.Count == 0
            ? "No playlists substantially overlap."
            : $"{identical} identical pair(s), {supersets} where one playlist fully contains another, "
              + $"and {near} that overlap heavily without matching.");

        if (analysis.NamingCollisions.Count > 0)
        {
            lines.Add($"{analysis.NamingCollisions.Count} set(s) of playlists whose names differ only by "
                    + "punctuation or case — worth renaming so I can tell them apart.");
        }

        lines.Add("");
        lines.Add("I have not changed anything. Making changes needs the reviewed plan flow, which is not built yet.");

        return string.Join("\n", lines);
    }

    private static string DescribeOverlap(SpotifyPlaylistOverlap overlap)
    {
        if (overlap.Identical)
        {
            return $"“{overlap.LeftName}” and “{overlap.RightName}” contain exactly the same "
                 + $"{overlap.SharedItems} {Plural(overlap.SharedItems, "song", "songs")}.";
        }

        var superset = overlap.SupersetOf == overlap.RightId ? overlap.LeftName
            : overlap.SupersetOf == overlap.LeftId ? overlap.RightName
            : null;

        if (superset != null)
        {
            var contained = superset == overlap.LeftName ? overlap.RightName : overlap.LeftName;
            return $"“{superset}” contains everything in “{contained}” — all {overlap.SharedItems} of "
                 + $"its songs — plus {Math.Max(overlap.LeftOnlyItems, overlap.RightOnlyItems)} more.";
        }

        return $"“{overlap.LeftName}” and “{overlap.RightName}” share {overlap.SharedItems} "
             + $"{Plural(overlap.SharedItems, "song", "songs")} ({overlap.Overlap:P0} overlap). "
             + $"{overlap.LeftOnlyItems} only in the first, {overlap.RightOnlyItems} only in the second.";
    }

    private static SpotifyConversationResponse Respond(
        SpotifyValidatedCommand command, string message, object? data) =>
        new(SpotifyActionCatalog.WireNameOf(command.Action), command.Confidence, message, data,
            command.Action == SpotifyReadAction.Unknown ? command.Clarification : null);

    private static string Plural(int count, string singular, string plural) =>
        count == 1 ? singular : plural;
}
